using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>Сквозные границы нового пути: приглашение, обязательный код, атомарная выдача и замена ролей.</summary>
[Collection(SenderHost.Collection)]
public sealed class AuthFoundationApi(SenderHost host) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Password = "Очень-Хороший-Пароль-42!";

    public void Dispose()
    {
        host.User.SignOut();
    }

    private void Admin(params string[] permissions)
    {
        host.User.SignIn();
    }

    [Fact]
    public async Task Пароль_без_кода_открывает_только_свою_настройку()
    {
        Admin("user.create", "user.read");
        var email = $"foundation-{Guid.NewGuid():N}@example.com";
        (await host.CreateActivatedUserAsync(host.CreateClient(), new { email, password = Password, must_change_password = false }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        host.User.SignOut();
        var login = await host.LoginFixtureAsync(host.CreateClient(), email, Password, false, Ct, completeFactor: false);
        var tokens = (await login.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var access = tokens["access_token"]!.GetValue<string>();
        var me = await Request(HttpMethod.Get, "/api/me", access);
        (await me.Content.ReadFromJsonAsync<JsonObject>(Ct))!["must_enroll_two_factor"]!.GetValue<bool>().ShouldBeTrue();
        var denied = await Request(HttpMethod.Get, "/api/users", access);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>().ShouldBe("TWO_FACTOR_ENROLLMENT_REQUIRED");
        await host.CompleteInitialFactorAsync(host.CreateClient(), access, Ct);
        var verified = await Request(HttpMethod.Get, "/api/me", access);
        (await verified.Content.ReadFromJsonAsync<JsonObject>(Ct))!["must_enroll_two_factor"]!.GetValue<bool>().ShouldBeFalse();
        // Прав в установке нет: подключивший фактор администратор видит людей сразу.
        var afterEnrollment = await Request(HttpMethod.Get, "/api/users", access);
        afterEnrollment.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Второй фактор — только при входе: давний код не мешает управлять доступом.
    /// </summary>
    /// <remarks>
    /// ⚠ Решение владельца 2026-10-20: повторного кода на действиях после входа нет. Прежде здесь
    /// проверялся отказ <c>SECOND_FACTOR_REQUIRED</c> — теперь проверяется, что его нет.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task Управление_доступом_не_требует_повторного_кода(int ageMinutes)
    {
        Admin("user.create");
        host.User.StepUpAt = ageMinutes == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(-ageMinutes);
        var response = await host.CreateClient().PostAsJsonAsync("/api/users", new { email = $"guard-{Guid.NewGuid():N}@example.com" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("PATCH", "/api/settings/system", 0)]
    [InlineData("PATCH", "/api/settings/system", 10)]
    [InlineData("PUT", "/api/mail/password", 0)]
    [InlineData("PUT", "/api/mail/password", 10)]
    [InlineData("POST", "/api/mail/test", 0)]
    [InlineData("POST", "/api/mail/test", 10)]
    public async Task Настройки_и_SMTP_не_требуют_повторного_кода(string method, string path, int ageMinutes)
    {
        Admin("settings.manage", "settings.security.manage");
        host.User.StepUpAt = ageMinutes == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(-ageMinutes);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { }),
        };
        var response = await host.CreateClient().SendAsync(request, Ct);

        // Пустое тело даёт свой ответ (сохранено, «не указано куда», «почта не настроена»), но
        // не отказ в доступе: второй фактор спрашивается только при входе.
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> Request(HttpMethod method, string path, string access)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return await host.CreateClient().SendAsync(request, Ct);
    }

    [Fact]
    public async Task Смена_обязательного_пароля_не_требует_повторного_кода()
    {
        Admin("user.create");
        var email = $"password-guard-{Guid.NewGuid():N}@example.com";
        (await host.CreateActivatedUserAsync(host.CreateClient(), new { email, password = Password, must_change_password = false }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        host.User.SignOut();
        var login = await host.LoginFixtureAsync(host.CreateClient(), email, Password, false, Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var access = tokens["access_token"]!.GetValue<string>();
        var me = (await (await Request(HttpMethod.Get, "/api/me", access)).Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
            var userId = me["id"]!.GetValue<Guid>();
            var sessionId = tokens["session_id"]!.GetValue<Guid>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET must_change_password = true WHERE id = {userId}", Ct);
            var proofAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE auth_tokens SET step_up_at = {proofAt} WHERE id = {sessionId}", Ct);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/password")
        {
            Content = JsonContent.Create(new { current_password = Password, new_password = Password + "-new" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var response = await host.CreateClient().SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "код подтверждён при входе десять минут назад, и этого достаточно");
    }
}
