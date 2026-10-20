using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Смена и сброс пароля.
/// </summary>
/// <remarks>
/// ⚠ Ничего этого не существовало до 2026-08-22, и вместе с этим не работал флаг
/// <c>must_change_password</c>: система велела сменить пароль, а сменить его было нечем.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class PasswordTests(IdentityDatabase database)
{
    [Fact]
    public async Task Смена_пароля_меняет_пароль_и_снимает_ограничение()
    {
        var (id, email) = await UserAsync(mustChange: true);

        database.Actor.Become(id);

        await database.InScopeAsync(users => users.ChangePasswordAsync(Strong, Другой, Ct));

        // Новый пароль работает, старый — нет.
        await Should.NotThrowAsync(() => SignInAsync(email, Другой));
        await Should.ThrowAsync<AppException>(() => SignInAsync(email, Strong));

        (await FlagAsync(id)).ShouldBeFalse("ради снятия ограничения смена и делается");
    }

    /// <summary>
    /// Подбор текущего пароля через смену упирается в блокировку — и запертая учётная запись не
    /// меняет пароль даже верным текущим.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-09-15 (S2, password-1). До правки угнанная сессия перебирала пароль владельца
    /// здесь без счёта, а угадав — ставила свой и выгоняла всех, кроме себя.
    /// </remarks>
    [Fact]
    public async Task Подбор_текущего_пароля_упирается_в_блокировку()
    {
        var (id, _) = await UserAsync();

        database.Actor.Become(id);
        database.Settings.Values[Settings.IdentitySettings.LockoutThreshold.Name] = 3;

        try
        {
            for (var попытка = 0; попытка < 3; попытка++)
            {
                (await Should.ThrowAsync<AppException>(
                    () => database.InScopeAsync(users => users.ChangePasswordAsync("Не-Тот-Пароль-1!", Другой, Ct))))
                    .Code.ShouldBe("INVALID_CREDENTIALS");
            }

            var заперт = await database.ReadAsync(db => db.Set<User>()
                .Where(строка => строка.Id == id)
                .Select(строка => строка.LockedUntil)
                .SingleAsync(Ct));

            заперт.ShouldNotBeNull("подбор текущего пароля не запер учётную запись");

            (await Should.ThrowAsync<AppException>(
                () => database.InScopeAsync(users => users.ChangePasswordAsync(Strong, Другой, Ct))))
                .Code.ShouldBe("INVALID_CREDENTIALS", "запертая учётная запись сменила пароль");
        }
        finally
        {
            database.Settings.Values.Remove(Settings.IdentitySettings.LockoutThreshold.Name);
        }
    }

    [Fact]
    public async Task Неверный_текущий_пароль_отвергается()
    {
        // ⚠ Старый пароль спрашивается обязательно: угнанная вкладка иначе меняет пароль и
        // запирает хозяина снаружи.
        var (id, _) = await UserAsync();

        database.Actor.Become(id);

        var error = await Should.ThrowAsync<AppException>(
            () => database.InScopeAsync(users => users.ChangePasswordAsync("Не-Тот-Пароль-1!", Другой, Ct)));

        error.Code.ShouldBe("INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Пароль_совпадающий_с_текущим_отвергается()
    {
        var (id, _) = await UserAsync();

        database.Actor.Become(id);

        var error = await Should.ThrowAsync<AppException>(
            () => database.InScopeAsync(users => users.ChangePasswordAsync(Strong, Strong, Ct)));

        error.Code.ShouldBe("PASSWORD_POLICY_VIOLATION");
    }

    [Fact]
    public async Task Слабый_новый_пароль_отвергается()
    {
        var (id, _) = await UserAsync();

        database.Actor.Become(id);

        var error = await Should.ThrowAsync<AppException>(
            () => database.InScopeAsync(users => users.ChangePasswordAsync(Strong, "коротко", Ct)));

        error.Code.ShouldBe("PASSWORD_POLICY_VIOLATION");
    }

    [Fact]
    public async Task Смена_закрывает_прочие_сессии_но_оставляет_текущую()
    {
        // ⚠ Смысл смены — выгнать того, кто мог подсмотреть прежний пароль. Выгонять заодно
        // и того, кто меняет, значит наказывать за правильное поведение: смена заканчивалась
        // бы экраном входа, и её перестали бы делать.
        var (id, email) = await UserAsync();

        var mine = await SignInAsync(email, Strong);
        var other = await SignInAsync(email, Strong);

        database.Actor.Become(id, mine.TokenId);

        await database.InScopeAsync(users => users.ChangePasswordAsync(Strong, Другой, Ct));

        // ⚠ Проверяем через ПУБЛИЧНЫЙ КОНТРАКТ соседа, а не через его список. Строки
        // токенов уехали в модуль токенов (этап 10.5.4), и заглядывать в них отсюда значило
        // бы открыть его внутренности чужому тестовому проекту. Свойство от этого
        // проверяется даже точнее: важно не «сколько строк осталось», а «чем ещё можно
        // войти».
        await Should.NotThrowAsync(() => ПроверитьAsync(mine.Access), "свой токен обязан остаться живым");

        var error = await Should.ThrowAsync<AppException>(() => ПроверитьAsync(other.Access));

        error.Message.ShouldContain("Пароль изменён");
    }

    // --- сброс по ссылке ------------------------------------------------------------------

    [Fact]
    public async Task Сброс_по_ссылке_задаёт_пароль_и_закрывает_все_сессии()
    {
        var (id, email) = await UserAsync();

        var было = await SignInAsync(email, Strong);

        database.Actor.AsSuperuser();

        var reset = await database.InScopeAsync(users => users.IssuePasswordResetAsync(id, Ct));

        await database.InScopeAsync(users => users.ResetPasswordAsync(reset.Token, Другой, Ct));

        // Сначала проверяем прежний токен, и только потом входим: вход завёл бы новый, и
        // проверка «войти больше нечем» стала бы проверкой самой себя.
        await Should.ThrowAsync<AppException>(() => ПроверитьAsync(было.Access));

        await Should.NotThrowAsync(() => SignInAsync(email, Другой));
    }

    [Fact]
    public async Task Ссылка_сброса_годится_один_раз()
    {
        var (id, _) = await UserAsync();

        database.Actor.AsSuperuser();

        var reset = await database.InScopeAsync(users => users.IssuePasswordResetAsync(id, Ct));

        await database.InScopeAsync(users => users.ResetPasswordAsync(reset.Token, Другой, Ct));

        var error = await Should.ThrowAsync<AppException>(
            () => database.InScopeAsync(users => users.ResetPasswordAsync(reset.Token, Третий, Ct)));

        error.Code.ShouldBe("ACTIVATION_TOKEN_INVALID");
    }

    [Fact]
    public async Task Ссылка_приглашения_не_годится_для_сброса()
    {
        // ⚠ Назначение ссылки — часть её самой. Иначе приглашение, выданное на заведение
        // учётной записи, работало бы как сброс пароля уже активированной, и наоборот.
        database.Actor.AsSuperuser();

        var email = $"invited-{Guid.CreateVersion7():N}@example.com";
        var (_, invitation) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Приглашённый", null), Ct));

        var error = await Should.ThrowAsync<AppException>(
            () => database.InScopeAsync(users => users.ResetPasswordAsync(invitation!.Token, Другой, Ct)));

        error.Code.ShouldBe("ACTIVATION_TOKEN_INVALID");
    }

    [Fact]
    public async Task Отключённому_ссылку_не_выдают()
    {
        database.Actor.AsSuperuser();

        var (id, _) = await UserAsync();

        await database.InScopeAsync(users => users.DisableAsync(id, Ct));

        var error = await Should.ThrowAsync<AppException>(
            () => database.InScopeAsync(users => users.IssuePasswordResetAsync(id, Ct)));

        error.Code.ShouldBe("USER_INACTIVE");
    }

    [Fact]
    public async Task Смена_пишется_в_журнал_без_самого_пароля()
    {
        var (id, _) = await UserAsync();

        database.Actor.Become(id);

        await database.InScopeAsync(users => users.ChangePasswordAsync(Strong, Другой, Ct));

        var entry = database.Audit.About(id).Last(row => row.Action.Name == "user.password_changed");

        entry.Changes.ShouldNotBeNull();
        entry.Changes!["by_reset"].ShouldBe(false);

        // Ни старого, ни нового — в журнале только факт.
        entry.Changes.Values.ShouldNotContain(Strong);
        entry.Changes.Values.ShouldNotContain(Другой);
    }

    // --- обвязка -------------------------------------------------------------------------

    /// <summary>Слишком длинный пароль отвергается конвертом, а не пятисоткой из хешера.</summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Граница считается в БАЙТАХ, и потому проверка идёт кириллицей.</b> Предел хешера —
    /// 1024 байта UTF-8; кириллическая буква весит два. Пароль из 600 русских букв проходит любую
    /// проверку по длине строки (600 &lt; 1024) и весит 1200 байт — то есть ровно тот случай,
    /// который до этапа 59.6 давал <c>ArgumentOutOfRangeException</c> из библиотеки, а наружу —
    /// пятисотку вместо конверта (правило 17).
    ///
    /// ⚠ <b>Проверяется код, а не текст.</b> Текст сообщения — дело словаря; машинный код
    /// <c>PASSWORD_POLICY_VIOLATION</c> — это контракт, по которому клиент показывает список
    /// невыполненных требований.
    ///
    /// ⚠ Уткнуться в предел проще всего не злым умыслом, а менеджером паролей: в поле «новый
    /// пароль» вставляют приватный ключ.
    ///
    /// ⚠ ⚠ <b>Пароль обязан удовлетворять ВСЕМ прочим требованиям, и первая редакция этого
    /// факта их не удовлетворяла.</b> Строка из шестисот букв «я» не имеет ни заглавной, ни
    /// цифры, ни знака — отказ приходил за них, код совпадал, тест зеленел. Красно-зелёная это
    /// и показала: с поднятой до недостижимой границей он **остался зелёным**. Поэтому здесь
    /// проверяется не код конверта, а <b>состав</b> <c>codes</c>: длина названа отдельной
    /// причиной, и подменить её другой нельзя.
    /// </remarks>
    [Fact]
    public async Task Слишком_длинный_пароль_отвергается_конвертом()
    {
        var (id, _) = await UserAsync();

        database.Actor.Become(id);

        // Заглавная, строчная, цифра и знак — на месте; длинно только «тело».
        var длинный = "Яб1!" + new string('я', 600);

        var отказ = await Should.ThrowAsync<AppException>(
            () => database.InScopeAsync(users => users.ChangePasswordAsync(Strong, длинный, Ct)));

        отказ.Code.ShouldBe(
            ErrorCodes.PasswordPolicyViolation,
            "длинный пароль ушёл в хешер и вернулся исключением библиотеки, а не отказом");

        отказ.Details.ShouldNotBeNull("конверт без подробностей не называет невыполненное");

        var коды = отказ.Details["codes"].ShouldBeAssignableTo<IEnumerable<string>>()!;

        коды.ShouldContain(
            "max_length",
            "отказ пришёл, но не за длину — значит проверка длины могла и не работать");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";
    private const string Другой = "Совсем-Другой-Пароль-77!";
    private const string Третий = "Третий-Хороший-Пароль-13!";

    private static readonly RequestOrigin Origin =
        new(System.Net.IPAddress.Parse("10.0.0.9"), "EAMS-Tests/1.0");

    private async Task<(Guid Id, string Email)> UserAsync(bool mustChange = false)
    {
        database.Actor.AsSuperuser();

        var email = $"pwd-{Guid.CreateVersion7():N}@example.com";
        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Тестовый человек", Strong, mustChange), Ct));

        return (user.Id, email);
    }

    private async Task<TokenPair> SignInAsync(string email, string password) =>
        (await InSessionsAsync(
            sessions => sessions.SignInAsync(email, password, Origin, cancellationToken: Ct))).Pair;

    /// <summary>
    /// Проверить секрет у соседа. Годный вернёт личность, негодный бросит.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Единственное, чем этот сьют трогает соседний модуль, — его ПУБЛИЧНЫЙ контракт.</b>
    /// Заглядывать в строки токенов отсюда нельзя: они внутренние, и открывать их чужому
    /// тестовому проекту значило бы сделать внутренности модуля частью его поверхности.
    /// </remarks>
    private async Task ПроверитьAsync(string access)
    {
        await using var scope = database.Scope();

        await scope.ServiceProvider.GetRequiredService<ITokenIntrospector>().IntrospectAsync(access, Ct);
    }

    private async Task<T> InSessionsAsync<T>(Func<SignInService, Task<T>> action)
    {
        await using var scope = database.Scope();

        return await action(scope.ServiceProvider.GetRequiredService<SignInService>());
    }

    private Task<bool> FlagAsync(Guid id) =>
        database.ReadAsync(db => db.Set<User>()
            .Where(user => user.Id == id)
            .Select(user => user.MustChangePassword)
            .FirstAsync(Ct));
}
