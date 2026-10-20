using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EmailSslSender.Modules.Identity;

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

    private async Task<HttpResponseMessage> MeAsync(string access)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return await host.CreateClient().SendAsync(request, Ct);
    }
}
