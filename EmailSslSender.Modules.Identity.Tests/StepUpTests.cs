using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Переподтверждение личности: отметка на предъявленном токене.
/// </summary>
/// <remarks>
/// ⚠ <b>Весь файл держится на одном свойстве: отметка привязана к ТОКЕНУ, а не к человеку</b>
/// (решение <c>docs/decisions/0023-хранилища-ответы-до-первой-строки.md</c> §3). Своя таблица
/// «когда этот человек подтверждался последний раз» дала бы то же поле с той же меткой времени
/// — и заметить подмену смысла можно было только здесь и только сейчас.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class StepUpTests(IdentityDatabase database)
{
    [Fact]
    public async Task Подтверждение_ставит_отметку_на_предъявленный_токен()
    {
        var вход = await ВошёлСФакторомAsync();

        (await ЛичностьAsync(вход.Token.Access))!.StepUpAt
            .ShouldBe(DateTimeOffset.UnixEpoch, "в этой обвязке отметка входа уже протухла");

        var когда = await ПодтвердитьAsync(вход);
        var личность = await ЛичностьAsync(вход.Token.Access);

        личность!.StepUpAt.ShouldNotBeNull();
        личность.StepUpAt!.Value.ShouldBe(когда, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Отметка_доезжает_до_личности_запроса()
    {
        // ⚠ ЭТОТ ТЕСТ НАШЛА МУТАЦИОННАЯ БАТАРЕЯ 14.3, а не замысел. Проверка «отметка легла на
        // токен» ходит через ITokenIntrospector и распознавателя личности НЕ КАСАЕТСЯ. Мутация
        // «распознаватель отдаёт null вместо token.StepUpAt» прошла все восемь тестов файла
        // насквозь — а означала она мёртвую возможность: показ любой записи с require_totp
        // отвечал бы 403 навсегда, потому что свежей отметки не бывает никогда.
        //
        // мутация 2026-08-26: `null` вместо `token.StepUpAt` в SessionIdentityResolver →
        // покраснел этот тест (и только он).
        var вход = await ВошёлСФакторомAsync();

        (await ЛичностьЗапросаAsync(вход.Token.Access))!.StepUpAt.ShouldBe(DateTimeOffset.UnixEpoch);

        var когда = await ПодтвердитьAsync(вход);
        var личность = await ЛичностьЗапросаAsync(вход.Token.Access);

        личность!.StepUpAt.ShouldNotBeNull("отметка не доехала до личности запроса");
        личность.StepUpAt!.Value.ShouldBe(когда, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Отметка_ставится_на_токен_а_не_на_человека()
    {
        // ⚠ ГЛАВНЫЙ ТЕСТ ФАЙЛА. Отметка на человеке означала бы, что переподтверждение в одной
        // вкладке открывает секреты ВО ВСЕХ его сессиях и на другом его устройстве, — а
        // украденная живая сессия начинает работать оттого, что законный владелец ввёл код у
        // себя.
        //
        // мутация 2026-08-26: пометил в StepUpMarker все токены человека (`row.UserId ==`
        // вместо `row.Id ==`) → покраснел этот тест.
        var вход = await ВошёлСФакторомAsync();
        var второй = await ВойтиКодомAsync(вход.Email, вход.Secret);

        await ПодтвердитьAsync(вход);

        (await ЛичностьAsync(вход.Token.Access))!.StepUpAt.ShouldNotBeNull("свой токен не помечен");
        (await ЛичностьAsync(второй.Access))!.StepUpAt
            .ShouldBe(DateTimeOffset.UnixEpoch, "обновлена отметка ЧУЖОГО токена того же человека");
    }

    [Fact]
    public async Task Неверный_код_отметки_не_ставит_и_пишет_отказ()
    {
        var вход = await ВошёлСФакторомAsync();

        var отказ = await Should.ThrowAsync<AppException>(
            () => ПодтвердитьAsync(вход, код: "000000"));

        отказ.Code.ShouldBe(ErrorCodes.TwoFactorInvalid);

        (await ЛичностьAsync(вход.Token.Access))!.StepUpAt.ShouldBe(DateTimeOffset.UnixEpoch);

        // ⚠ Отказ обязан оставить след: череда отказов подряд — это подбор кода на живой
        // сессии, то есть уже захваченной вкладке не хватает одного шага до чужих паролей.
        var строка = Строки(вход.Token.TokenId).ShouldHaveSingleItem();

        строка.Result.ShouldBe(AuditResult.Deny);
        строка.Reason.ShouldBe("invalid_code");
    }

    /// <summary>
    /// Подбор кода упирается в ту же блокировку, что и вход, — и запертая учётная запись не
    /// переподтверждается даже верным кодом.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-09-15 (S2, stepup-1). До правки неудача оставляла только строку журнала:
    /// шесть цифр перебирались из угнанной сессии со скоростью сети.
    /// </remarks>
    [Fact]
    public async Task Подбор_кода_упирается_в_блокировку()
    {
        var вход = await ВошёлСФакторомAsync();

        database.Settings.Values[Settings.IdentitySettings.LockoutThreshold.Name] = 3;

        try
        {
            for (var попытка = 0; попытка < 3; попытка++)
            {
                (await Should.ThrowAsync<AppException>(() => ПодтвердитьAsync(вход, код: "000000", время: false)))
                    .Code.ShouldBe(ErrorCodes.TwoFactorInvalid);
            }

            var заперт = await database.ReadAsync(db => db.Set<User>()
                .Where(строка => строка.Id == вход.Id)
                .Select(строка => строка.LockedUntil)
                .SingleAsync(Ct));

            заперт.ShouldNotBeNull("подбор кода переподтверждения не запер учётную запись");

            // Верный код запертой учётной записи отметки не даёт.
            (await Should.ThrowAsync<AppException>(() => ПодтвердитьAsync(вход)))
                .Code.ShouldBe(ErrorCodes.TwoFactorInvalid);

            (await ЛичностьAsync(вход.Token.Access))!.StepUpAt.ShouldBe(DateTimeOffset.UnixEpoch);
            Строки(вход.Token.TokenId).Last().Reason.ShouldBe("locked");
        }
        finally
        {
            database.Settings.Values.Remove(Settings.IdentitySettings.LockoutThreshold.Name);
        }
    }

    [Fact]
    public async Task Удачное_подтверждение_тоже_пишет_строку()
    {
        var вход = await ВошёлСФакторомAsync();

        await ПодтвердитьAsync(вход);

        var строка = Строки(вход.Token.TokenId).ShouldHaveSingleItem();

        строка.Action.Name.ShouldBe("user.step_up");
        строка.Result.ShouldBe(AuditResult.Allow);
        строка.ActorId.ShouldBe(вход.Id);
    }

    [Fact]
    public async Task Один_код_дважды_не_подтверждает()
    {
        // Защита от повторного использования кода — общая с входом, и здесь она обязана
        // работать так же: у вора и у хозяина код один и тот же.
        var вход = await ВошёлСФакторомAsync();
        var код = Код(вход.Secret);

        await ВремяПрошлоAsync(вход.Id);
        await ПодтвердитьAsync(вход, код, время: false);

        (await Should.ThrowAsync<AppException>(() => ПодтвердитьAsync(вход, код, время: false))).Code
            .ShouldBe(ErrorCodes.TwoFactorInvalid);
    }

    [Fact]
    public async Task Отозванный_токен_пометить_нельзя()
    {
        // ⚠ Гонка узкая и реальная: токен отозвали между распознаванием запроса и проверкой
        // кода. Правильный исход один — подтверждение не состоялось.
        //
        // мутация 2026-08-26: убрал `row.RevokedAt == null` из StepUpMarker → покраснел этот
        // тест.
        var вход = await ВошёлСФакторомAsync();

        await ОтозватьAsync(вход.Token.TokenId);

        (await Should.ThrowAsync<AppException>(() => ПодтвердитьAsync(вход))).Code
            .ShouldBe(ErrorCodes.TwoFactorInvalid);

        // ⚠ Причина отказа в журнале отличает «код не тот» от «токена больше нет»: первое —
        // подбор, второе — гонка с отзывом, и разбирают их по-разному.
        Строки(вход.Token.TokenId).ShouldHaveSingleItem().Reason.ShouldBe("token_revoked");
    }

    [Fact]
    public async Task Без_фактора_подтвердить_нечем()
    {
        // Человек без включённого фактора не может переподтвердиться вовсе — и это верно:
        // подтверждать нечем. Записи, требующие фактора, ему закрыты, пока он его не включит.
        var (id, почта) = await ЧеловекAsync();
        var токен = await ВойтиAsync(почта);

        var отказ = await Should.ThrowAsync<AppException>(() =>
            ПодтвердитьAsync(new Вошедший(id, почта, токен, ""), код: "123456", время: false));

        // Отказ по существу — «подключите аутентификатор», а не «код не тот»: код письмом сессию
        // не подтверждает (ревью безопасности 2026-10-20).
        отказ.Code.ShouldBe(ErrorCodes.TwoFactorEnrollmentRequired);
    }

    [Fact]
    public async Task Способ_входа_без_строки_выдачи_не_подтверждается()
    {
        // ⚠ Молча считать такой запрос подтверждённым было бы дырой ровно того рода, ради
        // закрытия которой отметку и привязали к токену.
        var вход = await ВошёлСФакторомAsync();

        database.Actor.Become(вход.Id, session: null);

        await using var scope = database.Scope();

        var отказ = await Should.ThrowAsync<AppException>(() =>
            scope.ServiceProvider.GetRequiredService<StepUpService>()
                .ConfirmAsync(Код(вход.Secret), Ct));

        отказ.Code.ShouldBe(ErrorCodes.TwoFactorInvalid);
    }

    // --- обвязка -----------------------------------------------------------------------------

    /// <summary>Вошедший человек со своим токеном и своим секретом фактора.</summary>
    private sealed record Вошедший(Guid Id, string Email, TokenPair Token, string Secret);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Пароль = "Очень-Хороший-Пароль-42!";

    private static readonly RequestOrigin Откуда =
        new(System.Net.IPAddress.Parse("10.0.0.7"), "EAMS-Tests/1.0");

    /// <summary>
    /// Строки журнала про подтверждение ЭТОГО токена.
    /// </summary>
    /// <remarks>
    /// ⚠ Журнал подделки живёт весь прогон и не чистится: чистка сделала бы соседние тесты
    /// зависимыми от порядка. Фильтр по своему токену отвечает на тот же вопрос и не врёт на
    /// полной базе.
    /// </remarks>
    private IReadOnlyList<AuditEntry> Строки(Guid токен) =>
    [
        .. database.Audit.Entries.Where(
            строка => строка.Action.Name == "user.step_up" && строка.ResourceId == токен),
    ];

    private async Task<(Guid Id, string Email)> ЧеловекAsync()
    {
        database.Actor.AsSuperuser();

        var почта = $"stepup-{Guid.CreateVersion7():N}@example.com";
        var (человек, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(почта, "Тестовый человек", Пароль, MustChangePassword: false), Ct));

        return (человек.Id, почта);
    }

    /// <summary>
    /// Человек с включённым фактором и живым токеном сессии.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Фактор включается ДО входа, и порядок здесь не вкусовой.</b> Включение закрывает
    /// прежние сессии («вы включили второй фактор — войдите заново»), то есть токен, выданный
    /// раньше, к моменту подтверждения уже мёртв. Первая редакция обвязки делала наоборот и
    /// падала на распознавании — падала верно.
    /// </remarks>
    private async Task<Вошедший> ВошёлСФакторомAsync()
    {
        var (id, почта) = await ЧеловекAsync();
        var секрет = await ВключитьФакторAsync(id);
        var токен = await ВойтиКодомAsync(почта, секрет);

        return new Вошедший(id, почта, токен, секрет);
    }

    /// <summary>Вход человека с включённым фактором: расписка плюс код.</summary>
    private async Task<TokenPair> ВойтиКодомAsync(string почта, string секрет)
    {
        var расписка = await РаспискаAsync(почта);

        await ВремяПрошлоAsync(почта);

        await using var scope = database.Scope();

        var pair = (await scope.ServiceProvider.GetRequiredService<SignInService>()
            .CompleteTwoFactorAsync(расписка, Код(секрет), Откуда, cancellationToken: Ct)).Pair;
        // Имитируем давнюю отметку входа: следующий код должен обновить только эту сессию.
        await scope.ServiceProvider.GetRequiredService<IStepUpMarker>().MarkAsync(pair.TokenId, DateTimeOffset.UnixEpoch, Ct);
        return pair;
    }

    private async Task<string> РаспискаAsync(string почта)
    {
        var отказ = await Should.ThrowAsync<AppException>(() => ВойтиAsync(почта));

        отказ.Code.ShouldBe(ErrorCodes.TwoFactorRequired);

        return (string)отказ.Details!["challenge"]!;
    }

    private async Task<TokenPair> ВойтиAsync(string почта)
    {
        await using var scope = database.Scope();

        return (await scope.ServiceProvider.GetRequiredService<SignInService>()
            .SignInAsync(почта, Пароль, Откуда, cancellationToken: Ct)).Pair;
    }

    /// <summary>Включить и подтвердить фактор. Возвращает секрет в base32.</summary>
    private async Task<string> ВключитьФакторAsync(Guid id)
    {
        database.Actor.Become(id);

        await using var scope = database.Scope();
        var факторы = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        var заведение = await факторы.SetupAsync(Ct);

        await факторы.ConfirmAsync(Код(заведение.Secret), Ct);

        return заведение.Secret;
    }

    /// <summary>Переподтвердить личность на токене этого входа.</summary>
    private async Task<DateTimeOffset> ПодтвердитьAsync(
        Вошедший вход, string? код = null, bool время = true)
    {
        if (время)
        {
            await ВремяПрошлоAsync(вход.Id);
        }

        database.Actor.Become(вход.Id, вход.Token.TokenId);

        await using var scope = database.Scope();

        return await scope.ServiceProvider.GetRequiredService<StepUpService>()
            .ConfirmAsync(код ?? Код(вход.Secret), Ct);
    }

    /// <summary>
    /// Пройти ВЕСЬ путь распознавания: секрет, состояние учётной записи, ограничения личности.
    /// </summary>
    /// <remarks>
    /// ⚠ Отличается от <see cref="ЛичностьAsync"/> тем, чем отличаются два разных вопроса.
    /// Тот спрашивает у владельца токенов «жив ли секрет и чей он»; этот собирает личность
    /// запроса — и только по этому пути отметка доезжает до того, кто её читает.
    /// </remarks>
    private async Task<ResolvedIdentity?> ЛичностьЗапросаAsync(string access)
    {
        await using var scope = database.Scope();

        return await scope.ServiceProvider.GetRequiredService<IIdentityResolver>()
            .ResolveAsync(new Заголовком(access), Ct);
    }

    /// <summary>Пришедший с одним заголовком — больше распознавателю ничего не нужно.</summary>
    private sealed class Заголовком(string access) : IIdentityRequest
    {
        public string? Header(string name) =>
            name == "Authorization" ? $"Bearer {access}" : null;

        public string? Cookie(string name) => null;
    }

    private async Task<TokenIdentity?> ЛичностьAsync(string access)
    {
        await using var scope = database.Scope();

        return await scope.ServiceProvider.GetRequiredService<ITokenIntrospector>()
            .IntrospectAsync(access, Ct);
    }

    /// <summary>
    /// Отозвать токен.
    /// </summary>
    /// <remarks>
    /// ⚠ Отзыв <b>требует открытой транзакции</b> и говорит об этом отказом: секреты гасятся
    /// оператором немедленно, а строка журнала уезжает на сохранении, — без общей транзакции
    /// доступ пропал бы, а следа не осталось.
    /// </remarks>
    private async Task ОтозватьAsync(Guid токен)
    {
        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var отзыв = scope.ServiceProvider.GetRequiredService<ITokenRevoker>();

        await db.InTransactionAsync(
            async ct =>
            {
                await отзыв.RevokeAsync(токен, RevokeReasons.SignedOut, ct);

                await db.SaveChangesAsync(ct);
            },
            Ct);
    }

    /// <summary>
    /// Считать, что между двумя кодами прошло время.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Иначе цепочку «включил фактор → вошёл → подтвердил» в тесте не проиграть.</b> Окно
    /// у кода — один шаг в обе стороны, а принятый шаг расходуется: трёх независимых кодов в
    /// тридцати секундах не существует. В жизни между этими действиями проходят минуты; здесь
    /// сбрасывается принятый шаг, и это ровно то же самое.
    ///
    /// ⚠ Сбрасывается ТОЛЬКО принятый шаг, и только там, где тест этого просит: на защите от
    /// повторного использования держится <see cref="Один_код_дважды_не_подтверждает"/>, и
    /// безусловный сброс сделал бы его зелёным навсегда.
    /// </remarks>
    private Task ВремяПрошлоAsync(Guid id) =>
        database.ReadAsync(db => db.Set<UserTwoFactor>()
            .Where(row => row.UserId == id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastStep, -1L), Ct));

    private Task ВремяПрошлоAsync(string почта) =>
        database.ReadAsync(db => db.Set<UserTwoFactor>()
            .Where(row => db.Set<User>().Any(
                человек => человек.Id == row.UserId && человек.Email == почта))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastStep, -1L), Ct));

    /// <summary>Код, который приложение показывает сейчас.</summary>
    private static string Код(string base32) => TestTotp.Code(base32);
}
