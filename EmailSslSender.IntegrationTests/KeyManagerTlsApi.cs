using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using EmailSslSender.Shared.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Настоящий соединитель против HTTPS-заглушки Key Manager Plus с самоподписанным сертификатом.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Проверяется то, чего не видят тесты клиента на заглушке обработчика:</b> что проверка
/// TLS действительно включена (самоподписанный без отпечатка — отказ), что закреплённый отпечаток
/// её проходит, что ключ уходит заголовком — и что ручная отправка доводит файлы до очереди писем
/// запечатанными вложениями.
///
/// ⚠ ⚠ <b>И что ключ не уходит туда, куда его не вводили:</b> подменённый адрес с отпечатком
/// подменного сервера — отказ без единого запроса к нему. Поэтому отпечаток в этих тестах
/// задаётся ДО ключа — ровно как в панели.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class KeyManagerTlsApi(SenderHost host) : IAsyncLifetime
{
    private const string Ключ = "75408B31-C7D6-4F5A-9A0E-1B2C3D4E5F60";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ЗаглушкаKmp _kmp = new(Ключ);

    public async ValueTask InitializeAsync()
    {
        await _kmp.ЗапуститьAsync();

        host.User.SignIn();
        host.Settings.Values.Clear();
        host.Settings.Values["keymanager.base_url"] = _kmp.Адрес;

        (await host.CreateClient().PutAsJsonAsync("/api/keymanager/api-key", new { api_key = Ключ }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public async ValueTask DisposeAsync()
    {
        host.User.SignIn();
        host.Settings.Values.Clear();

        var клиент = host.CreateClient();
        await клиент.PutAsJsonAsync("/api/keymanager/api-key", new { api_key = "" }, CancellationToken.None);

        foreach (var подписка in await клиент.GetFromJsonAsync<JsonArray>(
                     "/api/certificate-subscriptions", CancellationToken.None) ?? [])
        {
            await клиент.DeleteAsync(
                $"/api/certificate-subscriptions/{подписка!["id"]!.GetValue<Guid>()}", CancellationToken.None);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SenderDbContext>().Database.ExecuteSqlRawAsync(
                "DELETE FROM mail_outbox WHERE kind = 'certificate_delivery'", CancellationToken.None);
        }

        host.User.SignOut();
        await _kmp.DisposeAsync();
    }

    [Fact]
    public async Task Самоподписанный_сервер_без_отпечатка_отвергается()
    {
        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var тело = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        тело["code"]!.GetValue<string>().ShouldBe("KEYMANAGER_UNAVAILABLE");
        тело["details"]!["reason"]!.GetValue<string>().ShouldContain("keymanager.server_sha256");

        _kmp.Запросов.ShouldBe(0, "до сервера с непроверенным сертификатом ключ не доехал");
    }

    [Fact]
    public async Task Закреплённый_отпечаток_пропускает_и_ключ_едет_заголовком()
    {
        await ЗакрепитьAsync();

        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["certificates"]!.GetValue<int>().ShouldBe(1);
        _kmp.Запросов.ShouldBe(1);
    }

    [Fact]
    public async Task Отпечаток_закреплённый_после_ключа_требует_ввести_ключ_заново()
    {
        // Ключ вводили без отпечатка — закрепление после него меняет адресата ключа.
        host.Settings.Values["keymanager.server_sha256"] = _kmp.Отпечаток;

        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var тело = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        тело["code"]!.GetValue<string>().ShouldBe("KEYMANAGER_UNAVAILABLE");
        тело["details"]!["reason"]!.GetValue<string>().ShouldContain("Введите ключ API заново");
        _kmp.Запросов.ShouldBe(0);

        await ЗакрепитьAsync();

        (await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ключ_не_уходит_на_подменённый_адрес_с_его_отпечатком()
    {
        // ⚠ Сценарий перехваченной сессии: адрес и отпечаток переставлены на свой сервер, и
        // «проверить связь» понесла бы ключ туда.
        await using var подменный = new ЗаглушкаKmp("ключ-подменного");
        await подменный.ЗапуститьAsync();

        host.Settings.Values["keymanager.base_url"] = подменный.Адрес;
        host.Settings.Values["keymanager.server_sha256"] = подменный.Отпечаток;

        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var причина = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["details"]!["reason"]!.GetValue<string>();
        причина.ShouldContain("заново");
        причина.ShouldNotContain(Ключ);

        подменный.Запросов.ShouldBe(0, "до подменного сервера не дошло ни одного запроса — и ключа");
    }

    [Fact]
    public async Task Правка_и_удаление_подписки_отменяют_её_письма_в_очереди()
    {
        await ЗакрепитьAsync();
        ПочтаНаНедоступныйСервер();

        var клиент = host.CreateClient();
        var id = await ПодпискаAsync(клиент);

        (await клиент.PostAsync($"/api/certificate-subscriptions/{id}/send", null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ЖдутAsync("a@example.com")).ShouldBe(1);
        (await ЖдутAsync("b@example.com")).ShouldBe(1);

        // Убрали b — его письмо с закрытым ключом не уйдёт, а у a уйдёт.
        (await клиент.PatchAsJsonAsync(
                $"/api/certificate-subscriptions/{id}", new { recipients = new[] { "a@example.com" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ОтменёнAsync("b@example.com")).ShouldBe(1);
        (await ЖдутAsync("a@example.com")).ShouldBe(1);

        (await клиент.DeleteAsync($"/api/certificate-subscriptions/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ОтменёнAsync("a@example.com")).ShouldBe(1);
    }

    [Fact]
    public async Task Ручная_отправка_кладёт_письма_с_запечатанными_вложениями()
    {
        await ЗакрепитьAsync();
        host.Settings.Values["mail.enabled"] = true;
        host.Settings.Values["mail.smtp_host"] = "127.0.0.1";
        host.Settings.Values["mail.smtp_port"] = 1;
        host.Settings.Values["mail.smtp_security"] = "none";
        host.Settings.Values["mail.from_address"] = "vault@example.com";

        var клиент = host.CreateClient();

        var создано = await клиент.PostAsJsonAsync(
            "/api/certificate-subscriptions",
            new
            {
                common_name = "site.example.com",
                recipients = new[] { "a@example.com", "b@example.com" },
                formats = new[] { "PEM", "KEY" },
            },
            Ct);
        создано.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await создано.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<Guid>();

        var ответ = await клиент.PostAsync($"/api/certificate-subscriptions/{id}/send", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK, await ответ.Content.ReadAsStringAsync(Ct));
        var подписка = (await ответ.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        подписка["last_sent_serial"]!.GetValue<string>().ShouldBe("0a1b2c");
        подписка["last_error"].ShouldBeNull();

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        var письма = await db.Database
            .SqlQueryRaw<byte[]>(
                "SELECT attachments AS \"Value\" FROM mail_outbox WHERE kind = 'certificate_delivery' ORDER BY to_address")
            .ToListAsync(Ct);

        письма.Count.ShouldBe(2, "по письму на адрес");

        foreach (var вложения in письма)
        {
            вложения.ShouldNotBeNull().ShouldNotBeEmpty();
            вложения.AsSpan().IndexOf(ЗаглушкаKmp.ЗакрытыйКлюч).ShouldBe(-1, "ключ в очереди не лежит открытым");
        }
    }

    /// <summary>Закрепить отпечаток заглушки и ввести ключ после него — как это делает панель.</summary>
    private async Task ЗакрепитьAsync()
    {
        host.Settings.Values["keymanager.server_sha256"] = _kmp.Отпечаток;

        (await host.CreateClient().PutAsJsonAsync("/api/keymanager/api-key", new { api_key = Ключ }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>Почта настроена, но сервер недоступен: письма копятся в очереди.</summary>
    private void ПочтаНаНедоступныйСервер()
    {
        host.Settings.Values["mail.enabled"] = true;
        host.Settings.Values["mail.smtp_host"] = "127.0.0.1";
        host.Settings.Values["mail.smtp_port"] = 1;
        host.Settings.Values["mail.smtp_security"] = "none";
        host.Settings.Values["mail.from_address"] = "vault@example.com";
    }

    private async Task<Guid> ПодпискаAsync(HttpClient клиент)
    {
        var создано = await клиент.PostAsJsonAsync(
            "/api/certificate-subscriptions",
            new
            {
                common_name = "site.example.com",
                recipients = new[] { "a@example.com", "b@example.com" },
                formats = new[] { "PEM", "KEY" },
            },
            Ct);
        создано.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await создано.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<Guid>();
    }

    /// <summary>Писем с вложениями, ещё ждущих отправки, на адрес.</summary>
    /// <remarks>
    /// ⚠ Насос хоста в это время пробует недоступный сервер: попытка и причина у письма могут
    /// меняться, а вложения стираются только на последней попытке — до неё далеко.
    /// </remarks>
    private Task<int> ЖдутAsync(string адрес) => СчётAsync(
        "attachments IS NOT NULL AND next_attempt_at < '9999-01-01'", адрес);

    /// <summary>Отменённых писем на адрес: вложения стёрты, срок — «никогда».</summary>
    private Task<int> ОтменёнAsync(string адрес) => СчётAsync(
        "attachments IS NULL AND next_attempt_at >= '9999-01-01'", адрес);

    private async Task<int> СчётAsync(string условие, string адрес)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        var запрос = "SELECT count(*)::int AS \"Value\" FROM mail_outbox WHERE kind = 'certificate_delivery' "
            + "AND sent_at IS NULL AND to_address = {0} AND " + условие;

        var строки = await db.Database.SqlQueryRaw<int>(запрос, адрес).ToListAsync(Ct);

        return строки[0];
    }

    /// <summary>Key Manager Plus в процессе: HTTPS с самоподписанным сертификатом и тремя методами.</summary>
    private sealed class ЗаглушкаKmp(string ключ) : IAsyncDisposable
    {
        public static readonly byte[] ЗакрытыйКлюч = "-----BEGIN PRIVATE KEY-----\nзаглушка\n"u8.ToArray();

        private WebApplication? _приложение;
        private X509Certificate2? _сертификат;
        private int _запросов;

        public string Адрес { get; private set; } = "";

        public string Отпечаток { get; private set; } = "";

        public int Запросов => _запросов;

        public async Task ЗапуститьAsync()
        {
            using var пара = RSA.Create(2048);
            var запрос = new CertificateRequest("CN=kmp.example.test", пара, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var временный = запрос.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

            // ⚠ Через PKCS#12: SChannel не принимает эфемерный ключ, а CreateSelfSigned даёт именно его.
            _сертификат = X509CertificateLoader.LoadPkcs12(временный.Export(X509ContentType.Pkcs12), null);
            Отпечаток = Convert.ToHexString(SHA256.HashData(_сертификат.RawData));

            var строитель = WebApplication.CreateSlimBuilder();
            строитель.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, порт => порт.UseHttps(_сертификат)));

            _приложение = строитель.Build();

            _приложение.MapGet("/api/pki/restapi/{метод}", async (string метод, HttpContext контекст) =>
            {
                Interlocked.Increment(ref _запросов);

                if (контекст.Request.Headers["AUTHTOKEN"] != ключ || контекст.Request.QueryString.Value!.Contains(ключ))
                {
                    return Results.Json(new { result = new { status = "Failed", message = "Invalid API key" } });
                }

                // Как живой 7.1.1: параметр, не объявленный для метода, — 400 от фильтра безопасности.
                if (метод == "getAllSSLCertsExpiryDate" && контекст.Request.Query.Count > 0)
                {
                    return Results.Text("EXTRA_PARAM_FOUND", "text/html", statusCode: 400);
                }

                JsonNode Вход() => JsonNode.Parse(контекст.Request.Query["INPUT_DATA"].ToString())!["operation"]!["Details"]!;

                return метод switch
                {
                    "getAllSSLCertsExpiryDate" => Results.Json(new
                    {
                        name = "GetAllSSLCertificatesExpiryDate",
                        result = new { status = "Success", message = "Certificates expiry date fetched successfully" },
                        details = new[]
                        {
                            new Dictionary<string, string>
                            {
                                ["Common Name"] = "site.example.com",
                                ["serialNumber"] = "0a1b2c",
                                ["ExpiryDate"] = "Oct 2, 2027",
                            },
                        },
                    }),
                    "getCertificate" => Results.File(
                        Вход()["fileType"]!.GetValue<string>() == "KEY"
                            ? ЗакрытыйКлюч
                            : "-----BEGIN CERTIFICATE-----\nзаглушка\n"u8.ToArray(),
                        "application/octet-stream"),
                    _ => Results.NotFound(),
                };
            });

            await _приложение.StartAsync();

            Адрес = _приложение.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        }

        public async ValueTask DisposeAsync()
        {
            if (_приложение is not null)
            {
                await _приложение.DisposeAsync();
            }

            _сертификат?.Dispose();
        }
    }
}
