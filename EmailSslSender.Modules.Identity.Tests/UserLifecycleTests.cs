using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Users;
using Microsoft.Extensions.DependencyInjection;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Жизненный цикл учётной записи: заведение, приглашение, активация, отключение.
/// </summary>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class UserLifecycleTests(IdentityDatabase database)
{
    [Fact]
    public async Task Заведение_по_приглашению_не_задаёт_пароля()
    {
        // ⚠ Обычный путь, и он же единственный правильный. Выданный кем-то пароль знают
        // двое, и один из них не тот, кто им пользуется.
        database.Actor.AsSuperuser();

        var (user, invitation) = await CreateAsync(Email());

        invitation.ShouldNotBeNull();
        user.ActivatedAt.ShouldBeNull("до перехода по ссылке учётной записи ещё нет");
        user.MustChangePassword.ShouldBeFalse("пароль человек придумает сам — менять нечего");

        // Пароль есть, но его не знает никто: значение заведомо непроверяемое, а не
        // отсутствующее. NULL однажды породил бы ветку «проверять нечего, пускаем».
        user.PasswordHash.ShouldNotBeNullOrEmpty();

        var hasher = new PasswordHasher(Argon2Profile.Cheap);
        (await hasher.VerifyAsync(user.PasswordHash, "", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Заведение_с_паролем_сразу_считается_активированным()
    {
        // Человека допустили без подтверждения ящика — значит он активен с этой секунды.
        // Пропуск отметки не ломает вход, а врёт про него: в админке такая учётная запись
        // показывалась бы «Приглашён», хотя работает. Найдено живой проверкой на
        // Python-версии.
        database.Actor.AsSuperuser();

        var (user, invitation) = await CreateAsync(Email(), password: Strong);

        user.ActivatedAt.ShouldNotBeNull();
        invitation.ShouldBeNull("письмо тут ни к чему: пароль уже задан");
    }

    [Fact]
    public async Task Адрес_занят_независимо_от_регистра()
    {
        // ⚠ Колонка citext: `User@x.com` и `user@x.com` — один логин. Заведи обоих, и
        // непонятно, кто из них войдёт.
        database.Actor.AsSuperuser();

        var address = Email();
        await CreateAsync(address);

        var error = await Should.ThrowAsync<AppException>(() => CreateAsync(address.ToUpperInvariant()));

        error.Code.ShouldBe("USER_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Регистр_адреса_сохраняется_как_ввели()
    {
        // Сравнение регистронезависимо, но человек видит свой адрес таким, каким написал.
        database.Actor.AsSuperuser();

        var address = "Ivan.Petrov" + Guid.CreateVersion7().ToString("N")[..8] + "@Example.COM";
        var (user, _) = await CreateAsync(address);

        user.Email.ShouldBe(address);
    }

    [Fact]
    public async Task Удаление_освобождает_адрес()
    {
        // ⚠ Ради этого уникальный индекс частичный. Обычный не дал бы завести человека
        // заново: строка остаётся навсегда, потому что на неё ссылается журнал.
        database.Actor.AsSuperuser();

        var address = Email();
        var (first, _) = await CreateAsync(address);

        await database.InScopeAsync(users => users.DeleteAsync(first.Id, Ct));

        var (second, _) = await CreateAsync(address);

        second.Id.ShouldNotBe(first.Id);
    }

    // --- активация -----------------------------------------------------------------------

    [Fact]
    public async Task Активация_задаёт_пароль_и_отмечает_время()
    {
        database.Actor.AsSuperuser();

        var (user, invitation) = await CreateAsync(Email());

        var activated = await database.InScopeAsync(
            users => users.ActivateAsync(invitation!.Token, Strong, Ct));

        activated.ActivatedAt.ShouldNotBeNull();
        activated.PasswordChangedAt.ShouldNotBeNull();
        activated.MustChangePassword.ShouldBeFalse();

        var hasher = new PasswordHasher();
        (await hasher.VerifyAsync(activated.PasswordHash, Strong, Ct)).ShouldBeTrue();
        _ = user;
    }

    [Fact]
    public async Task Ссылка_гасится_и_второй_раз_не_работает()
    {
        // ⚠ Условие стоит в SQL, а не в проверке кода: два одновременных запроса иначе
        // задали бы два разных пароля, и второй затёр бы первый молча.
        database.Actor.AsSuperuser();

        var (_, invitation) = await CreateAsync(Email());

        await database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, Strong, Ct));

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, Strong + "2", Ct)));

        error.Code.ShouldBe("ACTIVATION_TOKEN_INVALID");
    }

    [Fact]
    public async Task Одновременные_активации_проходят_ровно_одной()
    {
        // Настоящая гонка, а не её изображение: восемь параллельных запросов по одной
        // ссылке. Выиграть должен ровно один, иначе пароль оказался бы не тем, который
        // человек ввёл.
        database.Actor.AsSuperuser();

        var (_, invitation) = await CreateAsync(Email());

        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            try
            {
                await database.InScopeAsync(
                    users => users.ActivateAsync(invitation!.Token, Strong + index, Ct));

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }));

        var results = await Task.WhenAll(attempts);

        results.Count(ok => ok).ShouldBe(1);
    }

    [Fact]
    public async Task Новое_приглашение_гасит_прежнее()
    {
        // «Выслать заново» означает «прежнее перестало работать». Иначе у человека на руках
        // несколько действующих ссылок, и отозвать их разом нечем.
        database.Actor.AsSuperuser();

        var (user, first) = await CreateAsync(Email());
        var second = await database.InScopeAsync(users => users.ResendInvitationAsync(user.Id, Ct));

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.ActivateAsync(first!.Token, Strong, Ct)));

        error.Code.ShouldBe("ACTIVATION_TOKEN_INVALID");

        var activated = await database.InScopeAsync(users => users.ActivateAsync(second.Token, Strong, Ct));
        activated.ActivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Просроченная_ссылка_не_работает()
    {
        database.Actor.AsSuperuser();
        database.Settings.Values["auth.invitation_ttl_minutes"] = 5;

        try
        {
            var (user, invitation) = await CreateAsync(Email());

            // Сдвигаем срок в прошлое прямо в базе: подменять часы всему модулю ради одного
            // теста значило бы сдвинуть время и всем остальным — контейнер один на прогон.
            await database.ReadAsync(async db =>
                await db.Set<IdentityToken>()
                    .Where(row => row.UserId == user.Id)
                    .ExecuteUpdateAsync(
                        row => row.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

            var error = await Should.ThrowAsync<AppException>(() =>
                database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, Strong, Ct)));

            error.Code.ShouldBe("ACTIVATION_TOKEN_INVALID");
        }
        finally
        {
            database.Settings.Values.Remove("auth.invitation_ttl_minutes");
        }
    }

    [Fact]
    public async Task Просроченная_ссылка_не_показывает_даже_адреса()
    {
        // ⚠ Отдельная проверка от предыдущей, и не дублирующая её. Гашение ссылки и ЧТЕНИЕ
        // ссылки — разные пути: экран активации спрашивает «кого приглашают» до того, как
        // человек что-то ввёл. Не проверяй чтение срок — просроченная ссылка подтверждала бы
        // существование адреса, то есть отвечала бы на вопрос, который снаружи задавать
        // нельзя. Мутация «убрать срок из чтения» проходила зелёной, пока этого теста не
        // было: гашение ловило её позже и по другой причине.
        database.Actor.AsSuperuser();

        var (user, invitation) = await CreateAsync(Email());

        await database.ReadAsync(async db =>
            await db.Set<IdentityToken>()
                .Where(row => row.UserId == user.Id)
                .ExecuteUpdateAsync(
                    row => row.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

        var peeked = await database.InScopeAsync(users => users.PeekActivationAsync(invitation!.Token, Ct));

        peeked.ShouldBeNull();
    }

    [Fact]
    public async Task Погашенная_ссылка_тоже_не_показывает_адреса()
    {
        database.Actor.AsSuperuser();

        var (_, invitation) = await CreateAsync(Email());
        await database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, Strong, Ct));

        var peeked = await database.InScopeAsync(users => users.PeekActivationAsync(invitation!.Token, Ct));

        peeked.ShouldBeNull();
    }

    [Fact]
    public async Task Отказ_в_активации_одинаков_для_всех_причин()
    {
        // ⚠ Различать «нет ссылки», «просрочена» и «уже использована» значило бы
        // подсказывать перебирающему, насколько он близок.
        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.ActivateAsync("ссылки-такой-нет", Strong, Ct)));

        error.Code.ShouldBe("ACTIVATION_TOKEN_INVALID");
        error.Details.ShouldBeNull("подробности отказа наружу не уходят");
    }

    [Fact]
    public async Task Отказ_в_активации_пишется_в_журнал()
    {
        // Снаружи все отказы одинаковы намеренно — а внутри их надо уметь сосчитать. Это
        // единственный след перебора ссылок.
        var before = database.Audit.Entries.Count;

        await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.ActivateAsync("ссылки-такой-нет", Strong, Ct)));

        database.Audit.Entries.Skip(before)
            .ShouldContain(entry => entry.Action == IdentityActions.ActivationRejected);
    }

    [Fact]
    public async Task Непригодный_пароль_не_сжигает_ссылку()
    {
        // ⚠ Всё, что может отказать по вине человека, проверяется ДО гашения: иначе одна
        // опечатка требовала бы просить новое приглашение у администратора.
        database.Actor.AsSuperuser();

        var (_, invitation) = await CreateAsync(Email());

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, "коротко", Ct)));

        error.Code.ShouldBe("PASSWORD_POLICY_VIOLATION");

        // И ссылка по-прежнему работает.
        var activated = await database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, Strong, Ct));
        activated.ActivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Активированного_повторно_не_приглашают()
    {
        database.Actor.AsSuperuser();

        var (user, invitation) = await CreateAsync(Email());
        await database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, Strong, Ct));

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.ResendInvitationAsync(user.Id, Ct)));

        error.Code.ShouldBe("USER_ALREADY_ACTIVATED");
    }

    [Fact]
    public async Task Включение_обратно_пишет_своё_событие()
    {
        // ⚠ Событие user.enable было объявлено с первого дня и не писалось ни одной строкой
        // кода: фильтр журнала предлагал категорию, в которой не бывает записей. Включение
        // шло правкой профиля — то есть ещё и мимо права user.disable.
        database.Actor.AsSuperuser();

        var (user, _) = await CreateAsync(Email(), Strong);

        await database.InScopeAsync(users => users.DisableAsync(user.Id, Ct));

        var enabled = await database.InScopeAsync(users => users.EnableAsync(user.Id, Ct));

        enabled.IsActive.ShouldBeTrue();
        database.Audit.About(user.Id).ShouldContain(entry => entry.Action.Name == "user.enable");
    }

    [Fact]
    public async Task Включение_уже_включённого_ничего_не_пишет()
    {
        // Строка «включил включённого» — шум, а журнал безопасности от шума уже чистили.
        database.Actor.AsSuperuser();

        var (user, _) = await CreateAsync(Email(), Strong);

        await database.InScopeAsync(users => users.EnableAsync(user.Id, Ct));

        database.Audit.About(user.Id).ShouldNotContain(entry => entry.Action.Name == "user.enable");
    }

    [Fact]
    public async Task Гонка_по_одному_адресу_кончается_отказом_а_не_пятисоткой()
    {
        // Проверка занятости проходит у обоих, а частичный уникальный индекс пропускает
        // одного. Без перевода наружу уезжала бы пятисотка вместо внятного «адрес занят».
        database.Actor.AsSuperuser();

        var email = Email();

        var attempts = await Task.WhenAll(
            Attempt(email),
            Attempt(email));

        attempts.Count(result => result is null).ShouldBe(1, "ровно один должен завестись");
        attempts.ShouldContain(code => code == "USER_ALREADY_EXISTS");

        async Task<string?> Attempt(string address)
        {
            try
            {
                await CreateAsync(address, Strong);

                return null;
            }
            catch (AppException error)
            {
                return error.Code;
            }
        }
    }

    [Fact]
    public async Task Блокировку_можно_снять_не_дожидаясь_срока()
    {
        // ⚠ Снять её было нечем вовсе: оставалось ждать пятнадцать минут, в том числе тому,
        // кто трижды промахнулся мимо раскладки.
        database.Actor.AsSuperuser();

        var (user, _) = await CreateAsync(Email(), Strong);

        await database.ReadAsync(db => db.Set<User>()
            .Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.LockedUntil, DateTimeOffset.UtcNow.AddMinutes(15))
                    .SetProperty(row => row.FailedLoginCount, 10),
                Ct));

        var unlocked = await database.InScopeAsync(users => users.UnlockAsync(user.Id, Ct));

        unlocked.LockedUntil.ShouldBeNull();
        unlocked.FailedLoginCount.ShouldBe(0);
        database.Audit.About(user.Id).ShouldContain(entry => entry.Action.Name == "user.unlock");
    }

    [Fact]
    public async Task Снятие_несуществующей_блокировки_ничего_не_пишет()
    {
        database.Actor.AsSuperuser();

        var (user, _) = await CreateAsync(Email(), Strong);

        await database.InScopeAsync(users => users.UnlockAsync(user.Id, Ct));

        database.Audit.About(user.Id).ShouldNotContain(entry => entry.Action.Name == "user.unlock");
    }

    [Fact]
    public async Task Своё_имя_человек_меняет_сам()
    {
        // До 2026-08-22 профиль только читался: изменить своё имя было нельзя никак.
        database.Actor.AsSuperuser();

        var (user, _) = await CreateAsync(Email(), Strong);

        database.Actor.Become(user.Id);

        var changed = await database.InScopeAsync(users => users.UpdateOwnProfileAsync("Новое Имя", Ct));

        changed.DisplayName.ShouldBe("Новое Имя");
    }

    [Fact]
    public async Task Ссылка_читается_и_гасится_только_своим_назначением()
    {
        // ⚠ Написан по следу мутаций: назначение проверяется ДВАЖДЫ — при чтении и при
        // гашении, — и поэтому ни одна из двух проверок не подтверждалась сквозным тестом:
        // снимешь любую, вторая спасёт. Защита в два слоя — это хорошо, но слой, который
        // можно снять незамеченным, однажды снимут.
        database.Actor.AsSuperuser();

        var (user, _) = await CreateAsync(Email(), Strong);

        await using var scope = database.Scope();

        var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();
        var issued = await invitations.IssueAsync(user.Id, Purposes.Reset, cancellationToken: Ct);

        // IssueAsync только готовит строку — сохраняет вызывающий, потому что ссылка обязана
        // уехать в базу той же транзакцией, что и само действие.
        await scope.ServiceProvider.GetRequiredService<EmailSslSender.Shared.Data.SenderDbContext>()
            .SaveChangesAsync(Ct);

        // Чужим назначением ссылка не читается...
        (await invitations.PeekAsync(issued.Token, Purposes.Activation, Ct)).ShouldBeNull();

        // ...и не гасится.
        (await invitations.ConsumeAsync(issued.Token, Purposes.Activation, Ct)).ShouldBeNull();

        // А своим — работает: значит дело именно в назначении, а не в самой ссылке.
        (await invitations.PeekAsync(issued.Token, Purposes.Reset, Ct)).ShouldBe(user.Id);
        (await invitations.ConsumeAsync(issued.Token, Purposes.Reset, Ct)).ShouldBe(user.Id);
    }

    [Fact]
    public async Task Ссылка_может_жить_своим_сроком()
    {
        // ⚠ Расписка второго фактора живёт минуты, приглашение — час. Один срок на двоих
        // означал бы либо расписку длиной в час (подсмотренная в логах прокси, она стоила бы
        // дорого), либо приглашение длиной в пять минут (человек не успел бы им
        // воспользоваться).
        database.Actor.AsSuperuser();

        var (user, _) = await CreateAsync(Email(), Strong);

        await using var scope = database.Scope();

        var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();
        var now = DateTimeOffset.UtcNow;

        var short_ = await invitations.IssueAsync(
            user.Id, Purposes.TwoFactor, TimeSpan.FromMinutes(3), cancellationToken: Ct);
        var long_ = await invitations.IssueAsync(user.Id, Purposes.Activation, cancellationToken: Ct);

        (short_.ExpiresAt - now).ShouldBeLessThan(TimeSpan.FromMinutes(5));
        (long_.ExpiresAt - now).ShouldBeGreaterThan(TimeSpan.FromMinutes(30));
    }

    // --- обвязка -------------------------------------------------------------------------

    /// <summary>
    /// Удаление уносит за человеком всё, что продолжало бы что-то значить.
    /// </summary>
    /// <remarks>
    /// ⚠ Удаление у нас мягкое — строка в <c>users</c> остаётся, — и поэтому каскады базы не
    /// срабатывают <b>никогда</b>. До 2026-08-22 после удаления оставались роли, членство в
    /// командах, выданные доступы, запечатанный секрет второго фактора со своим ключом данных,
    /// коды восстановления и непогашенные ссылки. Для менеджера учётных данных это не мусор,
    /// а хранение чужих секретов после увольнения; а роли и членство — не история, а
    /// действующая привязка.
    ///
    /// История при этом остаётся в журнале: здесь уносится доступ, а не память о человеке.
    /// </remarks>
    [Fact]
    public async Task Удаление_гасит_ссылки()
    {
        database.Actor.AsSuperuser();

        var email = $"swept-{Guid.CreateVersion7():N}@example.com";
        var (user, invitation) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "На увольнение", null), Ct));


        await database.InScopeAsync<bool>(async users =>
        {
            await users.DeleteAsync(user.Id, Ct);

            return true;
        });

        await database.ReadAsync(async db =>
        {
            // Ссылка не удалена, а погашена: след того, что её выпускали, остаётся.
            var link = await db.Set<IdentityToken>()
                .FirstAsync(row => row.UserId == user.Id, Ct);

            link.UsedAt.ShouldNotBeNull("непогашенная ссылка пережила удаление");

            return true;
        });

        invitation.ShouldNotBeNull();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Пароль, проходящий требования по умолчанию.</summary>
    private const string Strong = "Очень-Хороший-Пароль-42!";

    /// <summary>Свой адрес на каждый тест: таблица одна на прогон.</summary>
    private static string Email() => $"user-{Guid.CreateVersion7():N}@example.com";

    private Task<(User User, Invitation? Invitation)> CreateAsync(
        string email, string? password = null) =>
        database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Тестовый человек", password), Ct));
}
