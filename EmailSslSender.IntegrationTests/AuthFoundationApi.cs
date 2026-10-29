using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>Второй фактор: обязательное подключение и свежий код для опасных действий.</summary>
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
        // Ролей нет: с подключённым фактором список людей открыт сразу.
        var afterEnrollment = await Request(HttpMethod.Get, "/api/users", access);
        afterEnrollment.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Нет отметки или она старше окна — отказ с окном в <c>details.window_minutes</c>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(11)]
    [InlineData(180)]
    public async Task Управление_доступом_требует_свежего_кода(int? ageMinutes)
    {
        Admin("user.create");
        host.User.StepUpAt = ageMinutes is { } age ? DateTimeOffset.UtcNow.AddMinutes(-age) : null;

        var response = await host.CreateClient().PostAsJsonAsync("/api/users", new { email = $"guard-{Guid.NewGuid():N}@example.com" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");
        body["details"]!["window_minutes"]!.GetValue<int>().ShouldBe(10);
    }

    [Fact]
    public async Task Код_внутри_окна_пропускает_управление_доступом()
    {
        Admin("user.create");
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        (await host.CreateClient().PostAsJsonAsync("/api/users", new { email = $"guard-{Guid.NewGuid():N}@example.com" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>Без подмены личности: отметка на токене, вторая сессия остаётся с давним кодом.</summary>
    [Fact]
    public async Task Давний_код_просит_подтверждения_и_код_из_приложения_открывает_то_же_действие()
    {
        Admin("user.create");
        var email = $"step-up-{Guid.NewGuid():N}@example.com";
        (await host.CreateActivatedUserAsync(host.CreateClient(), new { email, password = Password, must_change_password = false }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        host.User.SignOut();

        var first = (await (await host.LoginFixtureAsync(host.CreateClient(), email, Password, false, Ct))
            .Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var second = (await (await host.LoginFixtureAsync(host.CreateClient(), email, Password, false, Ct))
            .Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var access = first["access_token"]!.GetValue<string>();
        var otherAccess = second["access_token"]!.GetValue<string>();

        var me = (await (await Request(HttpMethod.Get, "/api/me", access)).Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var userId = me["id"]!.GetValue<Guid>();
        me["step_up_until"].ShouldNotBeNull("вход с кодом — уже свежее подтверждение");

        await Состарить(first["session_id"]!.GetValue<Guid>(), second["session_id"]!.GetValue<Guid>());

        (await (await Request(HttpMethod.Get, "/api/me", access)).Content.ReadFromJsonAsync<JsonObject>(Ct))!["step_up_until"]
            .ShouldBeNull("экран обещает свежий код, которого у сервера нет");

        var refused = await Request(HttpMethod.Post, $"/api/users/{userId}/unlock", access);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var envelope = (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        envelope["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");
        envelope["details"]!["window_minutes"]!.GetValue<int>().ShouldBe(10);

        // Сброс принятого шага: иначе код того же окна отвергнет защита от повтора.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE user_two_factor SET last_step = -1 WHERE user_id = {userId}", Ct);
        }

        var stepUp = await Request(
            HttpMethod.Post, "/api/me/step-up", access, new { code = SenderHost.TestTotpCode(host.FactorSecret(email)!) });
        stepUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var confirmed = (await stepUp.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var confirmedAt = confirmed["confirmed_at"]!.GetValue<DateTimeOffset>();
        confirmed["step_up_until"]!.GetValue<DateTimeOffset>().ShouldBe(confirmedAt.AddMinutes(10), TimeSpan.FromMilliseconds(1));

        // База хранит микросекунды — отсюда допуск.
        (await (await Request(HttpMethod.Get, "/api/me", access)).Content.ReadFromJsonAsync<JsonObject>(Ct))!["step_up_until"]!
            .GetValue<DateTimeOffset>().ShouldBe(confirmedAt.AddMinutes(10), TimeSpan.FromMilliseconds(1));

        (await Request(HttpMethod.Post, $"/api/users/{userId}/unlock", access)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var other = await Request(HttpMethod.Post, $"/api/users/{userId}/unlock", otherAccess);
        other.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "код из одной сессии открыл опасное в другой");
        (await other.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");
    }

    /// <summary>Отметка фактора на сессиях — полчаса назад.</summary>
    private async Task Состарить(params Guid[] sessions)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var давно = DateTimeOffset.UtcNow.AddMinutes(-30);

        foreach (var session in sessions)
        {
            (await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE auth_tokens SET step_up_at = {давно} WHERE id = {session}", Ct)).ShouldBe(1);
        }
    }

    private async Task<HttpResponseMessage> Request(HttpMethod method, string path, string access, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await host.CreateClient().SendAsync(request, Ct);
    }

    [Fact]
    public async Task Смена_обязательного_пароля_требует_кода_этой_сессии()
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
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = me["id"]!.GetValue<Guid>();
            var sessionId = tokens["session_id"]!.GetValue<Guid>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET must_change_password = true WHERE id = {userId}", Ct);
            var proofAt = DateTimeOffset.UtcNow.AddMinutes(-11);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE auth_tokens SET step_up_at = {proofAt} WHERE id = {sessionId}", Ct);
        }

        // ⚠ Кроме текущего пароля нужен свежий код: давняя сессия переподтверждается.
        var давний = await ChangePasswordAsync(access, Password + "-new");
        давний.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await давний.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sessionId = tokens["session_id"]!.GetValue<Guid>();
            var proofAt = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE auth_tokens SET step_up_at = {proofAt} WHERE id = {sessionId}", Ct);
        }

        (await ChangePasswordAsync(access, Password + "-new")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<HttpResponseMessage> ChangePasswordAsync(string access, string newPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/password")
        {
            Content = JsonContent.Create(new { current_password = Password, new_password = newPassword }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        return await host.CreateClient().SendAsync(request, Ct);
    }
}
