using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using EmailSslSender.Modules.KeyManager.Client;

namespace EmailSslSender.Modules.KeyManager.Tests;

/// <summary>
/// Клиент REST API Key Manager Plus на заглушке HTTP: что уходит и как разбирается ответ.
/// </summary>
/// <remarks>
/// ⚠ Ответы взяты из документации REST API Key Manager Plus — со всей её разнобойностью:
/// <c>result.status</c> и плоский <c>Status</c>, <c>Success</c> и <c>SUCCESS</c>. Сверка с живым
/// сервером 7.1.1 — отдельный шаг у владельца установки.
/// </remarks>
public sealed class ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ключ = "3E014D78-E603-413A-AC24-6392F0001283";

    [Fact]
    public async Task Ключ_уходит_заголовком_а_не_адресом()
    {
        var заглушка = new Заглушка(_ => Json("""{"name":"x","result":{"status":"Success"},"details":[]}"""));

        await Клиент(заглушка).ListAsync(Ct);

        var запрос = заглушка.Запросы.Single();
        запрос.Headers.GetValues(KeyManagerClient.Заголовок).ShouldBe([Ключ]);
        запрос.RequestUri!.ToString().ShouldNotContain(Ключ);
        запрос.Method.ShouldBe(HttpMethod.Get);
        запрос.RequestUri.AbsolutePath.ShouldBe("/api/pki/restapi/getAllSSLCertsExpiryDate");
    }

    [Fact]
    public async Task Список_запрашивается_без_INPUT_DATA()
    {
        // 7.1.1 (build 7110) на INPUT_DATA у этого метода отвечает 400 EXTRA_PARAM_FOUND.
        var заглушка = new Заглушка(_ => Json("""{"name":"x","result":{"status":"Success"},"details":[]}"""));

        await Клиент(заглушка).ListAsync(Ct);

        заглушка.Запросы.Single().RequestUri!.Query.ShouldBeEmpty();
    }

    [Fact]
    public async Task Код_фильтра_безопасности_попадает_в_текст_отказа()
    {
        var заглушка = new Заглушка(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("EXTRA_PARAM_FOUND\n", Encoding.Latin1, "text/html"),
        });

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => Клиент(заглушка).ListAsync(Ct));

        ошибка.Message.ShouldBe("Key Manager Plus ответил 400 на getAllSSLCertsExpiryDate: EXTRA_PARAM_FOUND.");
    }

    [Fact]
    public async Task Страница_ошибки_не_попадает_в_текст_отказа()
    {
        var заглушка = new Заглушка(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>upstream 10.0.0.5 down</body></html>", Encoding.UTF8, "text/html"),
        });

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => Клиент(заглушка).ListAsync(Ct));

        ошибка.Message.ShouldBe("Key Manager Plus ответил 502 на getAllSSLCertsExpiryDate.");
    }

    [Fact]
    public async Task Неверный_ключ_с_кодом_200_говорит_про_ключ()
    {
        // Так отвечает живой 7.1.1: AuthenticationFailure в конверте, HTTP 200.
        var заглушка = new Заглушка(_ => Json(
            """{"result":{"message":"Login Failed!","status":"Failure"},"name":"AuthenticationFailure"}"""));

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => Клиент(заглушка).ListAsync(Ct));

        ошибка.Message.ShouldBe("Key Manager Plus не принял ключ API (Login Failed!).");
    }

    [Fact]
    public async Task Вход_едет_в_INPUT_DATA_конвертом_operation_Details()
    {
        var заглушка = new Заглушка(_ => Файл("-----BEGIN CERTIFICATE-----\n"u8.ToArray()));

        await Клиент(заглушка).DownloadAsync("*.example.com", "182e967a976", "pem", Ct);

        var вход = JsonNode.Parse(Параметр(заглушка.Запросы.Single(), "INPUT_DATA"))!;
        var детали = вход["operation"]!["Details"]!;
        детали["common_name"]!.GetValue<string>().ShouldBe("*.example.com");
        детали["serial_number"]!.GetValue<string>().ShouldBe("182e967a976");
        детали["fileType"]!.GetValue<string>().ShouldBe("PEM", "тип приводится к виду документации");
    }

    [Fact]
    public async Task Список_разбирается_как_в_документации()
    {
        var заглушка = new Заглушка(_ => Json("""
            {
              "name": "GetAllSSLCertificatesExpiryDate",
              "result": { "status": "Success", "message": "Certificates expiry date fetched successfully" },
              "totalRows": 3,
              "details": [
                { "ExpiryDate": "Oct 2, 2025", "serialNumber": "c745dcae53f59103beda2477ccce73a",
                  "Common Name": "*.zoho.com", "formattedExpiryDate": "2025-10-02" },
                { "ExpiryDate": "Aug 27, 2027", "serialNumber": "0a1b", "Common Name": " site.example.com " },
                { "ExpiryDate": "Aug 27, 2027", "Common Name": "без-серийного.example.com" }
              ]
            }
            """));

        var список = await Клиент(заглушка).ListAsync(Ct);

        список.Count.ShouldBe(2, "строка без серийного номера пропускается, а не роняет опрос");
        список[0].ShouldBe(new KmpCertificate("*.zoho.com", "c745dcae53f59103beda2477ccce73a", new DateOnly(2025, 10, 2)));
        список[1].ShouldBe(new KmpCertificate("site.example.com", "0a1b", new DateOnly(2027, 8, 27)));
    }

    [Theory]
    [InlineData("Oct 2, 2025", "2025-10-02")]
    [InlineData("Oct 23, 2026", "2026-10-23")]
    [InlineData(" Oct 23, 2026 1:59:59 AM ", "2026-10-23")]
    [InlineData("2026-10-23", "2026-10-23")]
    [InlineData("скоро", null)]
    public void Срок_разбирается_терпимо(string текст, string? ожидается)
    {
        var дата = KeyManagerClient.Дата(new JsonObject { ["ExpiryDate"] = текст });

        дата.ShouldBe(ожидается is null ? null : DateOnly.Parse(ожидается, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Плоский_конверт_и_SUCCESS_заглавными_тоже_успех()
    {
        var заглушка = new Заглушка(_ => Json(
            """{"Status":"SUCCESS","details":[{"Common Name":"a","serialNumber":"01"}]}"""));

        (await Клиент(заглушка).ListAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Отказ_в_конверте_приходит_текстом_сервера()
    {
        var заглушка = new Заглушка(_ => Json(
            """{"name":"x","result":{"status":"Failed","message":"Invalid API key"}}"""));

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => Клиент(заглушка).ListAsync(Ct));

        ошибка.Message.ShouldContain("Invalid API key");
    }

    [Fact]
    public async Task Ответ_без_статуса_не_считается_успехом()
    {
        var заглушка = new Заглушка(_ => Json("""{"details":[]}"""));

        await Should.ThrowAsync<KeyManagerException>(() => Клиент(заглушка).ListAsync(Ct));
    }

    [Fact]
    public async Task HTTP_401_говорит_про_ключ()
    {
        var заглушка = new Заглушка(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => Клиент(заглушка).ListAsync(Ct));

        ошибка.Message.ShouldContain("ключ API");
    }

    [Fact]
    public async Task Перенаправление_отказ_а_не_переход()
    {
        var заглушка = new Заглушка(_ =>
        {
            var ответ = new HttpResponseMessage(HttpStatusCode.Redirect);
            ответ.Headers.Location = new Uri("https://evil.example.com/");
            return ответ;
        });

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => Клиент(заглушка).ListAsync(Ct));

        ошибка.Message.ShouldContain("перенаправляет");
        заглушка.Запросы.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Сбой_TLS_подсказывает_про_отпечаток()
    {
        var заглушка = new Заглушка(_ => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError, "The SSL connection could not be established"));

        var ошибка = await Should.ThrowAsync<KeyManagerException>(() => Клиент(заглушка).ListAsync(Ct));

        ошибка.Message.ShouldContain("keymanager.server_sha256");
    }

    [Fact]
    public async Task Файл_берётся_как_есть_а_имя_своё_с_расширением_типа()
    {
        // Живой 7.1.1 называет и PEM, и KEY одинаково — «mdmtst.example.com», без расширения.
        byte[] байты = [0x30, 0x82, 0x01, 0x0a];
        var заглушка = new Заглушка(_ => Файл(байты, "site.example.com"));

        var файл = await Клиент(заглушка).DownloadAsync("site.example.com", "01", CertificateFormats.Pkcs, Ct);

        файл.Content.ShouldBe(байты);
        файл.FileName.ShouldBe("site.example.com.pfx");
        файл.ContentType.ShouldBe("application/x-pkcs12");
    }

    [Fact]
    public async Task Архив_вместо_файла_называется_zip()
    {
        var заглушка = new Заглушка(_ => Файл([(byte)'P', (byte)'K', 3, 4, 0], "bundle"));

        var файл = await Клиент(заглушка).DownloadAsync("site.example.com", "01", CertificateFormats.Pem, Ct);

        файл.FileName.ShouldBe("site.example.com.pem.zip");
        файл.ContentType.ShouldBe("application/zip");
    }

    [Fact]
    public async Task Файл_без_имени_получает_имя_из_Common_Name()
    {
        var заглушка = new Заглушка(_ => Файл("-----BEGIN PRIVATE KEY-----\n"u8.ToArray()));

        var файл = await Клиент(заглушка).DownloadAsync("*.example.com", "01", CertificateFormats.Key, Ct);

        файл.FileName.ShouldBe("wildcard.example.com.key");
    }

    [Fact]
    public async Task Отказ_JSON_ом_вместо_файла_приходит_текстом_сервера()
    {
        var заглушка = new Заглушка(_ => Json(
            """{"name":"GetCertificate","result":{"status":"Failed","message":"Certificate not found"}}"""));

        var ошибка = await Should.ThrowAsync<KeyManagerException>(
            () => Клиент(заглушка).DownloadAsync("site.example.com", "01", CertificateFormats.Pem, Ct));

        ошибка.Message.ShouldContain("Certificate not found");
    }

    [Fact]
    public async Task JSON_вместо_файла_называет_ключи_но_не_значения()
    {
        // ⚠ В значениях может лежать сам закрытый ключ: в текст ошибки (а он едет в лог и в
        // last_error) попадают только имена полей.
        var заглушка = new Заглушка(_ => Json(
            """{"result":{"status":"Success"},"details":{"key":"-----BEGIN PRIVATE KEY----- секрет"}}"""));

        var ошибка = await Should.ThrowAsync<KeyManagerException>(
            () => Клиент(заглушка).DownloadAsync("site.example.com", "01", CertificateFormats.Key, Ct));

        ошибка.Message.ShouldContain("details");
        ошибка.Message.ShouldNotContain("секрет");
    }

    [Fact]
    public async Task Страница_ошибки_с_кодом_200_не_принимается_за_файл()
    {
        // HTML прокси или «Session expired» текстом ушли бы получателю под именем site.key.
        var заглушка = new Заглушка(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Session expired. Please log in.</body></html>", Encoding.UTF8, "text/html"),
        });

        var ошибка = await Should.ThrowAsync<KeyManagerException>(
            () => Клиент(заглушка).DownloadAsync("site.example.com", "01", CertificateFormats.Key, Ct));

        ошибка.Message.ShouldContain("не файл сертификата");
        ошибка.Message.ShouldNotContain("Session expired", Case.Insensitive);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'-', (byte)'-', (byte)'-', (byte)'-', (byte)'-', (byte)'B', (byte)'E', (byte)'G', (byte)'I', (byte)'N' })]
    [InlineData(new byte[] { 0x30, 0x82, 0x0a })]
    [InlineData(new byte[] { 0xFE, 0xED, 0xFE, 0xED, 0x00 })]
    [InlineData(new byte[] { (byte)'P', (byte)'K', 3, 4, 0 })]
    public void Известные_виды_файлов_принимаются(byte[] начало)
    {
        KeyManagerClient.ПохожНаСертификат(начало).ShouldBeTrue();
    }

    [Fact]
    public async Task Пароль_ключа_и_в_ответе_массивом()
    {
        var заглушка = new Заглушка(_ => Json(
            """{"result":{"status":"Success"},"details":[{"Passphrase":"ИзМассива"}]}"""));

        (await Клиент(заглушка).PassphraseAsync("mycert", "01", Ct)).ShouldBe("ИзМассива");
    }

    [Fact]
    public async Task Пустой_файл_отказ()
    {
        var заглушка = new Заглушка(_ => Файл([]));

        await Should.ThrowAsync<KeyManagerException>(
            () => Клиент(заглушка).DownloadAsync("site.example.com", "01", CertificateFormats.Pem, Ct));
    }

    [Fact]
    public async Task Пароль_ключа_берётся_из_details_Passphrase()
    {
        var заглушка = new Заглушка(_ => Json("""
            {"name":"GetCertificatePassphrase",
             "result":{"status":"Success","message":"Private key passphrase of certificate mycert is fetched"},
             "details":{"Passphrase":"SamplePassphrase"}}
            """));

        (await Клиент(заглушка).PassphraseAsync("mycert", "01", Ct)).ShouldBe("SamplePassphrase");
        заглушка.Запросы.Single().RequestUri!.AbsolutePath.ShouldEndWith("/getCertificatePassphrase");
    }

    [Fact]
    public void Неизвестный_тип_файла_не_уходит_на_сервер()
    {
        CertificateFormats.Normalize("pfx").ShouldBeNull();
        CertificateFormats.Normalize(" key ").ShouldBe("KEY");
    }

    // --- обвязка ---------------------------------------------------------------------------

    private static KeyManagerClient Клиент(Заглушка заглушка) =>
        new(new HttpClient(заглушка) { BaseAddress = new Uri("https://kmp.example.test:6565/") }, Ключ);

    private static HttpResponseMessage Json(string тело) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(тело, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Файл(byte[] байты, string? имя = null)
    {
        var содержимое = new ByteArrayContent(байты);
        содержимое.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        if (имя is not null)
        {
            содержимое.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = имя };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = содержимое };
    }

    private static string Параметр(HttpRequestMessage запрос, string имя) =>
        HttpUtility.ParseQueryString(запрос.RequestUri!.Query)[имя]
            ?? throw new InvalidOperationException($"В запросе нет параметра {имя}");

    /// <summary>Заглушка HTTP: запоминает запросы и отвечает тем, что дал тест.</summary>
    private sealed class Заглушка(Func<HttpRequestMessage, HttpResponseMessage> ответ) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Запросы { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Запросы.Add(request);

            return Task.FromResult(ответ(request));
        }
    }
}
