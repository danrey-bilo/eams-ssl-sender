using EmailSslSender.Modules.Tokens.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Вход, разорванный на два модуля, остался одним действием (этап 10.5.5).
/// </summary>
/// <remarks>
/// ⚠ <b>Главный тест подэтапа, и вот почему.</b> До 10.5 вход выдавал токены сам, и
/// атомарность держалась тем, что всё происходило в одном методе. Теперь пароль проверяет
/// один модуль, а токены выдаёт другой — и если сосед сорвётся посередине, легко получить
/// систему, где счётчик неудач обнулён, а войти нечем. Или наоборот: токен в базе живой, а
/// система при мнении, что человек не входил.
///
/// Правило 15 говорит про это прямо: не сохранилось действие — не должно сохраниться и
/// упоминание о нём. Разрыв по модулям ничего не меняет: транзакция одна, потому что
/// контекст базы общий (<c>SenderDbContext</c>, названное исключение правила 19).
///
/// ⚠ <b>Чего здесь НЕТ и почему.</b> Пункт плана «в базе нет строки <c>auth.login</c>» этот
/// сьют проверить не может, и притворяться обратным было бы худшим видом зелёного. Обвязка
/// подменяет журнал на <see cref="RecordingAudit"/> — список в памяти, который пополняется в
/// момент вызова <c>Write</c>, то есть <b>до</b> того, как решится судьба транзакции.
/// Откатившаяся транзакция оставляет запись в списке ровно так же, как зафиксированная.
/// Проверка «строки журнала нет» живёт там, где журнал настоящий, —
/// <c>EmailSslSender.IntegrationTests/SignInAtomicityApi.cs</c>.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class SignInAtomicityTests(IdentityDatabase database)
{
    /// <summary>
    /// Сосед сорвался — наши правки откачены.
    /// </summary>
    /// <remarks>
    /// Слабая половина: выдача падает, не успев ничего записать. Доказывает, что откатывается
    /// то, что записали <b>мы</b>, — обнуление счётчика неудач.
    /// </remarks>
    [Fact]
    public async Task Отказ_выдачи_откатывает_наши_правки()
    {
        var (id, email) = await UserAsync();

        // Довести счётчик до единицы: удачный вход обязан его обнулить, а откат — вернуть.
        await Should.ThrowAsync<Exception>(() => ВойтиAsync(email, "неверный", ВидВыдачи.Настоящая));

        var было = await НеудачAsync(id);
        было.ShouldBe(1, "подготовка не сработала — счётчик не вырос");

        // ⚠ Пароль ВЕРНЫЙ. Всё, что ломается, — выдача токенов у соседа.
        var отказ = await Should.ThrowAsync<InvalidOperationException>(
            () => ВойтиAsync(email, Strong, ВидВыдачи.ПадаетСразу));

        // ⚠ Именно НАШ срыв, а не какой-нибудь. Разбор — у второй проверки.
        отказ.Message.ShouldContain("сорвана");

        (await НеудачAsync(id)).ShouldBe(было, "счётчик неудач обнулён, хотя вход не состоялся");
    }

    /// <summary>
    /// Откат забирает и то, что успел записать сосед.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Вот ради чего написан файл.</b> Предыдущая проверка слабее, чем кажется: выдача,
    /// падающая <b>до</b> первой записи, оставляет базу нетронутой сама собой — при любой
    /// транзакции и вообще без неё. Ею можно доказать что угодно.
    ///
    /// Здесь выдача отрабатывает <b>по-настоящему</b>, строки уезжают в базу, и только потом
    /// происходит срыв. Значит строк нет ровно в одном случае: если транзакция входа
    /// накрывает и записи соседа тоже. Убери транзакцию из <c>SignInService</c> — и токен
    /// переживёт несостоявшийся вход, то есть человек получит доступ, о котором система не
    /// знает.
    ///
    /// Мутация 2026-08-25: заменил <c>db.InTransactionAsync(тело)</c> на прямой вызов тела →
    /// покраснела эта проверка. ⚠ <b>С первого раза не покраснела</b>, и это стоит запомнить:
    /// без транзакции выдача отказывается работать своим <c>RequireTransaction</c> — с тем же
    /// типом исключения, — а строк в базе не оказывается по той простой причине, что до
    /// записи не дошло. Проверка была зелёной под мутацией и доказывала не то. Спасло её
    /// утверждение о <b>тексте</b> отказа, а нашла ошибку сама мутация.
    /// </remarks>
    [Fact]
    public async Task Откат_забирает_и_то_что_записал_сосед()
    {
        var (id, email) = await UserAsync();

        (await ТокеновAsync(id)).ShouldBe(0, "подготовка не сработала — человек уже с токенами");

        var отказ = await Should.ThrowAsync<InvalidOperationException>(
            () => ВойтиAsync(email, Strong, ВидВыдачи.ПадаетПослеЗаписи));

        // ⚠ Проверяется ТЕКСТ, а не только тип, и это не педантизм — это то, что нашла
        // мутация. Убери транзакцию — и выдача откажется работать сама, своим
        // RequireTransaction, с тем же типом InvalidOperationException. Строк в базе после
        // этого тоже нет, и проверка «токенов ноль» остаётся зелёной, доказывая не то:
        // не откат, а то, что до записи дело не дошло.
        отказ.Message.ShouldContain(
            "ПОСЛЕ записи", customMessage: "сорвалось не там, где задумано, — проверка ничего не значит");

        (await ТокеновAsync(id)).ShouldBe(
            0, "токен пережил несостоявшийся вход: транзакция не накрывает записи соседа");
    }

    /// <summary>
    /// Зеркальный: удачный вход оставляет оба следа.
    /// </summary>
    /// <remarks>
    /// ⚠ Без него первые два доказывают не то. Реализация, которая вообще ничего не пишет,
    /// прошла бы обе проверки «после отказа в базе пусто» и была бы сломана целиком.
    /// </remarks>
    [Fact]
    public async Task Удачный_вход_оставляет_свои_следы()
    {
        var (id, email) = await UserAsync();

        await Should.ThrowAsync<Exception>(() => ВойтиAsync(email, "неверный", ВидВыдачи.Настоящая));
        (await НеудачAsync(id)).ShouldBe(1);

        var pair = await ВойтиAsync(email, Strong, ВидВыдачи.Настоящая);

        pair.Access.ShouldNotBeNullOrWhiteSpace();
        (await НеудачAsync(id)).ShouldBe(0, "удачный вход обязан обнулить счётчик неудач");
        (await ТокеновAsync(id)).ShouldBe(1, "удачный вход обязан оставить строку токена");
    }

    // --- обвязка ------------------------------------------------------------------------

    /// <summary>Как ведёт себя сосед в этом прогоне.</summary>
    private enum ВидВыдачи
    {
        /// <summary>Обычная выдача модуля токенов.</summary>
        Настоящая,

        /// <summary>Падает, не записав ничего.</summary>
        ПадаетСразу,

        /// <summary>Записывает по-настоящему, сохраняет — и падает.</summary>
        ПадаетПослеЗаписи,
    }

    /// <summary>Войти собственной сборкой службы, где выдачей управляет тест.</summary>
    /// <remarks>
    /// ⚠ Своя сборка, а не подмена в общей обвязке: общая одна на коллекцию, и сорванная
    /// выдача осталась бы сорванной у соседних проверок.
    /// </remarks>
    private async Task<TokenPair> ВойтиAsync(string email, string password, ВидВыдачи вид)
    {
        await using var scope = database.Scope();

        var provider = scope.ServiceProvider;
        var настоящая = provider.GetRequiredService<ITokenIssuer>();
        var db = provider.GetRequiredService<SenderDbContext>();

        var выдача = вид switch
        {
            ВидВыдачи.Настоящая => настоящая,
            ВидВыдачи.ПадаетСразу => new СорваннаяВыдача(),
            _ => (ITokenIssuer)new ВыдачаИПадение(настоящая, db),
        };

        var service = new SignInService(
            provider.GetRequiredService<Invitations>(),
            provider.GetRequiredService<TwoFactor.TwoFactorService>(),

            // ⚠ Способ фактора и код письмом (этап 17.7): берутся из контейнера, как всё
            // прочее. Собирать их здесь руками значило бы проверять не то приложение.
            provider.GetRequiredService<TwoFactor.SecondFactorPolicy>(),
            provider.GetRequiredService<TwoFactor.LoginCodes>(),
            db,
            provider.GetRequiredService<UserRepository>(),
            provider.GetRequiredService<IPasswordHasher>(),
            выдача,
            provider.GetRequiredService<ISettingsReader>(),
            provider.GetRequiredService<IAuditWriter>(),
            provider.GetRequiredService<SignInAttempts>(),
            provider.GetRequiredService<LoginFailures>(),
            provider.GetRequiredService<TwoFactor.ChallengeAttempts>(),
            provider.GetRequiredService<TimeProvider>());

        return (await service.SignInAsync(email, password, Origin, cancellationToken: Ct)).Pair;
    }

    private async Task<(Guid Id, string Email)> UserAsync()
    {
        database.Actor.AsSuperuser();

        var email = $"atomic-{Guid.CreateVersion7():N}@example.com";
        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Тестовый человек", Strong), Ct));

        return (user.Id, email);
    }

    private Task<int> НеудачAsync(Guid id) =>
        database.ReadAsync(db => db.Set<User>()
            .Where(user => user.Id == id)
            .Select(user => user.FailedLoginCount)
            .FirstAsync(Ct));

    /// <summary>
    /// Сколько строк токенов у этого человека.
    /// </summary>
    /// <remarks>
    /// ⚠ Отбор по СВОЕМУ человеку, а не счёт по всей таблице: база одна на коллекцию, и
    /// «токенов ноль» врало бы, пока рядом входит кто-то другой.
    ///
    /// Читается голым SQL: тип строки принадлежит чужому модулю и наружу не выставлен. Имя
    /// таблицы при этом взято из его же контракта, а не написано строкой, — иначе
    /// переименование таблицы уронило бы этот тест непонятной ошибкой базы.
    /// </remarks>
    private Task<int> ТокеновAsync(Guid id) =>
        database.ReadAsync(async db =>
        {
            var значения = await db.Database
                .SqlQueryRaw<int>(
                    $"SELECT COUNT(*)::int AS \"Value\" FROM {TokenTables.Tokens} WHERE user_id = {{0}}",
                    id)
                .ToListAsync(Ct);

            return значения.Single();
        });

    private const string Strong = "Очень-Хороший-Пароль-42!";

    private static readonly RequestOrigin Origin =
        new(System.Net.IPAddress.Parse("10.0.0.7"), "EAMS-Tests/1.0");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Выдача, которая падает, не тронув базу.</summary>
    private sealed class СорваннаяВыдача : ITokenIssuer
    {
        public Task<TokenPair> IssueAsync(
            TokenIssueRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("выдача сорвана до первой записи");

        public Task<TokenPair> RotateAsync(
            string refresh, RequestOrigin origin, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("выдача сорвана до первой записи");
    }

    /// <summary>
    /// Выдача, которая отработала, сохранила — и упала.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>SaveChangesAsync</c> здесь обязателен и не является натяжкой: настоящая выдача
    /// только набирает строки в трекер, а сохраняет их вызывающий. Без явного сохранения
    /// «строк в базе нет» было бы верно всегда — и проверка не значила бы ничего.
    /// </remarks>
    private sealed class ВыдачаИПадение(ITokenIssuer настоящая, SenderDbContext db) : ITokenIssuer
    {
        public async Task<TokenPair> IssueAsync(
            TokenIssueRequest request, CancellationToken cancellationToken = default)
        {
            await настоящая.IssueAsync(request, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            throw new InvalidOperationException("выдача сорвана ПОСЛЕ записи строк");
        }

        public Task<TokenPair> RotateAsync(
            string refresh, RequestOrigin origin, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
