using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
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
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>Восстановление из консоли: цель, политика, отзыв доступа и обязательная новая 2FA.</summary>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class SuperuserRecoveryTests(IdentityDatabase database) : IAsyncLifetime
{
    private const string OldPassword = "Очень-Хороший-Пароль-42!";
    private const string NewPassword = "Другой-Надёжный-Пароль-73!";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        ConsoleActor();
        database.Settings.Values.Clear();
        database.Почта.Настроена = false;
        database.Почта.Исход = MailQueueResult.NotConfigured;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        database.Actor.AsSuperuser();
        database.Settings.Values.Clear();

        // ⚠ Почта — общее состояние коллекции: оставленная настроенной, она требует код письмом
        // у каждого, кто фактор ещё не подтверждал, и краснит чужие файлы.
        database.Почта.Настроена = false;
        database.Почта.Исход = MailQueueResult.NotConfigured;
        return ValueTask.CompletedTask;
    }

    private async Task<User> SuperuserAsync()
    {
        var email = $"console-{Guid.NewGuid():N}@example.com";
        var id = await IdentityModule.BootstrapSuperuserAsync(database.Services, email, "Console admin", OldPassword, Ct);
        return await database.ReadAsync(db => db.Set<User>().SingleAsync(user => user.Id == id, Ct));
    }

    private void ConsoleActor()
    {
        database.Actor.Id = null;
        database.Actor.SessionId = null;
        database.Actor.StepUpAt = null;
    }

    [Fact]
    public async Task Смена_пароля_из_консоли_отзывает_прежний_вход_и_ссылки()
    {
        var user = await SuperuserAsync();
        await using var scope = database.Scope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        var oldSession = await signIn.SignInAsync(user.Email, OldPassword, new RequestOrigin(null, "console-test"), cancellationToken: Ct);
        var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();
        foreach (var purpose in new[] { Purposes.Reset, Purposes.Activation, Purposes.TwoFactor, Purposes.LoginCode, Purposes.StepUpCode })
        {
            await invitations.IssueAsync(user.Id, purpose, cancellationToken: Ct);
        }
        await scope.ServiceProvider.GetRequiredService<EmailSslSender.Shared.Data.SenderDbContext>().SaveChangesAsync(Ct);

        (await IdentityModule.SetSuperuserPasswordAsync(database.Services, "  " + user.Email.ToUpperInvariant() + "  ", NewPassword, Ct)).ShouldBe(user.Id);

        var fresh = await database.ReadAsync(db => db.Set<User>().SingleAsync(row => row.Id == user.Id, Ct));
        (await new PasswordHasher(Argon2Profile.Cheap).VerifyAsync(fresh.PasswordHash, NewPassword, Ct)).ShouldBeTrue();
        (await new PasswordHasher(Argon2Profile.Cheap).VerifyAsync(fresh.PasswordHash, OldPassword, Ct)).ShouldBeFalse();
        (await database.ReadAsync(db => db.Set<IdentityToken>().CountAsync(row => row.UserId == user.Id && row.UsedAt == null, Ct))).ShouldBe(0);
        await using var check = database.Scope();
        await Should.ThrowAsync<AppException>(() => check.ServiceProvider.GetRequiredService<IIdentityResolver>()
            .ResolveAsync(new Request(oldSession.Pair.Access), Ct));
        var audit = database.Audit.Entries.Last(entry => entry.Action == IdentityActions.PasswordChanged);
        audit.ActorType.ShouldBe(AuditActorType.System);
        audit.ActorId.ShouldBeNull();
        audit.Changes!["via_cli"].ShouldBe(true);
        string.Join('|', audit.Changes.Values).ShouldNotContain(NewPassword);
    }

    [Fact]
    public async Task Непригодный_пароль_не_меняет_учётную_запись()
    {
        var user = await SuperuserAsync();
        (await Should.ThrowAsync<AppException>(() => IdentityModule.SetSuperuserPasswordAsync(database.Services, user.Email, "weak", Ct)))
            .Code.ShouldBe(ErrorCodes.PasswordPolicyViolation);
        (await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id).Select(row => row.PasswordHash).SingleAsync(Ct)))
            .ShouldBe(user.PasswordHash);
        database.Audit.Entries.ShouldNotContain(entry => entry.Action == IdentityActions.PasswordChanged && entry.ResourceId == user.Id);
    }

    [Fact]
    public async Task Сброс_2ФА_из_консоли_закрывает_сессии_и_возвращает_к_настройке()
    {
        var user = await SuperuserAsync();
        await using var scope = database.Scope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        var initial = await signIn.SignInAsync(user.Email, OldPassword, new RequestOrigin(null, "console-test"), cancellationToken: Ct);
        database.Actor.Become(user.Id);
        var factors = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        var setup = await factors.SetupAsync(Ct);
        await factors.ConfirmAsync(TestTotp.Code(setup.Secret), Ct);
        ConsoleActor();
        var challenge = await Should.ThrowAsync<AppException>(() => signIn.SignInAsync(
            user.Email, OldPassword, new RequestOrigin(null, "console-factor"), cancellationToken: Ct));
        initial = await signIn.CompleteTwoFactorAsync((string)challenge.Details!["challenge"]!,
            TestTotp.Code(setup.Secret, steps: 1), new RequestOrigin(null, "console-factor"), cancellationToken: Ct);
        await using (var before = database.Scope())
        {
            (await before.ServiceProvider.GetRequiredService<IIdentityResolver>().ResolveAsync(new Request(initial.Pair.Access), Ct))!
                .MustEnrollTwoFactor.ShouldBeFalse();
        }
        await IssueFactorTokensAsync(user.Id);

        (await IdentityModule.ResetSuperuserTwoFactorAsync(database.Services, user.Email, Ct)).ShouldBe(user.Id);
        (await database.ReadAsync(db => db.Set<UserTwoFactor>().AnyAsync(row => row.UserId == user.Id, Ct))).ShouldBeFalse();
        (await database.ReadAsync(db => db.Set<IdentityToken>().CountAsync(row => row.UserId == user.Id && row.UsedAt == null, Ct))).ShouldBe(0);
        (await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id).Select(row => row.TwoFactorConfiguredAt).SingleAsync(Ct))).ShouldBeNull();
        await using var check = database.Scope();
        await Should.ThrowAsync<AppException>(() => check.ServiceProvider.GetRequiredService<IIdentityResolver>().ResolveAsync(new Request(initial.Pair.Access), Ct));
        var next = await check.ServiceProvider.GetRequiredService<SignInService>()
            .SignInAsync(user.Email, OldPassword, new RequestOrigin(null, "console-next"), cancellationToken: Ct);
        (await check.ServiceProvider.GetRequiredService<IIdentityResolver>().ResolveAsync(new Request(next.Pair.Access), Ct))!
            .MustEnrollTwoFactor.ShouldBeTrue();
        database.Audit.Entries.Last(entry => entry.Action == IdentityActions.TwoFactorReset).ActorType.ShouldBe(AuditActorType.System);
    }

    [Fact]
    public async Task Повторный_сброс_2ФА_тоже_закрывает_ограниченный_вход()
    {
        var user = await SuperuserAsync();
        await using var scope = database.Scope();
        var initial = await scope.ServiceProvider.GetRequiredService<SignInService>()
            .SignInAsync(user.Email, OldPassword, new RequestOrigin(null, "console-test"), cancellationToken: Ct);
        await IdentityModule.ResetSuperuserTwoFactorAsync(database.Services, user.Email, Ct);
        await using var check = database.Scope();
        await Should.ThrowAsync<AppException>(() => check.ServiceProvider.GetRequiredService<IIdentityResolver>()
            .ResolveAsync(new Request(initial.Pair.Access), Ct));
    }

    [Fact]
    public async Task Сброс_2ФА_снимает_блокировку_после_неверных_кодов()
    {
        var user = await SuperuserAsync();
        await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.FailedLoginCount, 10)
            .SetProperty(row => row.LockedUntil, DateTimeOffset.UtcNow.AddHours(1)), Ct));
        await IdentityModule.ResetSuperuserTwoFactorAsync(database.Services, user.Email, Ct);
        var fresh = await database.ReadAsync(db => db.Set<User>().SingleAsync(row => row.Id == user.Id, Ct));
        fresh.LockedUntil.ShouldBeNull();
        fresh.FailedLoginCount.ShouldBe(0);
    }

    /// <summary>Без почты первичная настройка суперпользователя идёт по паролю — после заведения и после сброса из консоли.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Первичная_настройка_суперпользователя_без_почты_идёт_по_паролю(bool afterReset)
    {
        var user = await SuperuserAsync(afterReset);
        await using var scope = database.Scope();
        var initial = await scope.ServiceProvider.GetRequiredService<SignInService>()
            .SignInAsync(user.Email, OldPassword, new RequestOrigin(null, "console-no-mail"), cancellationToken: Ct);
        (await scope.ServiceProvider.GetRequiredService<IIdentityResolver>().ResolveAsync(new Request(initial.Pair.Access), Ct))!
            .MustEnrollTwoFactor.ShouldBeTrue();
    }

    /// <summary>
    /// При настроенной почте первичная настройка требует кода письмом, и отказ почты не превращает
    /// её во вход по одному паролю.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>Заменяет факт «первичная настройка не зависит от старого SMTP»</b> (ревью
    /// безопасности 2026-10-20). Тот закреплял сессию настройки по одному паролю при любой почте —
    /// а в ней узнавший пароль подключал свой аутентификатор и становился полноправным
    /// администратором. Откат на «без кода», когда очередь отказала, вернул бы ту же дверь: отказ
    /// очереди (предел частоты) вызывается снаружи. Цена названа: при настроенной, но неработающей
    /// почте восстановлению из консоли нужна починенная почта или снятый адрес SMTP.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Отказ_почты_не_превращает_первичную_настройку_во_вход_по_паролю(bool afterReset)
    {
        var user = await SuperuserAsync(afterReset);
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.RateLimited;
        await using var scope = database.Scope();

        var отказ = await Should.ThrowAsync<AppException>(() => scope.ServiceProvider.GetRequiredService<SignInService>()
            .SignInAsync(user.Email, OldPassword, new RequestOrigin(null, "console-bad-mail"), cancellationToken: Ct));

        отказ.Code.ShouldBe(ErrorCodes.RateLimited, "отказ почты обошли входом по одному паролю");
    }

    /// <summary>Суперпользователь из консоли — заведённый и, если просят, со сброшенным из консоли фактором.</summary>
    private async Task<User> SuperuserAsync(bool afterReset)
    {
        var user = await SuperuserAsync();
        if (afterReset)
        {
            await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.TwoFactorConfiguredAt, DateTimeOffset.UtcNow), Ct));
            await IdentityModule.ResetSuperuserTwoFactorAsync(database.Services, user.Email, Ct);
        }

        return user;
    }

    [Fact]
    public async Task Настроенная_почтовая_2ФА_суперпользователя_не_превращается_в_первичную_настройку()
    {
        var user = await SuperuserAsync();
        await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.TwoFactorConfiguredAt, DateTimeOffset.UtcNow), Ct));
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;
        await using var scope = database.Scope();
        (await Should.ThrowAsync<AppException>(() => scope.ServiceProvider.GetRequiredService<SignInService>()
            .SignInAsync(user.Email, OldPassword, new RequestOrigin(null, "configured-admin"), cancellationToken: Ct)))
            .Code.ShouldBe(ErrorCodes.TwoFactorRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Удалённого_суперпользователя_восстановление_не_возвращает(bool factor)
    {
        var user = await SuperuserAsync();
        await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.DeletedAt, DateTimeOffset.UtcNow), Ct));
        var error = await Should.ThrowAsync<AppException>(async () =>
        {
            if (factor) await IdentityModule.ResetSuperuserTwoFactorAsync(database.Services, user.Email, Ct);
            else await IdentityModule.SetSuperuserPasswordAsync(database.Services, user.Email, NewPassword, Ct);
        });
        error.Code.ShouldBe(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Смена_пароля_сохраняет_TOTP_и_не_включает_отключённого_администратора()
    {
        var user = await SuperuserAsync();
        database.Actor.Become(user.Id);
        await using (var scope = database.Scope())
        {
            var factors = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
            var setup = await factors.SetupAsync(Ct);
            await factors.ConfirmAsync(TestTotp.Code(setup.Secret), Ct);
        }
        ConsoleActor();
        await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.IsActive, false)
            .SetProperty(row => row.FailedLoginCount, 10)
            .SetProperty(row => row.LockedUntil, DateTimeOffset.UtcNow.AddHours(1)), Ct));

        await IdentityModule.SetSuperuserPasswordAsync(database.Services, user.Email, NewPassword, Ct);
        var fresh = await database.ReadAsync(db => db.Set<User>().SingleAsync(row => row.Id == user.Id, Ct));
        fresh.IsActive.ShouldBeFalse();
        fresh.LockedUntil.ShouldBeNull();
        fresh.FailedLoginCount.ShouldBe(0);
        fresh.TwoFactorConfiguredAt.ShouldNotBeNull();
        (await database.ReadAsync(db => db.Set<UserTwoFactor>().AnyAsync(row => row.UserId == user.Id && row.ConfirmedAt != null, Ct)))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Отказ_базы_не_оставляет_частичное_восстановление(bool factor)
    {
        var user = await SuperuserAsync();
        await using var scope = database.Scope();
        var initial = await scope.ServiceProvider.GetRequiredService<SignInService>()
            .SignInAsync(user.Email, OldPassword, new RequestOrigin(null, "console-atomic"), cancellationToken: Ct);
        await IssueFactorTokensAsync(user.Id);
        var constraint = "console_recovery_" + Guid.NewGuid().ToString("N");
        // Имена/значения состоят только из UUID теста; параметр SQL не может быть именем DDL.
        var installConstraint = $"ALTER TABLE users ADD CONSTRAINT {constraint} CHECK (id <> '{user.Id}') NOT VALID";
        var removeConstraint = $"ALTER TABLE users DROP CONSTRAINT {constraint}";
        await database.ReadAsync(db => db.Database.ExecuteSqlRawAsync(installConstraint, Ct));
        try
        {
            await Should.ThrowAsync<DbUpdateException>(async () =>
            {
                if (factor) await IdentityModule.ResetSuperuserTwoFactorAsync(database.Services, user.Email, Ct);
                else await IdentityModule.SetSuperuserPasswordAsync(database.Services, user.Email, NewPassword, Ct);
            });
            (await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id).Select(row => row.PasswordHash).SingleAsync(Ct)))
                .ShouldBe(user.PasswordHash);
            (await database.ReadAsync(db => db.Set<IdentityToken>().CountAsync(row => row.UserId == user.Id && row.UsedAt == null, Ct))).ShouldBe(3);
            await using var check = database.Scope();
            await Should.NotThrowAsync(() => check.ServiceProvider.GetRequiredService<IIdentityResolver>()
                .ResolveAsync(new Request(initial.Pair.Access), Ct));
        }
        finally
        {
            await database.ReadAsync(db => db.Database.ExecuteSqlRawAsync(removeConstraint, Ct));
        }
    }

    private async Task IssueFactorTokensAsync(Guid userId)
    {
        await using var scope = database.Scope();
        var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();
        foreach (var purpose in new[] { Purposes.TwoFactor, Purposes.LoginCode, Purposes.StepUpCode })
        {
            await invitations.IssueAsync(userId, purpose, cancellationToken: Ct);
        }
        await scope.ServiceProvider.GetRequiredService<SenderDbContext>().SaveChangesAsync(Ct);
    }

    private sealed class Request(string access) : IIdentityRequest
    {
        public string? Header(string name) => name == "Authorization" ? $"Bearer {access}" : null;
    }
}
