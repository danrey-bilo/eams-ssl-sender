using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Passwords;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Консольная <c>reset-access</c>: пароль, второй фактор, блокировка, сессии, ссылки — разом и
/// одной транзакцией.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Главный факт файла — атомарность.</b> Команда собрана из двух служб (смена пароля и
/// снятие фактора); сбой второй после первой не должен оставлять учётную запись с новым паролем и
/// прежним аутентификатором. Проверяется настоящим отказом базы на втором шаге.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class AccessRecoveryTests(IdentityDatabase database) : IAsyncLifetime
{
    private const string OldPassword = "Очень-Хороший-Пароль-42!";
    private const string NewPassword = "Другой-Надёжный-Пароль-73!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RequestOrigin Откуда = new(null, "reset-access-test");

    public ValueTask InitializeAsync()
    {
        Консоль();
        database.Settings.Values.Clear();
        database.Почта.Настроена = false;
        database.Почта.Исход = MailQueueResult.NotConfigured;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        database.Actor.AsSuperuser();
        database.Settings.Values.Clear();
        database.Почта.Настроена = false;
        database.Почта.Исход = MailQueueResult.NotConfigured;
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Восстановление_меняет_пароль_снимает_фактор_блокировку_сессии_и_ссылки()
    {
        var (user, session) = await ЗапертыйСФакторомAsync();

        var итог = await IdentityModule.ResetAccessAsync(
            database.Services, "  " + user.Email.ToUpperInvariant() + "  ", NewPassword, Ct);

        итог.UserId.ShouldBe(user.Id);
        итог.Email.ShouldBe(user.Email);
        итог.IsActive.ShouldBeTrue();
        итог.HadTwoFactor.ShouldBeTrue();
        итог.SessionsRevoked.ShouldBe(1);

        var fresh = await database.ReadAsync(db => db.Set<User>().SingleAsync(row => row.Id == user.Id, Ct));
        var hasher = new PasswordHasher(Argon2Profile.Cheap);
        (await hasher.VerifyAsync(fresh.PasswordHash, NewPassword, Ct)).ShouldBeTrue();
        (await hasher.VerifyAsync(fresh.PasswordHash, OldPassword, Ct)).ShouldBeFalse();
        fresh.MustChangePassword.ShouldBeFalse();
        fresh.LockedUntil.ShouldBeNull();
        fresh.FailedLoginCount.ShouldBe(0);
        fresh.TwoFactorConfiguredAt.ShouldBeNull();
        fresh.IsActive.ShouldBeTrue();

        (await database.ReadAsync(db => db.Set<UserTwoFactor>().AnyAsync(row => row.UserId == user.Id, Ct)))
            .ShouldBeFalse("аутентификатор пережил восстановление доступа");
        (await database.ReadAsync(db => db.Set<IdentityToken>()
                .CountAsync(row => row.UserId == user.Id && row.UsedAt == null, Ct)))
            .ShouldBe(0, "ссылка, расписка или код пережили восстановление доступа");

        await using (var check = database.Scope())
        {
            await Should.ThrowAsync<AppException>(() => check.ServiceProvider.GetRequiredService<IIdentityResolver>()
                .ResolveAsync(new Заголовком(session.Access), Ct));
        }

        // Следующий вход — новым паролем и сразу в сессию настройки: аутентификатор подключается
        // заново, прежний секрет больше ничего не открывает.
        await using (var next = database.Scope())
        {
            var вход = await next.ServiceProvider.GetRequiredService<SignInService>()
                .SignInAsync(user.Email, NewPassword, Откуда, cancellationToken: Ct);

            (await next.ServiceProvider.GetRequiredService<IIdentityResolver>()
                    .ResolveAsync(new Заголовком(вход.Pair.Access), Ct))!
                .MustEnrollTwoFactor.ShouldBeTrue();
        }

        var пароль = database.Audit.About(user.Id).Last(entry => entry.Action == IdentityActions.PasswordChanged);
        пароль.ActorType.ShouldBe(AuditActorType.System);
        пароль.Changes!["via_cli"].ShouldBe(true);
        string.Join('|', пароль.Changes.Values).ShouldNotContain(NewPassword);

        var фактор = database.Audit.About(user.Id).Last(entry => entry.Action == IdentityActions.TwoFactorReset);
        фактор.ActorType.ShouldBe(AuditActorType.System);
        фактор.Changes!["via_cli"].ShouldBe(true);
    }

    [Fact]
    public async Task Отключённую_учётную_запись_восстановление_не_включает_и_говорит_об_этом()
    {
        var (user, _) = await ЗапертыйСФакторомAsync();
        await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), Ct));

        var итог = await IdentityModule.ResetAccessAsync(database.Services, user.Email, NewPassword, Ct);

        итог.IsActive.ShouldBeFalse("итог промолчал об отключённой учётной записи");

        var fresh = await database.ReadAsync(db => db.Set<User>().SingleAsync(row => row.Id == user.Id, Ct));
        fresh.IsActive.ShouldBeFalse("восстановление доступа включило отключённую учётную запись");
        (await new PasswordHasher(Argon2Profile.Cheap).VerifyAsync(fresh.PasswordHash, NewPassword, Ct)).ShouldBeTrue();
        fresh.TwoFactorConfiguredAt.ShouldBeNull();
    }

    [Fact]
    public async Task Без_фактора_восстановление_тоже_проходит_и_так_и_говорит()
    {
        var email = $"reset-access-{Guid.NewGuid():N}@example.com";
        var id = await IdentityModule.BootstrapSuperuserAsync(database.Services, email, "Console admin", OldPassword, Ct);

        var итог = await IdentityModule.ResetAccessAsync(database.Services, email, NewPassword, Ct);

        итог.UserId.ShouldBe(id);
        итог.HadTwoFactor.ShouldBeFalse();
        итог.SessionsRevoked.ShouldBe(0);
    }

    [Fact]
    public async Task Негодный_пароль_не_трогает_ни_пароля_ни_фактора_ни_сессий()
    {
        var (user, session) = await ЗапертыйСФакторомAsync();

        (await Should.ThrowAsync<AppException>(() =>
                IdentityModule.ResetAccessAsync(database.Services, user.Email, "weak", Ct)))
            .Code.ShouldBe(ErrorCodes.PasswordPolicyViolation);

        await НичегоНеТронутоAsync(user, session);
    }

    [Fact]
    public async Task Неизвестный_адрес_отвечает_не_найдено()
    {
        (await Should.ThrowAsync<AppException>(() => IdentityModule.ResetAccessAsync(
                database.Services, $"nobody-{Guid.NewGuid():N}@example.com", NewPassword, Ct)))
            .Code.ShouldBe(ErrorCodes.NotFound);
    }

    /// <summary>
    /// Отказ базы на снятии фактора — ПОСЛЕ сохранённой смены пароля — откатывает и смену пароля.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Без общей транзакции учётная запись осталась бы с новым паролем и прежним
    /// аутентификатором: по-прежнему запертой, но уже с паролем, который знает стоящий у консоли.
    /// Отказ ставится триггером на удаление строки фактора — второй шаг, после первого
    /// <c>SaveChanges</c>.
    /// </remarks>
    // мутация 2026-10-20: AccessRecovery звал две службы без общей транзакции → покраснел этот факт
    // (пароль сменился, аутентификатор остался).
    [Fact]
    public async Task Отказ_базы_на_снятии_фактора_откатывает_и_смену_пароля()
    {
        var (user, session) = await ЗапертыйСФакторомAsync();
        var метка = Guid.NewGuid().ToString("N");

        // Имена и значения — только UUID теста; параметр SQL не может быть именем DDL.
        var установить =
            $"CREATE FUNCTION reset_access_block_{метка}() RETURNS trigger LANGUAGE plpgsql AS "
            + "$$ BEGIN RAISE EXCEPTION 'reset-access test block'; END $$; "
            + $"CREATE TRIGGER reset_access_block_{метка} BEFORE DELETE ON user_two_factor "
            + $"FOR EACH ROW WHEN (OLD.user_id = '{user.Id}') EXECUTE FUNCTION reset_access_block_{метка}();";
        var снять =
            $"DROP TRIGGER IF EXISTS reset_access_block_{метка} ON user_two_factor; "
            + $"DROP FUNCTION IF EXISTS reset_access_block_{метка}();";

        await database.ReadAsync(db => db.Database.ExecuteSqlRawAsync(установить, Ct));

        try
        {
            await Should.ThrowAsync<DbUpdateException>(() =>
                IdentityModule.ResetAccessAsync(database.Services, user.Email, NewPassword, Ct));

            await НичегоНеТронутоAsync(user, session);
        }
        finally
        {
            await database.ReadAsync(db => db.Database.ExecuteSqlRawAsync(снять, CancellationToken.None));
        }
    }

    // --- обвязка ---------------------------------------------------------------------------

    /// <summary>Действует консоль: ни человека, ни сессии.</summary>
    private void Консоль()
    {
        database.Actor.Id = null;
        database.Actor.SessionId = null;
        database.Actor.StepUpAt = null;
    }

    /// <summary>
    /// Администратор с подтверждённым аутентификатором, живой сессией, блокировкой входа и
    /// непогашенными ссылками всех видов — всё, что восстановление обязано снять.
    /// </summary>
    private async Task<(User User, TokenPair Session)> ЗапертыйСФакторомAsync()
    {
        var email = $"reset-access-{Guid.NewGuid():N}@example.com";
        var id = await IdentityModule.BootstrapSuperuserAsync(database.Services, email, "Console admin", OldPassword, Ct);

        string secret;

        database.Actor.Become(id);

        await using (var scope = database.Scope())
        {
            var factors = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
            var setup = await factors.SetupAsync(Ct);
            await factors.ConfirmAsync(TestTotp.Code(setup.Secret), Ct);
            secret = setup.Secret;
        }

        Консоль();

        TokenPair session;

        await using (var scope = database.Scope())
        {
            var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
            var challenge = await Should.ThrowAsync<AppException>(() =>
                signIn.SignInAsync(email, OldPassword, Откуда, cancellationToken: Ct));

            session = (await signIn.CompleteTwoFactorAsync(
                (string)challenge.Details!["challenge"]!, TestTotp.Code(secret, steps: 1), Откуда,
                cancellationToken: Ct)).Pair;
        }

        await using (var scope = database.Scope())
        {
            var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();

            foreach (var purpose in new[] { Purposes.Reset, Purposes.Activation, Purposes.TwoFactor, Purposes.LoginCode, Purposes.StepUpCode })
            {
                await invitations.IssueAsync(id, purpose, cancellationToken: Ct);
            }

            await scope.ServiceProvider.GetRequiredService<SenderDbContext>().SaveChangesAsync(Ct);
        }

        await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.FailedLoginCount, 10)
            .SetProperty(row => row.LockedUntil, DateTimeOffset.UtcNow.AddHours(1)), Ct));

        var user = await database.ReadAsync(db => db.Set<User>().AsNoTracking().SingleAsync(row => row.Id == id, Ct));

        return (user, session);
    }

    /// <summary>Пароль, фактор, блокировка, ссылки и сессия — как были до попытки.</summary>
    private async Task НичегоНеТронутоAsync(User before, TokenPair session)
    {
        var fresh = await database.ReadAsync(db => db.Set<User>().SingleAsync(row => row.Id == before.Id, Ct));

        fresh.PasswordHash.ShouldBe(before.PasswordHash, "пароль сменился, хотя восстановление не состоялось");
        fresh.TwoFactorConfiguredAt.ShouldBe(before.TwoFactorConfiguredAt);
        fresh.LockedUntil.ShouldNotBeNull();
        (await database.ReadAsync(db => db.Set<UserTwoFactor>()
                .AnyAsync(row => row.UserId == before.Id && row.ConfirmedAt != null, Ct)))
            .ShouldBeTrue("аутентификатор снят, хотя восстановление не состоялось");
        (await database.ReadAsync(db => db.Set<IdentityToken>()
                .CountAsync(row => row.UserId == before.Id && row.UsedAt == null, Ct)))
            .ShouldBe(5);

        await using var check = database.Scope();
        await Should.NotThrowAsync(() => check.ServiceProvider.GetRequiredService<IIdentityResolver>()
            .ResolveAsync(new Заголовком(session.Access), Ct));
    }

    /// <summary>Пришедший с одним заголовком — больше распознавателю ничего не нужно.</summary>
    private sealed class Заголовком(string access) : IIdentityRequest
    {
        public string? Header(string name) => name == "Authorization" ? $"Bearer {access}" : null;
    }
}
