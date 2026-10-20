using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Письма модуля личностей: постановка в той же транзакции и ссылка в ответе.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Главный факт файла — ссылка в теле ответа появляется ТОЛЬКО когда письмо не ушло.</b>
/// Ушло — секрет уехал по своему каналу, и второй раз показывать его в ответе API незачем: тело
/// попадает в лог обратного прокси, в историю вкладки и в отчёт об ошибке (решение 0031).
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class MailNoticeTests(IdentityDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        база.Почта.Письма.Clear();
        база.Почта.Настроена = true;
        база.Почта.Исход = MailQueueResult.Queued;
        база.Язык.Язык = MailLocales.Ru;
        база.Адрес.Адрес = "https://sender.example.com";
        база.Actor.Id = Guid.CreateVersion7();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Вернуть подставную почту в исходное — «не настроена».
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Обязательно, и стоило трёх красных тестов в чужом файле.</b> Обвязка одна на
    /// коллекцию: оставленная настроенной почта заставляет приглашение уходить письмом, и
    /// `UserLifecycleTests`, проверяющий возвращённую ссылку, краснеет — при том что про почту
    /// он ничего не знает. Ровно то же заражение, что чинили обвязкой настроек (этап 16.6),
    /// только здесь заражает не таблица, а поле подделки.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        база.Почта.Письма.Clear();
        база.Почта.Настроена = false;
        база.Почта.Исход = MailQueueResult.NotConfigured;
        база.Адрес.Адрес = null;

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Заведение_ставит_приглашение_и_НЕ_отдаёт_ссылку()
    {
        var (человек, приглашение) = await Завести();

        база.Почта.Письма.ShouldContain(п => п.Kind == "invitation" && п.To == человек.Email);

        // ⚠ Ссылки в ответе нет: она ушла письмом.
        приглашение.ShouldBeNull();
    }

    [Fact]
    public async Task Ненастроенная_почта_ВОЗВРАЩАЕТ_ссылку()
    {
        // ⚠ Зеркальный факт, и без него первый доказывал бы только «ссылки не бывает никогда».
        // Администратор уже вошёл и имеет право; не дав ему ссылку, мы оставили бы заведённого
        // человека недостижимым.
        база.Почта.Исход = MailQueueResult.NotConfigured;

        var (_, приглашение) = await Завести();

        приглашение.ShouldNotBeNull();
        приглашение.Token.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Незаданный_внешний_адрес_тоже_возвращает_ссылку()
    {
        // ⚠ Ссылка в письме строится ТОЛЬКО из заданного настройкой адреса: `Host`
        // подделывается, и письмо уехало бы вместе с токеном на сервер атакующего. Адреса нет —
        // письмо не ставится, и ссылку показывают администратору.
        база.Адрес.Адрес = null;

        var (_, приглашение) = await Завести();

        приглашение.ShouldNotBeNull();
        база.Почта.Письма.ShouldBeEmpty("без внешнего адреса письмо не ставится вовсе");
    }

    [Fact]
    public async Task Язык_письма_берётся_у_ПОЛУЧАТЕЛЯ()
    {
        // ⚠ Ради этого заведён `IUserLocale`: письмо уходит не тому, кто в запросе, и
        // `ISettingsReader` на вопрос «какой язык у вот этого» не отвечает.
        база.Язык.Язык = MailLocales.Uk;

        await Завести();

        база.Почта.Письма.ShouldContain(п => п.Locale == MailLocales.Uk);
    }

    [Fact]
    public async Task Административный_сброс_ставит_письмо_и_прячет_ссылку()
    {
        var (человек, _) = await Завести();

        база.Почта.Письма.Clear();

        var сброс = await Служба(scope => scope.IssuePasswordResetAsync(человек.Id, Ct));

        база.Почта.Письма.ShouldContain(п => п.Kind == "password_reset");
        сброс.Token.ShouldBeEmpty("письмо ушло — ссылку второй раз не показываем");
    }

    [Fact]
    public async Task Отключение_ставит_письмо_о_приостановке()
    {
        // ⚠ Форма входа причину не называет — она отвечает одинаково на все случаи, чтобы не
        // подсказывать посторонним, какие адреса существуют. Письмо — единственный способ
        // узнать, что доступ приостановлен.
        var (человек, _) = await Завести();

        база.Почта.Письма.Clear();

        await Служба(scope => scope.DisableAsync(человек.Id, Ct));

        база.Почта.Письма.ShouldContain(п => п.Kind == "account_suspended" && п.To == человек.Email);
    }

    [Fact]
    public async Task Публичный_запрос_на_несуществующий_адрес_молчит()
    {
        await Служба(async scope =>
        {
            await scope.RequestPasswordResetAsync($"нет-{Guid.NewGuid():N}@example.com", Ct);
            return 0;
        });

        база.Почта.Письма.ShouldBeEmpty();
    }

    /// <summary>
    /// Холостой Argon2 на чужой адрес не держит транзакцию и соединение базы.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Найдено аудитами надёжности и скорости 2026-10-19 (решение 0095). Адрес публичный, а
    /// холостая проверка — сотни миллисекунд и 64 МиБ: внутри транзакции каждый такой запрос
    /// держал соединение пула «idle in transaction», пока стоял в очереди к Argon2. Поток
    /// запросов на выдуманные адреса с разных машин выедал пул в сорок соединений, и вставало
    /// всё API.
    /// </remarks>
    // мутация 2026-10-19: вернул холостую проверку внутрь транзакции → покраснел этот факт.
    [Fact]
    public async Task Холостая_проверка_не_держит_транзакцию()
    {
        long? висящих = null;

        база.Хешер.ПередХолостой = async () =>
        {
            await using var связь = new Npgsql.NpgsqlConnection(база.ConnectionString);
            await связь.OpenAsync(Ct);

            await using var запрос = new Npgsql.NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity "
                + "WHERE datname = current_database() AND state LIKE 'idle in transaction%'",
                связь);

            висящих = (long)(await запрос.ExecuteScalarAsync(Ct))!;
        };

        try
        {
            await Служба(async scope =>
            {
                await scope.RequestPasswordResetAsync($"нет-{Guid.NewGuid():N}@example.com", Ct);
                return 0;
            });
        }
        finally
        {
            база.Хешер.ПередХолостой = null;
        }

        висящих.ShouldNotBeNull("холостой проверки не было — проверяется не тот путь");
        висящих.ShouldBe(0, "на время Argon2 соединение держит открытую транзакцию");
    }

    [Fact]
    public async Task Публичный_запрос_на_существующий_адрес_ставит_письмо()
    {
        var (человек, _) = await Завести();

        база.Почта.Письма.Clear();

        await Служба(async scope =>
        {
            await scope.RequestPasswordResetAsync(человек.Email, Ct);
            return 0;
        });

        база.Почта.Письма.ShouldContain(п => п.Kind == "password_reset" && п.To == человек.Email);
    }

    [Fact]
    public async Task Публичный_запрос_при_ненастроенной_почте_НЕ_оставляет_ссылки()
    {
        // ⚠ ⚠ Ссылка, которую никто не получит, существовать не должна: просящий не вошёл, и
        // показать её ему нельзя. Транзакция откатывается целиком.
        var (человек, _) = await Завести();

        база.Почта.Исход = MailQueueResult.NotConfigured;

        var было = await Ссылок(человек.Id);

        await Служба(async scope =>
        {
            await scope.RequestPasswordResetAsync(человек.Email, Ct);
            return 0;
        });

        (await Ссылок(человек.Id)).ShouldBe(было, "выданная ссылка откатилась вместе с транзакцией");
    }

    // --- обвязка ----------------------------------------------------------------------------

    private async Task<(Domain.User User, Invitation? Invitation)> Завести()
    {
        return await Служба(scope => scope.CreateAsync(
            new UserDraft($"кому-{Guid.NewGuid():N}@example.com", "Кто-то", null, false), Ct));
    }

    private async Task<T> Служба<T>(Func<UserService, Task<T>> что)
    {
        await using var scope = база.Scope();

        return await что(scope.ServiceProvider.GetRequiredService<UserService>());
    }

    /// <summary>Сколько живых ссылок у человека — им проверяется откат.</summary>
    private async Task<int> Ссылок(Guid userId)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        var значения = await db.Database
            .SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM identity_tokens WHERE user_id = {0}",
                userId)
            .ToListAsync(Ct);

        return значения[0];
    }
}
