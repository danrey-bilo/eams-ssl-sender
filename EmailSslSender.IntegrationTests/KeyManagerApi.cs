using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Key Manager Plus через API: ключ API, проверка связи, подписки.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Подписки и ключ API требуют свежего второго фактора, и это проверяется отдельно</b>
/// (решение владельца 2026-10-20). Подписка рассылает закрытые ключи на записанные в ней адреса:
/// дописавший свой адрес получает ключ следующим опросом. Ключ API отдаёт их все разом.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class KeyManagerApi(SenderHost host) : IAsyncLifetime
{
    private const string Состояние = "/api/keymanager";
    private const string КлючApi = "/api/keymanager/api-key";
    private const string Проверка = "/api/keymanager/test";
    private const string Подписки = "/api/certificate-subscriptions";

    private const string Ключ = "A36ECFEF-1234-4C5D-8E9F-0A1B2C3D4E5F";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        host.User.SignIn();
        host.Settings.Values.Clear();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        host.User.SignIn();
        host.Settings.Values.Clear();

        var клиент = host.CreateClient();
        await клиент.PutAsJsonAsync(КлючApi, new { api_key = "" }, CancellationToken.None);

        var список = await клиент.GetFromJsonAsync<JsonArray>(Подписки, CancellationToken.None) ?? [];

        foreach (var подписка in список)
        {
            await клиент.DeleteAsync($"{Подписки}/{подписка!["id"]!.GetValue<Guid>()}", CancellationToken.None);
        }

        host.User.SignOut();
    }

    [Fact]
    public async Task Состояние_не_отдаёт_ключа_ни_в_каком_виде()
    {
        (await host.CreateClient().PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var ответ = await host.CreateClient().GetAsync(Состояние, Ct);
        var тело = await ответ.Content.ReadAsStringAsync(Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(тело)!["api_key_set"]!.GetValue<bool>().ShouldBeTrue();
        тело.ShouldNotContain(Ключ);

        (await host.CreateClient().PutAsJsonAsync(КлючApi, new { api_key = "" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await host.CreateClient().GetFromJsonAsync<JsonObject>(Состояние, Ct))!["api_key_set"]!
            .GetValue<bool>().ShouldBeFalse();
    }

    /// <summary>
    /// Без свежего кода не проходит ничего, что отдаёт ключи или решает, кому они уйдут.
    /// </summary>
    /// <remarks>
    /// Отказ — до разбора ресурса: подписки с таким номером нет, а ответ всё равно 403, а не 404.
    /// Чтение и проверка связи кода не спрашивают.
    /// </remarks>
    [Fact]
    public async Task Ключ_и_подписки_без_свежего_фактора_отвергаются()
    {
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-11);
        var клиент = host.CreateClient();
        var чужая = $"{Подписки}/{Guid.NewGuid()}";

        await ОтказФактора(await клиент.PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct));
        await ОтказФактора(await клиент.PostAsJsonAsync(Подписки, Черновик(), Ct));
        await ОтказФактора(await клиент.PatchAsJsonAsync(чужая, new { interval_days = 7 }, Ct));
        await ОтказФактора(await клиент.DeleteAsync(чужая, Ct));
        await ОтказФактора(await клиент.PostAsync($"{чужая}/send", null, Ct));

        // Чтение свежего фактора не требует, проверка связи — тоже.
        (await клиент.GetAsync(Подписки, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await клиент.GetAsync(Состояние, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await клиент.GetAsync("/api/keymanager/certificates", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Код(await клиент.PostAsync(Проверка, null, Ct))).ShouldBe("KEYMANAGER_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Свежий_код_пропускает_ключ_и_подписки()
    {
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-9);
        var клиент = host.CreateClient();

        (await клиент.PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await клиент.PostAsJsonAsync(Подписки, Черновик(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Подписка_заводится_меняется_и_удаляется()
    {
        var клиент = host.CreateClient();

        var создано = await клиент.PostAsJsonAsync(Подписки, Черновик(), Ct);
        создано.StatusCode.ShouldBe(HttpStatusCode.Created);

        var подписка = (await создано.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var id = подписка["id"]!.GetValue<Guid>();

        подписка["common_name"]!.GetValue<string>().ShouldBe("site.example.com");
        Строки(подписка["formats"]).ShouldBe(["PEM", "KEY"], "по умолчанию — сертификат и ключ");
        подписка["on_change"]!.GetValue<bool>().ShouldBeTrue();
        подписка["interval_days"].ShouldBeNull();
        подписка["locale"]!.GetValue<string>().ShouldBe("ru");
        подписка["last_sent_serial"].ShouldBeNull();

        var изменено = await клиент.PatchAsJsonAsync(
            $"{Подписки}/{id}", new { interval_days = 7, formats = new[] { "pkcs", "pem" }, locale = "uk" }, Ct);
        изменено.StatusCode.ShouldBe(HttpStatusCode.OK);

        var после = (await изменено.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        после["interval_days"]!.GetValue<int>().ShouldBe(7);
        Строки(после["formats"]).ShouldBe(["PKCS", "PEM"]);
        после["locale"]!.GetValue<string>().ShouldBe("uk");
        Строки(после["recipients"]).ShouldBe(["it@example.com"], "не присланное поле не меняется");

        var безРасписания = await клиент.PatchAsJsonAsync($"{Подписки}/{id}", new { interval_days = 0 }, Ct);
        (await безРасписания.Content.ReadFromJsonAsync<JsonObject>(Ct))!["interval_days"].ShouldBeNull();

        (await клиент.DeleteAsync($"{Подписки}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await клиент.GetAsync($"{Подписки}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public static TheoryData<string, string> Неверные => new()
    {
        { """{"recipients":["it@example.com"]}""", "common_name" },
        { """{"common_name":"a","recipients":[]}""", "recipients" },
        { """{"common_name":"a","recipients":["windowsdanigame.com"]}""", "recipients" },
        { """{"common_name":"a","recipients":["Имя <it@example.com>"]}""", "recipients" },
        { """{"common_name":"a","recipients":["it@example.com"],"formats":["PFX"]}""", "formats" },
        { """{"common_name":"a","recipients":["it@example.com"],"formats":[]}""", "formats" },
        { """{"common_name":"a","recipients":["it@example.com"],"interval_days":400}""", "interval_days" },
        { """{"common_name":"a","recipients":["it@example.com"],"locale":"de"}""", "locale" },
    };

    [Theory]
    [MemberData(nameof(Неверные))]
    public async Task Неверная_подписка_отвергается_с_именем_поля(string тело, string поле)
    {
        var ответ = await host.CreateClient().PostAsync(
            Подписки, new StringContent(тело, System.Text.Encoding.UTF8, "application/json"), Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var конверт = (await ответ.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        конверт["code"]!.GetValue<string>().ShouldBe("VALIDATION_ERROR");
        конверт["details"]!["field"]!.GetValue<string>().ShouldBe(поле);
    }

    [Fact]
    public async Task Проверка_без_адреса_и_ключа_отвечает_409_не_настроено()
    {
        var ответ = await host.CreateClient().PostAsync(Проверка, null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("KEYMANAGER_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Открытый_HTTP_отвергается_до_отправки_ключа()
    {
        host.Settings.Values["keymanager.base_url"] = "http://kmp.example.test:6565";
        await host.CreateClient().PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct);

        var ответ = await host.CreateClient().PostAsync(Проверка, null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("KEYMANAGER_UNAVAILABLE");
        (await Причина(ответ)).ShouldContain("https://");
    }

    [Fact]
    public async Task Недоступный_сервер_приезжает_409_с_причиной()
    {
        // Порт, на котором заведомо никто не слушает: нужен отказ соединения, а не сервер.
        host.Settings.Values["keymanager.base_url"] = "https://127.0.0.1:1";
        await host.CreateClient().PutAsJsonAsync(КлючApi, new { api_key = Ключ }, Ct);

        var ответ = await host.CreateClient().PostAsync(Проверка, null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Код(ответ)).ShouldBe("KEYMANAGER_UNAVAILABLE");
        (await Причина(ответ)).ShouldContain("недоступен");
    }

    [Fact]
    public async Task Ручная_отправка_несуществующей_подписки_404()
    {
        (await host.CreateClient().PostAsync($"{Подписки}/{Guid.NewGuid()}/send", null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Опрос_будится_и_снимок_читается()
    {
        (await host.CreateClient().PostAsync("/api/keymanager/poll", null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await host.CreateClient().GetAsync("/api/keymanager/certificates", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // --- обвязка ---------------------------------------------------------------------------

    private static object Черновик() => new
    {
        common_name = "site.example.com",
        recipients = new[] { "it@example.com" },
    };

    private static List<string> Строки(JsonNode? массив) =>
        [.. массив!.AsArray().Select(з => з!.GetValue<string>())];

    private static async Task ОтказФактора(HttpResponseMessage ответ)
    {
        ответ.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var конверт = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        конверт["code"]!.GetValue<string>().ShouldBe("SECOND_FACTOR_REQUIRED");
        конверт["details"]!["window_minutes"]!.GetValue<int>().ShouldBe(10);
    }

    private static async Task<string> Код(HttpResponseMessage ответ) =>
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>();

    private static async Task<string> Причина(HttpResponseMessage ответ) =>
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["details"]!["reason"]!.GetValue<string>();
}
