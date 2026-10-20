using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Почта: состояние, пароль SMTP, проверочное письмо.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Пароль и проверка закрыты <c>settings.security.manage</c>, а не <c>settings.manage</c>,
/// и это проверяется отдельно.</b> Тот, кто меняет адрес SMTP-сервера, направляет письма сброса
/// на свой релей и собирает одноразовые ссылки всех, кто попросит восстановление.
///
/// ⚠ <b>Проверочное письмо — зонд наружу из-под сервера:</b> оно устанавливает исходящее
/// соединение на произвольный хост и порт из настроек и показывает ответ. Тем же правом и по
/// той же причине.
/// </remarks>
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

        // ⚠ Ни в одном поле и ни в каком виде: ответ проверяется целиком, а не по полям.
        тело.ShouldNotContain("очень-секретный-пароль");

        await Снять();
    }

    [Fact]
    public async Task Пароль_задаётся_и_снимается_пустой_строкой()
    {
        // ⚠ Пустая строка СНИМАЕТ пароль, и это законное состояние: реле в той же сети
        // принимает почту без авторизации. Отказ на пустой строке запретил бы такую установку.
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

        // ⚠ 409, а не 503: это не «сервис недоступен», а «настройка не заполнена», и чинится
        // она человеком за минуту.
        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("MAIL_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Отказ_чужого_сервера_приезжает_409_с_его_текстом()
    {
        // ⚠ ⚠ 409, а не 502, и причина в жизни: Cloudflare подменяет тело любого 5xx от origin
        // своей страницей, и конверт с `details.reason` до браузера не доезжает. Администратор
        // увидел бы «письмо не отправлено» вместо `5.7.1 Authentication Required`.
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.security.manage", "settings.manage");

        // Настраиваем почту на заведомо недоступный порт: ответ будет отказом соединения.
        await Настроить();

        var ответ = await host.CreateClient()
            .PostAsJsonAsync(Проверка, new { to = "кто@example.com" }, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("MAIL_SEND_FAILED");

        // Текст чужого сервера уезжает администратору целиком — ради этого ручка и существует.
        var тело = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        тело["details"]!["reason"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Проверочное_письмо_без_адреса_отвергается()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.security.manage");

        (await host.CreateClient().PostAsJsonAsync(Проверка, new { to = "" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // --- обвязка ---------------------------------------------------------------------------

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

                    // Порт, на котором заведомо никто не слушает: нам нужен отказ соединения,
                    // а не работающий почтовик.
                    ["mail.smtp_host"] = "127.0.0.1",
                    ["mail.smtp_port"] = 1,
                    ["mail.smtp_security"] = "none",

                    // ⚠ Вскрытие 44: без адреса отправителя почта НЕ считается настроенной, и
                    // отказ пришёл бы «не настроена» вместо ответа чужого сервера. Прежде адрес
                    // угадывался из учётной записи SMTP — и угадывался неверно.
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

    /// <summary>Строки журнала по событию пароля — прирост считается ими.</summary>
    private async Task<List<string>> Записи()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Database
            .SqlQueryRaw<string>(
                "SELECT changes::text AS \"Value\" FROM audit_log"
                + " WHERE action = 'mail.password_set' ORDER BY ts")
            .ToListAsync(Ct);
    }
}
