using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Modules.Mail.Settings;
using EmailSslSender.Modules.Mail.Templates;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Постановка письма в очередь: транзакция вызывающего, отказы без исключений, предел частоты.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Главный факт файла — письмо исчезает вместе с откатом чужой транзакции.</b> Письмо
/// «пароль сменён» не должно существовать, если сама смена откатилась; проверяется это на обеих
/// ветках, потому что «строка есть» и «строка исчезает» ломаются по-разному.
/// </remarks>
[Collection(MailDatabaseCollection.Name)]
public sealed class QueueTests(MailDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        база.Настройки.Значения.Clear();
        база.Логи.Строки.Clear();
        Настроить();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        await db.Set<MailOutbox>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Письмо_ложится_в_очередь_с_фиксацией_вызывающего()
    {
        var адрес = Адрес();

        await using (var scope = база.Scope())
        {
            var (очередь, db) = Слои(scope);

            (await очередь.EnqueueAsync(адрес, MailKinds.Test, MailLocales.Ru, Пусто, Ct))
                .ShouldBe(MailQueueResult.Queued);

            await db.SaveChangesAsync(Ct);
        }

        var письма = await Письма(адрес);

        письма.Count.ShouldBe(1);
        письма[0].Subject.ShouldBe("EAMS ssl sender: проверка почты");
        письма[0].SentAt.ShouldBeNull();
        письма[0].Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Откат_чужой_транзакции_уносит_письмо()
    {
        // ⚠ ⚠ Ради этого постановка не зовёт `SaveChangesAsync`. Письмо «пароль сменён» не
        // должно пережить откат самой смены — а с отдельной фиксацией пережило бы.
        var адрес = Адрес();

        await using (var scope = база.Scope())
        {
            var (очередь, db) = Слои(scope);

            await очередь.EnqueueAsync(адрес, MailKinds.Test, MailLocales.Ru, Пусто, Ct);

            // Единица работы уходит в мусор, не будучи сохранённой, — так выглядит откат.
            db.ChangeTracker.Clear();
        }

        (await Письма(адрес)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Тело_письма_лежит_запечатанным()
    {
        var адрес = Адрес();

        await using (var scope = база.Scope())
        {
            var (очередь, db) = Слои(scope);
            await очередь.EnqueueAsync(адрес, MailKinds.Test, MailLocales.Ru, Пусто, Ct);
            await db.SaveChangesAsync(Ct);
        }

        var письмо = (await Письма(адрес))[0];

        // ⚠ Решение 0029: в теле живут одноразовый код и ссылка сброса. Дамп базы иначе отдаёт
        // действующие ссылки всех писем, которые ещё не ушли.
        база.Конверт
            .OpenText(new WrappedDataKey(письмо.DataKey), new SealedSecret(письмо.SealedBody), "sender:mail-body")
            .ShouldContain("настроена верно");

        // А тема открыта: секрета в ней нет, и при разборе она отвечает на первый вопрос.
        письмо.Subject.ShouldBe("EAMS ssl sender: проверка почты");
    }

    // --- отказы -----------------------------------------------------------------------------

    [Fact]
    public async Task Ненастроенная_почта_не_роняет_чужое_действие()
    {
        база.Настройки.Значения[MailSettings.Enabled.Name] = false;

        var адрес = Адрес();

        await using var scope = база.Scope();
        var (очередь, _) = Слои(scope);

        // ⚠ Возвращает, а не бросает. В эталоне это исключение, и отправка кода входа его не
        // ловит — то есть ненастроенная почта уронила бы вход.
        (await очередь.EnqueueAsync(адрес, MailKinds.Test, MailLocales.Ru, Пусто, Ct))
            .ShouldBe(MailQueueResult.NotConfigured);

        (await очередь.CanSendAsync(Ct)).ShouldBeFalse();
        (await Письма(адрес)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Стёртый_адрес_сервера_означает_ненастроенную_почту()
    {
        // ⚠ `Configured` ВЫЧИСЛЯЕТСЯ, а не хранится настройкой. Отдельный признак разошёлся бы
        // с действительностью ровно тогда, когда администратор стёр адрес сервера, не сняв
        // галочку: система считала бы себя настроенной и предлагала бы код письмом.
        база.Настройки.Значения[MailSettings.Enabled.Name] = true;
        база.Настройки.Значения[MailSettings.SmtpHost.Name] = null;

        await using var scope = база.Scope();
        var (очередь, _) = Слои(scope);

        (await очередь.CanSendAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Отказ_не_печатает_адрес_получателя_в_лог()
    {
        // ⚠ Сам факт «на этот адрес просили сброс» является сведением о человеке, а строка
        // уезжает в общий лог приложения (правило 14). Вид письма при этом нужен: без него
        // строка не отвечает ни на один вопрос.
        база.Настройки.Значения[MailSettings.Enabled.Name] = false;

        var адрес = Адрес();

        await using var scope = база.Scope();
        var (очередь, _) = Слои(scope);

        await очередь.EnqueueAsync(адрес, MailKinds.Test, MailLocales.Ru, Пусто, Ct);

        база.Логи.Строки.ShouldContain(с => с.Contains(MailKinds.Test, StringComparison.Ordinal));
        база.Логи.Строки.ShouldNotContain(с => с.Contains(адрес, StringComparison.OrdinalIgnoreCase));
    }

    // --- предел частоты ---------------------------------------------------------------------

    [Fact]
    public async Task Предел_частоты_считает_ПОСТАВЛЕННЫЕ_а_не_отправленные()
    {
        // ⚠ ⚠ Счёт по `sent_at` означал бы, что неработающий SMTP снимает ограничение: очередь
        // копится без счёта, а при починке человек получает всё разом — то есть предел
        // перестаёт работать ровно тогда, когда он нужнее всего. Ни одно письмо здесь не
        // отправлено, и предел обязан сработать всё равно.
        база.Настройки.Значения[MailSettings.RatePerHour.Name] = 2;

        var адрес = Адрес();

        (await Поставить(адрес, MailKinds.Test)).ShouldBe(MailQueueResult.Queued);
        (await Поставить(адрес, MailKinds.Test)).ShouldBe(MailQueueResult.Queued);
        (await Поставить(адрес, MailKinds.Test)).ShouldBe(MailQueueResult.RateLimited);

        (await Письма(адрес)).Count.ShouldBe(2);
        (await Письма(адрес)).ShouldAllBe(п => п.SentAt == null);
    }

    [Fact]
    public async Task Предел_считается_по_паре_адрес_и_вид()
    {
        // ⚠ Поток писем одного вида не должен съедать квоту сброса пароля: иначе перебором
        // приглашений закрывают человеку восстановление доступа.
        база.Настройки.Значения[MailSettings.RatePerHour.Name] = 1;

        var адрес = Адрес();

        (await Поставить(адрес, MailKinds.Test)).ShouldBe(MailQueueResult.Queued);
        (await Поставить(адрес, MailKinds.Test)).ShouldBe(MailQueueResult.RateLimited);

        // Другой вид на тот же адрес — своя квота.
        (await Поставить(адрес, "чужой_вид")).ShouldBe(MailQueueResult.Queued);
    }

    [Fact]
    public async Task Предел_считается_по_окну_а_не_за_всё_время()
    {
        база.Настройки.Значения[MailSettings.RatePerHour.Name] = 1;

        var адрес = Адрес();

        (await Поставить(адрес, MailKinds.Test)).ShouldBe(MailQueueResult.Queued);
        (await Поставить(адрес, MailKinds.Test)).ShouldBe(MailQueueResult.RateLimited);

        база.Часы.Вперёд(TimeSpan.FromHours(2));

        (await Поставить(адрес, MailKinds.Test)).ShouldBe(MailQueueResult.Queued);
    }

    [Fact]
    public async Task Разные_адреса_не_делят_квоту()
    {
        база.Настройки.Значения[MailSettings.RatePerHour.Name] = 1;

        var первый = Адрес();
        var второй = Адрес();

        (await Поставить(первый, MailKinds.Test)).ShouldBe(MailQueueResult.Queued);
        (await Поставить(второй, MailKinds.Test)).ShouldBe(MailQueueResult.Queued);
    }

    // --- обвязка ----------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> Пусто =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Свой адрес на тест: таблица живёт весь прогон коллекции.</summary>
    private static string Адрес() => $"кому-{Guid.NewGuid():N}@example.com";

    private void Настроить()
    {
        база.Настройки.Значения[MailSettings.Enabled.Name] = true;
        база.Настройки.Значения[MailSettings.SmtpHost.Name] = "smtp.example.com";
        // ⚠ Вскрытие 44: без адреса отправителя почта НЕ считается настроенной —
        // прежде он угадывался из учётной записи SMTP, и угадывался неверно.
        база.Настройки.Значения[MailSettings.FromAddress.Name] = "vault@example.com";
    }

    private async Task<MailQueueResult> Поставить(string адрес, string вид)
    {
        await using var scope = база.Scope();
        var (очередь, db) = Слои(scope);

        // ⚠ Чужой вид отрисовать нечем — подставляем справочник теста только там, где вид свой.
        var итог = вид == MailKinds.Test
            ? await очередь.EnqueueAsync(адрес, вид, MailLocales.Ru, Пусто, Ct)
            : await ПоставитьЧужой(scope, адрес, вид);

        await db.SaveChangesAsync(Ct);

        return итог;
    }

    /// <summary>
    /// Чужой вид ставится напрямую строкой: справочник его не знает, а проверяется здесь
    /// счётчик предела, а не отрисовка.
    /// </summary>
    private async Task<MailQueueResult> ПоставитьЧужой(AsyncServiceScope scope, string адрес, string вид)
    {
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var ключ = база.Конверт.CreateDataKey();

        db.Set<MailOutbox>().Add(new MailOutbox
        {
            Id = Guid.CreateVersion7(),
            ToAddress = адрес,
            Subject = "чужое",
            SealedBody = база.Конверт.SealText(ключ, "тело", "sender:mail-body").Bytes,
            DataKey = ключ.Bytes,
            Kind = вид,
            CreatedAt = база.Часы.GetUtcNow(),
            NextAttemptAt = база.Часы.GetUtcNow(),
        });

        await Task.CompletedTask;
        return MailQueueResult.Queued;
    }

    private async Task<List<MailOutbox>> Письма(string адрес)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Set<MailOutbox>()
            .AsNoTracking()
            .Where(п => п.ToAddress == адрес)
            .OrderBy(п => п.CreatedAt)
            .ToListAsync(Ct);
    }

    private static (IMailQueue, SenderDbContext) Слои(AsyncServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<IMailQueue>(),
         scope.ServiceProvider.GetRequiredService<SenderDbContext>());
}
