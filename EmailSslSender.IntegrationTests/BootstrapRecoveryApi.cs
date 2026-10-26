using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Identity;

namespace EmailSslSender.IntegrationTests;

/// <summary>Точка входа консольного восстановления с настоящим аудитом и проверкой отзыва через HTTP.</summary>
[Collection(SenderHost.Collection)]
public sealed class BootstrapRecoveryApi(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Команды_меняют_доступ_в_настоящем_приложении()
    {
        host.User.SignOut();
        const string oldPassword = "Очень-Хороший-Пароль-42!";
        const string newPassword = "Другой-Надёжный-Пароль-73!";
        var email = $"command-api-{Guid.NewGuid():N}@example.com";
        var id = await IdentityModule.BootstrapSuperuserAsync(host.Services, email, "CLI admin", oldPassword, Ct);
        var first = await host.LoginFixtureAsync(host.CreateClient(), email, oldPassword, false, Ct, completeFactor: false);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var oldAccess = (await first.Content.ReadFromJsonAsync<JsonObject>(Ct))!["access_token"]!.GetValue<string>();
        (await IdentityModule.SetSuperuserPasswordAsync(host.Services, email, newPassword, Ct)).ShouldBe(id);
        (await MeAsync(oldAccess)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var second = await host.LoginFixtureAsync(host.CreateClient(), email, newPassword, false, Ct, completeFactor: false);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newAccess = (await second.Content.ReadFromJsonAsync<JsonObject>(Ct))!["access_token"]!.GetValue<string>();
        var me = await (await MeAsync(newAccess)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        me!["must_enroll_two_factor"]!.GetValue<bool>().ShouldBeTrue();
        (await IdentityModule.ResetSuperuserTwoFactorAsync(host.Services, email, Ct)).ShouldBe(id);
        (await MeAsync(newAccess)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// <c>reset-access</c> в настоящем приложении: прежняя сессия мертва, прежний пароль не входит,
    /// новый — входит в сессию подключения аутентификатора.
    /// </summary>
    [Fact]
    public async Task Восстановление_доступа_закрывает_вход_и_возвращает_к_подключению_фактора()
    {
        host.User.SignOut();
        const string oldPassword = "Очень-Хороший-Пароль-42!";
        const string newPassword = "Другой-Надёжный-Пароль-73!";
        var email = $"reset-access-api-{Guid.NewGuid():N}@example.com";
        var id = await IdentityModule.BootstrapSuperuserAsync(host.Services, email, "CLI admin", oldPassword, Ct);

        // Первый вход подключает аутентификатор — дальше у учётной записи настоящий второй фактор.
        var first = await host.LoginFixtureAsync(host.CreateClient(), email, oldPassword, false, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var oldAccess = (await first.Content.ReadFromJsonAsync<JsonObject>(Ct))!["access_token"]!.GetValue<string>();
        (await MeAsync(oldAccess)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var итог = await IdentityModule.ResetAccessAsync(host.Services, email, newPassword, Ct);

        итог.UserId.ShouldBe(id);
        итог.IsActive.ShouldBeTrue();
        итог.HadTwoFactor.ShouldBeTrue();
        итог.SessionsRevoked.ShouldBe(1);
        (await MeAsync(oldAccess)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await host.LoginFixtureAsync(host.CreateClient(), email, oldPassword, false, Ct, completeFactor: false))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "прежний пароль пережил восстановление");

        var next = await host.LoginFixtureAsync(host.CreateClient(), email, newPassword, false, Ct, completeFactor: false);
        next.StatusCode.ShouldBe(HttpStatusCode.OK, "новый пароль спросил прежний аутентификатор");
        var newAccess = (await next.Content.ReadFromJsonAsync<JsonObject>(Ct))!["access_token"]!.GetValue<string>();
        var me = (await (await MeAsync(newAccess)).Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        me["must_enroll_two_factor"]!.GetValue<bool>().ShouldBeTrue();
        me["step_up_until"].ShouldBeNull("у сессии настройки нет подтверждённого фактора");
    }

    private async Task<HttpResponseMessage> MeAsync(string access)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return await host.CreateClient().SendAsync(request, Ct);
    }
}
