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
    /// Управление доступом требует кода, подтверждённого на этой сессии за последние десять минут.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Решение владельца 2026-10-20, заменившее «второй фактор — только при входе»: отказ
    /// называет окно (<c>details.window_minutes</c>), чтобы экран мог сказать, сколько живёт код.
    /// Отметки нет вовсе — тот же отказ, а не пропуск.
    /// </remarks>
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

    /// <summary>
    /// Давний код на настоящей сессии: отказ, код из приложения, и то же действие проходит.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Сквозь весь путь, без подмены личности: отметка — на ТОКЕНЕ, и доехать до фильтра она
    /// может только через распознаватель. Вторая сессия того же человека остаётся с давним кодом:
    /// код, введённый в одной вкладке, не открывает опасное в остальных.
    /// </remarks>
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

        // Код из приложения — тот же, что вводит человек; принятый шаг сброшен, чтобы код текущего
        // окна не упёрся в защиту от повтора после входа в те же тридцать секунд.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SenderDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE user_two_factor SET last_step = -1 WHERE user_id = {userId}", Ct);
        }

        var stepUp = await Request(
            HttpMethod.Post, "/api/me/step-up", access, new { code = SenderHost.TestTotpCode(host.FactorSecret(email)!) });
        stepUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var confirmed = (await stepUp.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var confirmedAt = confirmed["confirmed_at"]!.GetValue<DateTimeOffset>();
        confirmed["step_up_until"]!.GetValue<DateTimeOffset>().ShouldBe(confirmedAt.AddMinutes(10), TimeSpan.FromMilliseconds(1));

        // Через базу отметка доезжает с точностью до микросекунды — отсюда допуск.
        (await (await Request(HttpMethod.Get, "/api/me", access)).Content.ReadFromJsonAsync<JsonObject>(Ct))!["step_up_until"]!
            .GetValue<DateTimeOffset>().ShouldBe(confirmedAt.AddMinutes(10), TimeSpan.FromMilliseconds(1));

        (await Request(HttpMethod.Post, $"/api/users/{userId}/unlock", access)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var other = await Request(HttpMethod.Post, $"/api/users/{userId}/unlock", otherAccess);
        other.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "код из одной сессии открыл опасное в другой");
        (await other.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");
    }

    /// <summary>Отметка фактора на сессиях — полчаса назад: окно в десять минут давно прошло.</summary>
    private async Task Состарить(params Guid[] sessions)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
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

    /// <summary>
    /// Смена своего пароля свежего кода не спрашивает: её подтверждает текущий пароль.
    /// </summary>
    /// <remarks>
    /// Свежий второй фактор нужен действиям над ключами, подписками, почтой, людьми и настройками
    /// защиты (<c>[RequireRecentSecondFactor]</c>); своё — пароль и имя — в этот список не входит.
    /// </remarks>
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
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "смену своего пароля подтверждает текущий пароль, а не свежий код");
    }
}
