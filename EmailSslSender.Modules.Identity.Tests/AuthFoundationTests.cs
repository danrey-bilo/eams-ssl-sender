using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>Основа доступа: пароль не заменяет второй фактор, отключение окончательно отзывает токены.</summary>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class AuthFoundationTests(IdentityDatabase database) : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        database.Почта.Настроена = false;
        database.Почта.Исход = MailQueueResult.NotConfigured;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        database.Почта.Настроена = false;
        database.Почта.Исход = MailQueueResult.NotConfigured;
        database.Почта.Письма.Clear();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Пароль_даёт_только_настройку_даже_после_включения_почты()
    {
        var user = await UserAsync();
        var outcome = await SignInAsync(user.Email);
        (await ResolveAsync(outcome.Pair.Access)).MustEnrollTwoFactor.ShouldBeTrue();

        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;
        (await ResolveAsync(outcome.Pair.Access)).MustEnrollTwoFactor.ShouldBeTrue();
    }

    [Fact]
    public async Task Отключение_и_включение_не_возвращает_старую_сессию()
    {
        var user = await UserAsync();
        var outcome = await SignInAsync(user.Email);
        await database.InScopeAsync(service => service.DisableAsync(user.Id, Ct));
        await database.InScopeAsync(service => service.EnableAsync(user.Id, Ct));

        var refusal = await Should.ThrowAsync<AppException>(() => ResolveAsync(outcome.Pair.Access));
        refusal.Code.ShouldBe(ErrorCodes.UserInactive);
    }

    [Fact]
    public async Task Повторное_приглашение_уходит_почтой_без_секрета_в_ответе()
    {
        var (user, _) = await database.InScopeAsync(service => service.CreateAsync(
            new UserDraft($"invite-{Guid.NewGuid():N}@example.com"), Ct));
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;
        database.Адрес.Адрес = "https://sender.example.com";
        try
        {
            var invitation = await database.InScopeAsync(service => service.ResendInvitationAsync(user.Id, Ct));
            invitation.Token.ShouldBeEmpty();
            database.Почта.Письма.ShouldContain(letter => letter.Kind == "invitation" && letter.To == user.Email);
        }
        finally
        {
            database.Адрес.Адрес = null;
        }
    }

    [Fact]
    public async Task Код_почтой_завершает_вход_а_отключение_SMTP_не_ослабляет_следующий()
    {
        var user = await ConfiguredUserAsync();
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;
        var refusal = await Should.ThrowAsync<AppException>(() => SignInAsync(user.Email));
        refusal.Code.ShouldBe(ErrorCodes.TwoFactorRequired);
        var challenge = (string)refusal.Details!["challenge"]!;
        var code = database.Почта.Последнее(user.Email)!.Values["code"];
        await using var scope = database.Scope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        var outcome = await signIn.CompleteTwoFactorAsync(challenge, code, new RequestOrigin(null, "test"), cancellationToken: Ct);
        (await ResolveAsync(outcome.Pair.Access)).MustEnrollTwoFactor.ShouldBeFalse();
        (await Should.ThrowAsync<AppException>(() => signIn.CompleteTwoFactorAsync(challenge, code,
            new RequestOrigin(null, "test"), cancellationToken: Ct))).Code.ShouldBe(ErrorCodes.SessionExpired);
        database.Почта.Настроена = false;
        database.Почта.Исход = MailQueueResult.NotConfigured;
        (await Should.ThrowAsync<AppException>(() => SignInAsync(user.Email))).Code.ShouldBe(ErrorCodes.TwoFactorUnavailable);
    }

    [Fact]
    public async Task Код_действия_не_подходит_другой_сессии_того_же_человека()
    {
        var user = await UserAsync();
        var first = await SignInAsync(user.Email);
        var second = await SignInAsync(user.Email);
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;
        database.Actor.Become(user.Id, first.Pair.TokenId);
        await using (var scope = database.Scope())
            await scope.ServiceProvider.GetRequiredService<StepUpService>().SendEmailAsync(Ct);
        var code = database.Почта.Последнее(user.Email)!.Values["code"];
        database.Actor.Become(user.Id, second.Pair.TokenId);
        await using (var scope = database.Scope())
            (await Should.ThrowAsync<AppException>(() => scope.ServiceProvider.GetRequiredService<StepUpService>()
                .ConfirmAsync(code, Ct))).Code.ShouldBe(ErrorCodes.TwoFactorInvalid);
        database.Actor.Become(user.Id, first.Pair.TokenId);
        await using (var scope = database.Scope())
            await scope.ServiceProvider.GetRequiredService<StepUpService>().ConfirmAsync(code, Ct);
        (await ResolveAsync(first.Pair.Access)).MustEnrollTwoFactor.ShouldBeFalse();
        (await ResolveAsync(second.Pair.Access)).MustEnrollTwoFactor.ShouldBeTrue();
    }

    [Fact]
    public async Task Короткий_код_проверяет_назначение_привязку_и_повтор()
    {
        var user = await UserAsync();
        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();
        await invitations.IssueCodeAsync(user.Id, Purposes.LoginCode, TimeSpan.FromMinutes(5), "246810", Ct, "first-challenge");
        await db.SaveChangesAsync(Ct);
        (await invitations.ConsumeCodeAsync(user.Id, "246810", Purposes.StepUpCode, Ct, "first-challenge")).ShouldBeNull();
        (await invitations.ConsumeCodeAsync(user.Id, "246810", Purposes.LoginCode, Ct, "other-challenge")).ShouldBeNull();
        (await invitations.ConsumeCodeAsync(user.Id, "246810", Purposes.LoginCode, Ct, "first-challenge")).ShouldBe(user.Id);
        (await invitations.ConsumeCodeAsync(user.Id, "246810", Purposes.LoginCode, Ct, "first-challenge")).ShouldBeNull();
    }

    [Fact]
    public async Task Отказ_повторного_письма_не_уничтожает_прежний_код()
    {
        var user = await ConfiguredUserAsync();
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;
        var refusal = await Should.ThrowAsync<AppException>(() => SignInAsync(user.Email));
        var challenge = (string)refusal.Details!["challenge"]!;
        var code = database.Почта.Последнее(user.Email)!.Values["code"];
        database.Почта.Исход = MailQueueResult.RateLimited;
        await using var scope = database.Scope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        (await Should.ThrowAsync<AppException>(() => signIn.SendEmailCodeAsync(challenge, Ct))).Code.ShouldBe(ErrorCodes.RateLimited);
        await signIn.CompleteTwoFactorAsync(challenge, code, new RequestOrigin(null, "test"), cancellationToken: Ct);
    }

    private async Task<User> UserAsync()
    {
        var (user, _) = await database.InScopeAsync(service => service.CreateAsync(
            new UserDraft($"auth-{Guid.NewGuid():N}@example.com", Password: Password, MustChangePassword: false), Ct));
        return user;
    }

    /// <summary>
    /// Человек, который второй фактор уже подтверждал (например, аутентификатором, а потом
    /// выбрал письмо). Только у такого вход спрашивает код письмом.
    /// </summary>
    private async Task<User> ConfiguredUserAsync()
    {
        var user = await UserAsync();
        await database.ReadAsync(db => db.Set<User>().Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.TwoFactorConfiguredAt, DateTimeOffset.UtcNow), Ct));
        return user;
    }

    /// <summary>
    /// Без подтверждённого фактора вход по паролю даёт сессию настройки — даже при работающей почте.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Правило этой установки: каждая учётная запись — администратор, и, как у
    /// суперпользователя EAMS, приложение-аутентификатор обязательно. Код письмом до первого
    /// подтверждения фактора не выдаётся: иначе вход держался бы на одном почтовом ящике, а
    /// сломанный SMTP запирал бы снаружи всех администраторов разом.
    /// </remarks>
    [Fact]
    public async Task Без_подтверждённого_фактора_вход_даёт_настройку_даже_при_работающей_почте()
    {
        var user = await UserAsync();
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;
        database.Почта.Письма.Clear();

        var outcome = await SignInAsync(user.Email);

        (await ResolveAsync(outcome.Pair.Access)).MustEnrollTwoFactor.ShouldBeTrue();
        database.Почта.Письма.ShouldNotContain(letter => letter.To == user.Email, "код письмом до первого подтверждения не выдаётся");
    }

    [Fact]
    public async Task Администратор_сбрасывает_почтовую_2ФА_с_отзывом_сессий()
    {
        var user = await ConfiguredUserAsync();
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;
        var challengeError = await Should.ThrowAsync<AppException>(() => SignInAsync(user.Email));
        var challenge = (string)challengeError.Details!["challenge"]!;
        var code = database.Почта.Последнее(user.Email)!.Values["code"];
        await using var scope = database.Scope();
        var verified = await scope.ServiceProvider.GetRequiredService<SignInService>().CompleteTwoFactorAsync(
            challenge, code, new RequestOrigin(null, "test"), cancellationToken: Ct);
        (await scope.ServiceProvider.GetRequiredService<TwoFactorService>().ResetForAsync(user.Id, Ct)).ShouldBeTrue();
        await Should.ThrowAsync<AppException>(() => ResolveAsync(verified.Pair.Access));
        database.Почта.Настроена = false;
        database.Почта.Исход = MailQueueResult.NotConfigured;
        var bootstrap = await SignInAsync(user.Email);
        (await ResolveAsync(bootstrap.Pair.Access)).MustEnrollTwoFactor.ShouldBeTrue();
    }

    private async Task<SignInOutcome> SignInAsync(string email)
    {
        await using var scope = database.Scope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>().SignInAsync(
            email, Password, new RequestOrigin(null, "auth-foundation-test"), cancellationToken: Ct);
    }

    private async Task<ResolvedIdentity> ResolveAsync(string access)
    {
        await using var scope = database.Scope();
        return (await scope.ServiceProvider.GetRequiredService<IIdentityResolver>().ResolveAsync(new Request(access), Ct))!;
    }

    private sealed class Request(string access) : IIdentityRequest
    {
        public string? Header(string name) => name == "Authorization" ? $"Bearer {access}" : null;
        public string? Cookie(string name) => null;
    }

    private const string Password = "Очень-Хороший-Пароль-42!";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
