using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Почта: состояние, пароль SMTP, проверочное письмо.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Пароль SMTP требует свежего второго фактора, и это проверяется отдельно</b> (решение
/// владельца 2026-10-20). Тот, кто меняет SMTP, направляет письма сброса на свой релей и собирает
/// одноразовые ссылки всех, кто попросит восстановление.
///
/// ⚠ <b>Проверочное письмо — зонд наружу из-под сервера:</b> оно устанавливает исходящее
/// соединение на хост и порт из настроек и показывает ответ. Свежего кода оно не спрашивает:
/// хост и порт — настройки защиты, и сменить их без свежего кода нельзя.
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
    public async Task Пароль_не_уходит_на_сменившийся_сервер()
    {
        // ⚠ ⚠ Сценарий перехваченной сессии: пароль вводили для настоящего сервера, а сервер в
        // настройках переставили на свой. Отказ — до соединения, с просьбой ввести пароль заново.
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

            // Свой сервер — пароль открывается, и отказ уже сетевой (порт заведомо пуст).
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
        // ⚠ Внутри попытки открывается пароль из базы: отказ криптографии (а с ним Npgsql и что
        // угодно ещё) не должен уезжать наружу своим сообщением — только цепочкой типов.
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

    /// <summary>
    /// Пароль SMTP без свежего кода не меняется; проверочное письмо кода не спрашивает.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Решение владельца 2026-10-20: сменивший SMTP направляет письма сброса и коды входа на
    /// свой релей. Проверочное письмо уходит на уже настроенный сервер — его отказ здесь «не
    /// указано куда», а не 403.
    /// </remarks>
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

            // ⚠ Модуль почты закрывает кодом и проверочное письмо: оно уходит с паролем SMTP на сервер из
            // настроек, и подменённый сервер узнал бы пароль.
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

    private static async Task<string> Причина(HttpResponseMessage ответ) =>
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["details"]!["reason"]!.GetValue<string>();

    /// <summary>Почта со входом по паролю на заведомо пустой порт — на подставных настройках.</summary>
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

    /// <summary>Строки журнала по событию пароля — прирост считается ими.</summary>
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
