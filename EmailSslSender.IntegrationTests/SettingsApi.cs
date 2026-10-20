using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Настройки установки: кто видит, кто правит и что попадает в журнал.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Каждый тест работает с НАСТОЯЩИМИ настройками и убирает за собой фикстурой.</b>
/// Подмена читателя — умолчание этого сьюта именно потому, что `system_settings` одна на всю
/// установку: строка, оставленная здесь, досталась бы всем следующим тестам коллекции. На
/// Python-версии пять таких строк уронили 151 тест в модулях, которые про настройки не знают.
///
/// ⚠ <b>Главная проверка файла — «отказ ЦЕЛИКОМ».</b> Половина новой политики паролей вместе с
/// половиной старой хуже, чем отклонённое изменение: администратор увидит «сохранено с ошибкой»
/// и не узнает, какая половина применилась.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class SettingsApi(SenderHost host)
{
    private const string Адрес = "/api/settings/system";

    /// <summary>
    /// Обычный ключ: правится правом `settings.manage`.
    /// </summary>
    /// <remarks>
    /// ⚠ Выбран не наугад. Первая редакция взяла `vaults.version_history_depth` — и получила
    /// 403 на всём: он объявлен `IsSecurity: true`, потому что версия записи содержит прежний
    /// секрет. Ключ для проверки «обычного» пути обязан быть проверенно обычным.
    /// </remarks>
    private const string Обычный = "identity.link_retention_days";

    /// <summary>Умолчание обычного ключа — им проверяется `from` в журнале.</summary>
    private const int ОбычныйПоУмолчанию = 30;

    /// <summary>Ключ защиты: требует второго права.</summary>
    private const string Защитный = "auth.lockout_threshold";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Справочник_отдаётся_ВЕСЬ_а_не_только_заданное()
    {
        // ⚠ Иначе «настройки по умолчанию» существуют только в коде: администратор увидел бы
        // пустой список и решил, что настроек нет.
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read");

        var строки = await Прочитать();

        строки.Count.ShouldBeGreaterThan(20);
        строки.ShouldContain(строка => Ключ(строка) == Защитный);
        строки.First(строка => Ключ(строка) == Защитный)["source"]!
            .GetValue<string>().ShouldBe("default");
    }

    [Fact]
    public async Task Неизвестный_ключ_отвергается_своим_кодом()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage");

        var ответ = await Править(new { values = new Dictionary<string, object?> { ["нет.такого"] = 1 } });

        ответ.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await Код(ответ)).ShouldBe("UNKNOWN_SETTING");
    }

    [Fact]
    public async Task Обычный_ключ_правится_правом_управления()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage");

        var ответ = await Править(new { values = new Dictionary<string, object?> { [Обычный] = 7 } });

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);

        var строки = Разобрать(await ответ.Content.ReadAsStringAsync(Ct));
        var строка = строки.First(с => Ключ(с) == Обычный);

        строка["value"]!.GetValue<int>().ShouldBe(7);
        строка["source"]!.GetValue<string>().ShouldBe("system");
    }

    [Fact]
    public async Task Ключ_защиты_со_вторым_правом_проходит()
    {
        // Зеркальный факт: без него предыдущий зеленел бы и на «запрещено всегда».
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage", "settings.security.manage");

        var ответ = await Править(new
        {
            values = new Dictionary<string, object?> { [Обычный] = 9, [Защитный] = 99 },
        });

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Строк()).ShouldBe(2);
    }

    /// <summary>
    /// Ключ защиты без свежего кода — отказ целиком: ни ключа защиты, ни соседнего обычного.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Решение владельца 2026-10-20: адрес SMTP или Key Manager Plus, сменённый с угнанной
    /// вкладки, увёл бы коды входа или ключ API. Половина правки, применённая до отказа, — та же
    /// беда, что и в «отказе целиком» выше.
    /// </remarks>
    [Fact]
    public async Task Ключ_защиты_без_свежего_фактора_отвергается_целиком()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage", "settings.security.manage");
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-11);

        try
        {
            var ответ = await Править(new
            {
                values = new Dictionary<string, object?> { [Обычный] = 9, [Защитный] = 99 },
            });

            ответ.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var конверт = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
            конверт["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");
            конверт["details"]!["window_minutes"]!.GetValue<int>().ShouldBe(10);
            (await Строк()).ShouldBe(0, "правка применилась наполовину, хотя отказали");

            // Сброс ключа защиты — такая же правка защиты.
            (await Править(new { reset = new[] { Защитный } })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            host.User.StepUpAt = DateTimeOffset.UtcNow;
        }
    }

    [Fact]
    public async Task Обычный_ключ_правится_и_с_давним_кодом()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage");
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddHours(-3);

        try
        {
            (await Править(new { values = new Dictionary<string, object?> { [Обычный] = 7 } }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await Править(new { reset = new[] { Обычный } })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            host.User.StepUpAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Окно свежести — настройка защиты: её правка сама требует свежего кода и действует сразу.
    /// </summary>
    [Fact]
    public async Task Окно_свежести_задаётся_настройкой_и_действует_сразу()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage", "settings.security.manage");

        var окно = (await Прочитать()).First(строка => Ключ(строка) == "auth.step_up_minutes");
        окно["is_security"]!.GetValue<bool>().ShouldBeTrue();
        окно["default"]!.GetValue<int>().ShouldBe(10);
        окно["minimum"]!.GetValue<double>().ShouldBe(1);
        окно["maximum"]!.GetValue<double>().ShouldBe(120);

        (await Править(new { values = new Dictionary<string, object?> { ["auth.step_up_minutes"] = 60 } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Полчаса назад — давно для десяти минут и свежо для часа.
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        try
        {
            (await Править(new { values = new Dictionary<string, object?> { [Защитный] = 50 } }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            (await Править(new { values = new Dictionary<string, object?> { ["auth.step_up_minutes"] = 121 } }))
                .StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, "окно больше двух часов — уже не «свежее»");
        }
        finally
        {
            host.User.StepUpAt = DateTimeOffset.UtcNow;
        }
    }

    [Fact]
    public async Task Негодное_значение_отвергает_тело_целиком()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage");

        var ответ = await Править(new
        {
            values = new Dictionary<string, object?> { [Обычный] = 9, ["auth.lockout_minutes"] = 0 },
        });

        ответ.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await Код(ответ)).ShouldBe("INVALID_SETTING_VALUE");
        (await Строк()).ShouldBe(0);
    }

    [Fact]
    public async Task Сброс_убирает_строку_и_возвращает_слой_ниже()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage");

        await Править(new { values = new Dictionary<string, object?> { [Обычный] = 7 } });

        var ответ = await Править(new { reset = new[] { Обычный } });

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);

        var строка = Разобрать(await ответ.Content.ReadAsStringAsync(Ct))
            .First(с => Ключ(с) == Обычный);

        строка["source"]!.GetValue<string>().ShouldBe("default");
        (await Строк()).ShouldBe(0);
    }

    [Fact]
    public async Task Заданный_null_у_обнуляемого_это_значение_а_не_сброс()
    {
        // ⚠ Ради этого `values` и `reset` — два поля, а не одно. «Сервер не задан» —
        // заданный `null`; означай он сброс, выставить его стало бы нечем.
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage", "settings.security.manage");

        var ответ = await Править(new
        {
            values = new Dictionary<string, object?> { ["mail.smtp_host"] = null },
        });

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);

        var строка = Разобрать(await ответ.Content.ReadAsStringAsync(Ct))
            .First(с => Ключ(с) == "mail.smtp_host");

        // ⚠ Строка пишется, ХОТЯ значение совпало с умолчанием: администратор выбрал
        // «не задано», а не «пусть будет как в коде». Не записав её, мы отдали бы
        // `source: default`, и следующая выкатка, сменившая умолчание, молча сменила бы
        // и его настройку.
        строка["source"]!.GetValue<string>().ShouldBe("system");
        строка["value"].ShouldBeNull();
        (await Строк()).ShouldBe(1);
    }

    // --- обвязка ---------------------------------------------------------------------------

    private Guid Войти(params string[] права)
    {
        var кто = host.User.SignIn();

        return кто;
    }

    private async Task<List<JsonNode>> Прочитать()
    {
        var ответ = await host.CreateClient().GetAsync(Адрес, Ct);
        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);

        return Разобрать(await ответ.Content.ReadAsStringAsync(Ct));
    }

    private Task<HttpResponseMessage> Править(object тело) =>
        host.CreateClient().PatchAsJsonAsync(Адрес, тело, Ct);

    private static List<JsonNode> Разобрать(string json) =>
        [.. JsonNode.Parse(json)!.AsArray().Select(узел => узел!)];

    private static string Ключ(JsonNode строка) => строка["key"]!.GetValue<string>();

    private async Task<string> Код(HttpResponseMessage ответ) =>
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>();

    private async Task<int> Строк() =>
        await host.СтрокНастроекAsync(Ct);

    /// <summary>
    /// Строки журнала по нашему событию, старые сверху.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Без фильтра по актору — намеренно.</b> Строку журнала собирает
    /// <c>IAuditContextAccessor</c> из контекста запроса, а не <c>ICurrentUser</c>: подменённая
    /// личность в <c>actor_id</c> не попадает, и фильтр по ней дал бы ноль строк, выглядящий как
    /// «журнал не пишется». Поэтому тест считает <b>прирост</b> — он верен и на чистой базе, и
    /// на рабочей.
    /// </remarks>
    private async Task<List<string>> Записи()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Database
            .SqlQueryRaw<string>(
                "SELECT changes::text AS \"Value\" FROM audit_log"
                + " WHERE action = 'settings.system.update' ORDER BY ts")
            .ToListAsync(Ct);
    }

    private async Task<int> Записей() => (await Записи()).Count;
}
