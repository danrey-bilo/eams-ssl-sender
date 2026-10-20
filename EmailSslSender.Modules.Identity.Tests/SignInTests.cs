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

    /// <summary>
    /// После истёкшей блокировки одна неудача не запирает снова: счёт начинается заново.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-10-20. Счётчик не убывал никогда: после первой блокировки он оставался на
    /// пороге, и истёкшую блокировку ставила заново ПЕРВАЯ же неудача. Одной неверной попытки раз в
    /// <c>auth.lockout_minutes</c> хватало, чтобы владелец не вошёл никогда. Теперь, чтобы запереть
    /// снова, нужен снова полный порог.
    /// </remarks>
    // мутация 2026-10-20: в LoginFailures.TakeAsync счёт после истёкшей блокировки продолжает
    // расти (`+ 1` вместо `1`) → покраснел этот факт.
    [Fact]
    public async Task После_истёкшей_блокировки_одна_неудача_не_запирает_снова()
    {
        var (id, email) = await ActiveUserAsync();
        database.Settings.Values["auth.lockout_threshold"] = 2;

        try
        {
            for (var i = 0; i < 2; i++)
            {
                await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));
            }

            (await LockedUntilAsync(id)).ShouldNotBeNull("подготовка не сработала — блокировки нет");

            await ОтмотатьБлокировкуAsync(id);

            await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));

            (await LockedUntilAsync(id)).ShouldBeNull("одна неудача после истёкшей блокировки заперла снова");
            (await FailuresAsync(id)).ShouldBe(1, "счёт после истёкшей блокировки не начался заново");

            // А полный порог по-прежнему запирает.
            await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));

            (await LockedUntilAsync(id)).ShouldNotBeNull("полный порог после истёкшей блокировки не запер");
        }
        finally
        {
            database.Settings.Values.Remove("auth.lockout_threshold");
        }
    }

    /// <summary>
    /// Одновременный подбор пароля получает ровно столько проверок, сколько разрешает порог.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-10-20. Блокировка читалась из загруженной строки, а неудача засчитывалась
    /// после argon2 — десять одновременных попыток при пороге три проходили проверку блокировки все.
    /// Теперь попытка занимается одним условным обновлением до проверки пароля.
    /// </remarks>
    [Fact]
    public async Task Одновременный_подбор_пароля_не_превышает_порог()
    {
        var (id, email) = await ActiveUserAsync();
        database.Settings.Values["auth.lockout_threshold"] = 3;

        try
        {
            var старт = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var попытки = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                await старт.Task;

                return await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));
            })).ToList();

            старт.SetResult();

            await Task.WhenAll(попытки);

            database.Audit.Entries.Count(строка => строка.ActorId == id && строка.Reason == "bad_password")
                .ShouldBe(3, "одновременные запросы получили больше проверок пароля, чем порог");
            database.Audit.Entries.Count(строка => строка.ActorId == id && строка.Reason == "locked")
                .ShouldBe(7);
            (await FailuresAsync(id)).ShouldBe(3);
        }
        finally
        {
            database.Settings.Values.Remove("auth.lockout_threshold");
        }
    }

    /// <summary>Верный пароль на последней попытке до порога не запирает своего владельца.</summary>
    /// <remarks>
    /// ⚠ Попытка занимается заранее и доводит счёт до порога — значит ставит блокировку ещё до
    /// проверки пароля. Верный пароль обязан её снять, иначе владелец, ошибившийся порог минус
    /// один раз, был бы заперт собственным правильным вводом.
    /// </remarks>
    [Fact]
    public async Task Верный_пароль_на_пороге_не_запирает()
    {
        var (id, email) = await ActiveUserAsync();
        database.Settings.Values["auth.lockout_threshold"] = 2;

        try
        {
            await Should.ThrowAsync<AppException>(() => SignInAsync(email, "Неверный-Пароль-1!"));

            var pair = await SignInAsync(email, Strong);

            pair.Access.ShouldNotBeNullOrWhiteSpace();
            (await LockedUntilAsync(id)).ShouldBeNull();
            (await FailuresAsync(id)).ShouldBe(0);
        }
        finally
        {
            database.Settings.Values.Remove("auth.lockout_threshold");
        }
    }

    // --- обвязка -----------------------------------------------------------------------------

    private Task<int> FailuresAsync(Guid id) =>
        database.ReadAsync(db => db.Set<User>()
            .Where(user => user.Id == id)
            .Select(user => user.FailedLoginCount)
            .FirstAsync(Ct));

    /// <summary>Отмотать блокировку в прошлое: ждать её конца в тесте — минуты простоя.</summary>
    private Task ОтмотатьБлокировкуAsync(Guid id) =>
        database.ReadAsync(db => db.Set<User>()
            .Where(user => user.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(user => user.LockedUntil, DateTimeOffset.UtcNow.AddMinutes(-1)),
                Ct));

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
