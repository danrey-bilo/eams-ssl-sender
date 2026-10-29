using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>Настройки установки: кто видит, кто правит и что попадает в журнал.</summary>
/// <remarks>⚠ <c>system_settings</c> одна на установку: каждый тест убирает за собой фикстурой. Отказ — всегда целиком.</remarks>
[Collection(SenderHost.Collection)]
public sealed class SettingsApi(SenderHost host)
{
    private const string Адрес = "/api/settings/system";

    /// <summary>Обычный ключ (не <c>IsSecurity</c>): правится правом <c>settings.manage</c>.</summary>
    private const string Обычный = "identity.link_retention_days";

    /// <summary>Умолчание обычного ключа — им проверяется `from` в журнале.</summary>
    private const int ОбычныйПоУмолчанию = 30;

    /// <summary>Ключ защиты: требует второго права.</summary>
    private const string Защитный = "auth.lockout_threshold";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Справочник_отдаётся_ВЕСЬ_а_не_только_заданное()
    {
        // Иначе администратор увидел бы пустой список и решил, что настроек нет.
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

    /// <summary>Ни ключа защиты, ни соседнего обычного: адрес SMTP или KMP с угнанной вкладки увёл бы секреты.</summary>
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

    /// <summary>Модуль настроек закрывает кодом любую правку, не только ключи защиты.</summary>
    [Fact]
    public async Task Обычный_ключ_с_давним_кодом_просит_подтверждения()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage");
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddHours(-3);

        try
        {
            var ответ = await Править(new { values = new Dictionary<string, object?> { [Обычный] = 7 } });

            ответ.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Код(ответ)).ShouldBe("SECOND_FACTOR_REQUIRED");
            (await Строк()).ShouldBe(0);
        }
        finally
        {
            host.User.StepUpAt = DateTimeOffset.UtcNow;
        }
    }

    [Fact]
    public async Task Окно_свежести_задаётся_настройкой_и_действует_сразу()
    {
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage", "settings.security.manage");

        // Умолчание модуля — 5 минут; 10 задаёт набор миграций `sender`.
        var окно = (await Прочитать()).First(строка => Ключ(строка) == "auth.step_up_minutes");
        окно["is_security"]!.GetValue<bool>().ShouldBeTrue();
        окно["value"]!.GetValue<int>().ShouldBe(10);
        окно["source"]!.GetValue<string>().ShouldBe("system");
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
        // Поэтому `values` и `reset` — два поля: «сервер не задан» — заданный `null`.
        await using var _ = await SystemSettingsFixture.НачатьAsync(host);
        Войти("settings.read", "settings.manage", "settings.security.manage");

        var ответ = await Править(new
        {
            values = new Dictionary<string, object?> { ["mail.smtp_host"] = null },
        });

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);

        var строка = Разобрать(await ответ.Content.ReadAsStringAsync(Ct))
            .First(с => Ключ(с) == "mail.smtp_host");

        // ⚠ Строка пишется, хотя значение равно умолчанию: иначе смена умолчания молча сменила бы выбор.
        строка["source"]!.GetValue<string>().ShouldBe("system");
        строка["value"].ShouldBeNull();
        (await Строк()).ShouldBe(1);
    }

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

    /// <summary>Строки журнала по событию, старые сверху; без фильтра по актору — подменённая личность в него не попадает.</summary>
    private async Task<List<string>> Записи()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Database
            .SqlQueryRaw<string>(
                "SELECT changes::text AS \"Value\" FROM audit_log"
                + " WHERE action = 'settings.system.update' ORDER BY ts")
            .ToListAsync(Ct);
    }

    private async Task<int> Записей() => (await Записи()).Count;
}
