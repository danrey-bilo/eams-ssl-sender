using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Вход: пароль, блокировка после неудач, счётчик.
/// </summary>
/// <remarks>
/// <b>Осталось здесь при расщеплении сьюта (этап 10.5.4).</b> Всё, что про сам предъявляемый
/// секрет — выдача, обновление, кража, отзыв, — уехало в
/// <c>EmailSslSender.Modules.Tokens.Tests/TokenLifecycleTests.cs</c>. Граница та же, что у кода:
/// «кто решает, что пускать» — здесь, «чем это доказывается» — там.
///
/// ⚠ Вход по-прежнему возвращает пару токенов, и это не противоречие: выдаёт её сосед
/// через <c>ITokenIssuer</c>, а мы проверяем, что до выдачи дело дошло — или не дошло.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class SignInTests(IdentityDatabase database)
{
    [Fact]
    public async Task Вход_по_паролю_выдаёт_пару_токенов()
    {
        var (_, email) = await ActiveUserAsync();

        var pair = await SignInAsync(email, Strong);

        pair.Access.ShouldNotBeNullOrWhiteSpace();
        pair.Refresh.ShouldNotBe(pair.Access);
        pair.AccessExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Неверный_пароль_и_несуществующий_адрес_отвечают_одинаково()
    {
        // ⚠ Разные ответы здесь означали бы, что форма входа отвечает на вопрос «а есть ли
        // такой человек». Совпадать обязаны и код, и текст.
        var (_, email) = await ActiveUserAsync();

        var wrong = await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));
        var missing = await Should.ThrowAsync<AppException>(
            () => SignInAsync($"нет-{Guid.CreateVersion7():N}@example.com", Strong));

        wrong.Code.ShouldBe(missing.Code);
        wrong.Message.ShouldBe(missing.Message);
        wrong.Details.ShouldBeNull();
        missing.Details.ShouldBeNull();
    }

    [Fact]
    public async Task Отключённый_войти_не_может()
    {
        var (id, email) = await ActiveUserAsync();

        await database.InScopeAsync(users => users.DisableAsync(id, Ct));

        var error = await Should.ThrowAsync<AppException>(() => SignInAsync(email, Strong));

        error.Code.ShouldBe("INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task После_нескольких_неудач_вход_блокируется()
    {
        var (id, email) = await ActiveUserAsync();
        database.Settings.Values["auth.lockout_threshold"] = 3;

        try
        {
            for (var i = 0; i < 3; i++)
            {
                await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));
            }

            var locked = await database.ReadAsync(db => db.Set<User>()
                .Where(user => user.Id == id)
                .Select(user => user.LockedUntil)
                .FirstAsync(Ct));

            locked.ShouldNotBeNull();
            locked!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

            // И верный пароль теперь тоже не проходит — иначе блокировка бессмысленна.
            await Should.ThrowAsync<AppException>(() => SignInAsync(email, Strong));
        }
        finally
        {
            database.Settings.Values.Remove("auth.lockout_threshold");
        }
    }

    [Fact]
    public async Task Перебор_по_заблокированной_учётной_записи_не_продлевает_блокировку()
    {
        // ⚠ Ловушка, о которой предупреждает OWASP: если счётчик растёт и во время
        // блокировки, непрерывный перебор продлевает её бесконечно — и настоящий владелец
        // не войдёт никогда. Блокировка, задуманная как защита, становится оружием против
        // того, кого защищает.
        var (id, email) = await ActiveUserAsync();
        database.Settings.Values["auth.lockout_threshold"] = 2;

        try
        {
            for (var i = 0; i < 2; i++)
            {
                await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));
            }

            var first = await LockedUntilAsync(id);

            for (var i = 0; i < 5; i++)
            {
                await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));
            }

            (await LockedUntilAsync(id)).ShouldBe(first);
        }
        finally
        {
            database.Settings.Values.Remove("auth.lockout_threshold");
        }
    }

    [Fact]
    public async Task Успешный_вход_обнуляет_счётчик_неудач()
    {
        var (id, email) = await ActiveUserAsync();

        await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));
        await SignInAsync(email, Strong);

        var count = await database.ReadAsync(db => db.Set<User>()
            .Where(user => user.Id == id)
            .Select(user => user.FailedLoginCount)
            .FirstAsync(Ct));

        count.ShouldBe(0);
    }

    [Fact]
    public async Task После_истечения_блокировки_вход_снова_работает()
    {
        // ⚠ Найдено мутацией: «считать неудачей любую попытку, пока у записи стоит отметка
        // блокировки» проходило зелёным. Отметка не снимается по времени сама — она просто
        // уходит в прошлое; и человек, дождавшийся конца блокировки, не вошёл бы уже никогда.
        var (id, email) = await ActiveUserAsync();
        database.Settings.Values["auth.lockout_threshold"] = 2;

        try
        {
            for (var i = 0; i < 2; i++)
            {
                await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));
            }

            (await LockedUntilAsync(id)).ShouldNotBeNull();

            // Отматываем блокировку в прошлое: ждать её конца в тесте — минуты простоя.
            await database.ReadAsync(db => db.Set<User>()
                .Where(user => user.Id == id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(user => user.LockedUntil, DateTimeOffset.UtcNow.AddMinutes(-1)),
                    Ct));

            var pair = await SignInAsync(email, Strong);

            pair.Access.ShouldNotBeNullOrWhiteSpace();

            // И отметка снята: иначе следующая попытка снова упрётся в прошлое.
            (await LockedUntilAsync(id)).ShouldBeNull();
        }
        finally
        {
            database.Settings.Values.Remove("auth.lockout_threshold");
        }
    }

    // --- обвязка -----------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    private static readonly RequestOrigin Origin =
        new(System.Net.IPAddress.Parse("10.0.0.7"), "EAMS-Tests/1.0");

    /// <summary>Завести живого человека с известным паролем.</summary>
    private async Task<(Guid Id, string Email)> ActiveUserAsync()
    {
        database.Actor.AsSuperuser();

        var email = $"signin-{Guid.CreateVersion7():N}@example.com";
        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Тестовый человек", Strong), Ct));

        return (user.Id, email);
    }

    private async Task<TokenPair> SignInAsync(string email, string password)
    {
        await using var scope = database.Scope();

        return (await scope.ServiceProvider
            .GetRequiredService<SignInService>()
            .SignInAsync(email, password, Origin, cancellationToken: Ct)).Pair;
    }

    private Task<DateTimeOffset?> LockedUntilAsync(Guid id) =>
        database.ReadAsync(db => db.Set<User>()
            .Where(user => user.Id == id)
            .Select(user => user.LockedUntil)
            .FirstAsync(Ct));
}
