using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>Почта: состояние, пароль SMTP, проверочное письмо.</summary>
[Collection(SenderHost.Collection)]
public sealed class MailApi(SenderHost host)
{
    private const string Состояние = "/api/mail";
    private const string Пароль = "/api/mail/password";
    private const string Проверка = "/api/mail/test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Состояние_не_отдаёт_пароля_ни_в_каком_виде()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.security.manage");

        await Задать("очень-секретный-пароль");

        var ответ = await host.CreateClient().GetAsync(Состояние, Ct);
        var тело = await ответ.Content.ReadAsStringAsync(Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);

        var состояние = JsonNode.Parse(тело)!;

        состояние["password_set"]!.GetValue<bool>().ShouldBeTrue();

        // Ответ проверяется целиком, а не по полям.
        тело.ShouldNotContain("очень-секретный-пароль");

        await Снять();
    }

    [Fact]
    public async Task Пароль_задаётся_и_снимается_пустой_строкой()
    {
        // Без пароля — законно: реле в той же сети принимает почту без авторизации.
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.security.manage");

        await Задать("пароль");
        (await Состоянием())["password_set"]!.GetValue<bool>().ShouldBeTrue();

        await Снять();
        (await Состоянием())["password_set"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Проверочное_письмо_при_ненастроенной_почте_отвечает_409()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.security.manage");

        var ответ = await host.CreateClient()
            .PostAsJsonAsync(Проверка, new { to = "кто@example.com" }, Ct);

        // 409, а не 503: не заполнена настройка, сервис не лежит.
        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("MAIL_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Отказ_чужого_сервера_приезжает_409_с_его_текстом()
    {
        // ⚠ 409, а не 502: Cloudflare подменяет тело любого 5xx, и `details.reason` не доехал бы.
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.security.manage", "settings.manage");

        await Настроить();

        var ответ = await host.CreateClient()
            .PostAsJsonAsync(Проверка, new { to = "кто@example.com" }, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("MAIL_SEND_FAILED");

        // Текст чужого сервера уезжает администратору целиком.
        var тело = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        тело["details"]!["reason"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Пароль_не_уходит_на_сменившийся_сервер()
    {
        // ⚠ Пароль привязан к серверу: на сменившийся — отказ до соединения.
        Войти();

        try
        {
            ПочтаСВходом("127.0.0.1");
            await Задать("очень-секретный-пароль");

            host.Settings.Values["mail.smtp_host"] = "127.0.0.2";

            var ответ = await host.CreateClient().PostAsJsonAsync(Проверка, new { to = "кто@example.com" }, Ct);

            ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Код(ответ)).ShouldBe("MAIL_SEND_FAILED");

            var причина = await Причина(ответ);
            причина.ShouldContain("Введите пароль SMTP заново");
            причина.ShouldNotContain("очень-секретный-пароль");

            // Свой сервер — пароль открывается, отказ уже сетевой.
            host.Settings.Values["mail.smtp_host"] = "127.0.0.1";

            var свой = await host.CreateClient().PostAsJsonAsync(Проверка, new { to = "кто@example.com" }, Ct);

            свой.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Причина(свой)).ShouldNotContain("заново");
        }
        finally
        {
            await Снять();
            host.Settings.Values.Clear();
        }
    }

    [Fact]
    public async Task Чужой_отказ_приезжает_типом_а_не_текстом()
    {
        // ⚠ Отказ криптографии или Npgsql уезжает наружу только цепочкой типов, без текста.
        Войти();

        try
        {
            ПочтаСВходом("127.0.0.1");
            await Задать("пароль");

            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
                    "UPDATE system_secrets SET sealed = sealed || '\\x00'::bytea WHERE key = 'smtp_password'", Ct);
            }

            var ответ = await host.CreateClient().PostAsJsonAsync(Проверка, new { to = "кто@example.com" }, Ct);

            ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var причина = await Причина(ответ);

            Regex.IsMatch(причина, "^[A-Za-z]+Exception( → [A-Za-z]+Exception)*$")
                .ShouldBeTrue($"наружу уехал текст, а не цепочка типов: «{причина}»");
        }
        finally
        {
            await Снять();
            host.Settings.Values.Clear();
        }
    }

    /// <summary>⚠ Сменивший SMTP направил бы письма сброса и коды входа на свой релей.</summary>
    [Fact]
    public async Task Пароль_SMTP_и_проверочное_письмо_без_свежего_кода_отвергаются()
    {
        Войти();
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-11);

        try
        {
            var ответ = await host.CreateClient().PutAsJsonAsync(Пароль, new { password = "очень-секретный-пароль" }, Ct);

            ответ.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Код(ответ)).ShouldBe("SECOND_FACTOR_REQUIRED");
            (await ответ.Content.ReadAsStringAsync(Ct)).ShouldNotContain("очень-секретный-пароль");

            // Проверочное письмо несёт пароль SMTP — подменённый сервер узнал бы его.
            var проверка = await host.CreateClient().PostAsJsonAsync(Проверка, new { to = "" }, Ct);

            проверка.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Код(проверка)).ShouldBe("SECOND_FACTOR_REQUIRED");
        }
        finally
        {
            host.User.StepUpAt = DateTimeOffset.UtcNow;
        }

        (await Состоянием())["password_set"]!.GetValue<bool>().ShouldBeFalse("пароль лёг, хотя в коде отказали");
    }

    [Fact]
    public async Task Проверочное_письмо_без_адреса_отвергается()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.security.manage");

        (await host.CreateClient().PostAsJsonAsync(Проверка, new { to = "" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private void Войти(params string[] права)
    {
        host.User.SignIn();
    }

    private async Task Задать(string пароль) =>
        (await host.CreateClient().PutAsJsonAsync(Пароль, new { password = пароль }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private async Task Снять() =>
        (await host.CreateClient().PutAsJsonAsync(Пароль, new { password = "" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private async Task Настроить() =>
        (await host.CreateClient().PatchAsJsonAsync(
            "/api/settings/system",
            new
            {
                values = new Dictionary<string, object?>
                {
                    ["mail.enabled"] = true,

                    // Пустой порт: нужен отказ соединения.
                    ["mail.smtp_host"] = "127.0.0.1",
                    ["mail.smtp_port"] = 1,
                    ["mail.smtp_security"] = "none",

                    // Без адреса отправителя почта не считается настроенной.
                    ["mail.from_address"] = "vault@example.com",
                },
            },
            Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task<JsonNode> Состоянием()
    {
        var ответ = await host.CreateClient().GetAsync(Состояние, Ct);
        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);

        return JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
    }

    private async Task<string> Код(HttpResponseMessage ответ) =>
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>();

    private static async Task<string> Причина(HttpResponseMessage ответ) =>
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["details"]!["reason"]!.GetValue<string>();

    /// <summary>Почта со входом по паролю на пустой порт — подставными настройками.</summary>
    private void ПочтаСВходом(string сервер)
    {
        host.Settings.Values.Clear();
        host.Settings.Values["mail.enabled"] = true;
        host.Settings.Values["mail.smtp_host"] = сервер;
        host.Settings.Values["mail.smtp_port"] = 1;
        host.Settings.Values["mail.smtp_security"] = "starttls";
        host.Settings.Values["mail.smtp_username"] = "robot";
        host.Settings.Values["mail.from_address"] = "vault@example.com";
    }

    /// <summary>Строки журнала по событию пароля.</summary>
    private async Task<List<string>> Записи()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Database
            .SqlQueryRaw<string>(
                "SELECT changes::text AS \"Value\" FROM audit_log"
                + " WHERE action = 'mail.password_set' ORDER BY ts")
            .ToListAsync(Ct);
    }
}
