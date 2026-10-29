using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using EmailSslSender.Modules.KeyManager;
using Modules.Core.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.IntegrationTests;

/// <summary>Настоящий соединитель против HTTPS-заглушки Key Manager Plus с самоподписанным сертификатом.</summary>
/// <remarks>⚠ Доверие заглушке — только через <see cref="KeyManagerModuleOptions.TrustedRoots"/>: проверка TLS та же, что в бою.</remarks>
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
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
                "DELETE FROM mail_outbox WHERE starts_with(kind, 'certificate_delivery')", CancellationToken.None);
        }

        host.User.SignOut();
        Корни(host.Services).Clear();
        await _kmp.DisposeAsync();
    }

    [Fact]
    public async Task Самоподписанный_сервер_отвергается()
    {
        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var тело = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        тело["code"]!.GetValue<string>().ShouldBe("KEYMANAGER_UNAVAILABLE");
        тело["details"]!["reason"]!.GetValue<string>().ShouldContain("самоподписанный не принимается");

        _kmp.Запросов.ShouldBe(0, "до сервера с непроверенным сертификатом ключ не доехал");
    }

    [Fact]
    public async Task Сертификат_доверенного_корня_пропускает_и_ключ_едет_заголовком()
    {
        Довериться(_kmp);

        var ответ = await host.CreateClient().PostAsync("/api/keymanager/test", null, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!["certificates"]!.GetValue<int>().ShouldBe(1);
        _kmp.Запросов.ShouldBe(1);
    }

    [Fact]
    public async Task Ключ_не_уходит_на_подменённый_адрес()
    {
        // ⚠ Сертификат подменного сервера проходит проверку — отказывает привязка ключа к адресу.
        await using var подменный = new ЗаглушкаKmp("ключ-подменного");
        await подменный.ЗапуститьAsync();
        Довериться(подменный);

        host.Settings.Values["keymanager.base_url"] = подменный.Адрес;

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
        Довериться(_kmp);
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

    /// <summary>Сертификат без ключа — открытые данные, ему можно.</summary>
    [Fact]
    public async Task Ключ_не_уходит_по_почте_без_шифрования()
    {
        Довериться(_kmp);
        host.Settings.Values["mail.enabled"] = true;
        host.Settings.Values["mail.smtp_host"] = "127.0.0.1";
        host.Settings.Values["mail.smtp_port"] = 1;
        host.Settings.Values["mail.smtp_security"] = "none";
        host.Settings.Values["mail.from_address"] = "vault@example.com";

        var клиент = host.CreateClient();

        async Task<HttpResponseMessage> ОтправитьAsync(string[] форматы)
        {
            var создано = await клиент.PostAsJsonAsync(
                "/api/certificate-subscriptions",
                new { common_name = "site.example.com", recipients = new[] { "a@example.com" }, formats = форматы },
                Ct);
            var id = (await создано.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<Guid>();

            return await клиент.PostAsync($"/api/certificate-subscriptions/{id}/send", null, Ct);
        }

        var сКлючом = await ОтправитьAsync(["PEM", "KEY"]);
        сКлючом.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await сКлючом.Content.ReadFromJsonAsync<JsonObject>(Ct))!["message"]!.GetValue<string>()
            .ShouldContain("без шифрования");

        (await ОтправитьAsync(["PEM"])).StatusCode.ShouldBe(HttpStatusCode.OK, "сертификат без ключа уходит");
    }

    /// <summary>⚠ Запрет выше читает настройку почты по имени: переименование иначе прошло бы молча.</summary>
    [Fact]
    public async Task Настройка_защиты_SMTP_есть_у_модуля_почты_с_вариантом_none()
    {
        var настройки = await host.CreateClient().GetFromJsonAsync<JsonNode>("/api/settings/system", Ct);

        настройки!.ToJsonString().ShouldContain("\"mail.smtp_security\"");
        настройки.ToJsonString().ShouldContain("\"none\"");
    }

    [Fact]
    public async Task Ручная_отправка_кладёт_письма_с_запечатанными_вложениями()
    {
        Довериться(_kmp);
        host.Settings.Values["mail.enabled"] = true;
        host.Settings.Values["mail.smtp_host"] = "127.0.0.1";
        host.Settings.Values["mail.smtp_port"] = 1;
        host.Settings.Values["mail.smtp_security"] = "tls";
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
        подписка["sent_serials"]!["site.example.com"]!.GetValue<string>().ShouldBe("0a1b2c");
        подписка["last_error"].ShouldBeNull();

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var письма = await db.Database
            .SqlQueryRaw<byte[]>(
                "SELECT attachments AS \"Value\" FROM mail_outbox WHERE starts_with(kind, 'certificate_delivery') ORDER BY to_address")
            .ToListAsync(Ct);

        письма.Count.ShouldBe(2, "по письму на адрес");

        foreach (var вложения in письма)
        {
            вложения.ShouldNotBeNull().ShouldNotBeEmpty();
            вложения.AsSpan().IndexOf(ЗаглушкаKmp.ЗакрытыйКлюч).ShouldBe(-1, "ключ в очереди не лежит открытым");
        }

        // Хост включает HTML-часть писем.
        (await db.Database
            .SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM mail_outbox WHERE starts_with(kind, 'certificate_delivery') AND html_body IS NOT NULL")
            .SingleAsync(Ct)).ShouldBe(2, "у каждого письма — HTML-часть");
    }

    [Fact]
    public async Task Выгрузка_сертификата_отдаёт_файл_под_именем_вложения_и_без_кэша()
    {
        Довериться(_kmp);
        await СнимокAsync();

        var ответ = await host.CreateClient().PostAsJsonAsync(
            "/api/keymanager/certificates/download",
            new { common_name = "site.example.com", serial_number = "0a1b2c", format = "pem" },
            Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK, await ответ.Content.ReadAsStringAsync(Ct));
        (await ответ.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(ЗаглушкаKmp.Сертификат);
        ответ.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        ответ.Content.Headers.GetValues("Content-Disposition").Single()
            .ShouldBe("attachment; filename=\"site.example.com.pem\"");
        ответ.Headers.CacheControl!.NoStore.ShouldBeTrue();
        _kmp.Запросов.ShouldBe(1);
    }

    /// <summary>Без пароля в Key Manager Plus — <c>null</c>.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Закрытый_ключ_приходит_текстом_PEM_и_пароль_по_возможности(bool парольЕсть)
    {
        Довериться(_kmp);
        await СнимокAsync();
        _kmp.Пароль = парольЕсть ? ЗаглушкаKmp.ПарольКлюча : null;

        var ответ = await host.CreateClient().PostAsJsonAsync(
            "/api/keymanager/certificates/private-key",
            new { common_name = "site.example.com", serial_number = "0a1b2c" },
            Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK, await ответ.Content.ReadAsStringAsync(Ct));
        ответ.Headers.CacheControl!.NoStore.ShouldBeTrue();

        var тело = (await ответ.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        тело["private_key"]!.GetValue<string>().ShouldBe(System.Text.Encoding.UTF8.GetString(ЗаглушкаKmp.ЗакрытыйКлюч));

        if (парольЕсть)
        {
            тело["passphrase"]!.GetValue<string>().ShouldBe(ЗаглушкаKmp.ПарольКлюча);
        }
        else
        {
            тело["passphrase"].ShouldBeNull("отказ Key Manager Plus в пароле обязан стать null, а не ошибкой");
        }

        // ⚠ Строка журнала сохранена: без SaveChangesAsync она пропала бы молча.
        var журнал = await host.CreateClient().GetFromJsonAsync<JsonNode>(
            "/api/audit?action=keymanager.private_key_viewed&limit=50", Ct);
        журнал!.ToJsonString().ShouldContain("keymanager.private_key_viewed");
    }

    [Fact]
    public async Task Ключ_не_текстом_PEM_отвечает_409_с_причиной()
    {
        Довериться(_kmp);
        await СнимокAsync();
        _kmp.КлючВDer = true;

        var ответ = await host.CreateClient().PostAsJsonAsync(
            "/api/keymanager/certificates/private-key",
            new { common_name = "site.example.com", serial_number = "0a1b2c" },
            Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var тело = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        тело["code"]!.GetValue<string>().ShouldBe("KEYMANAGER_UNAVAILABLE");
        тело["details"]!["reason"]!.GetValue<string>().ShouldBe("Key Manager Plus отдал ключ не в виде PEM");
    }

    /// <summary>Ни ключа, ни пароля — ни в журнале, ни где-либо в логе.</summary>
    [Fact]
    public async Task Показ_ключа_пишется_в_журнал_без_ключа_и_пароля()
    {
        Довериться(_kmp);
        await СнимокAsync();
        _kmp.Пароль = ЗаглушкаKmp.ПарольКлюча;

        var перо = new ВсеСтроки();

        await using var фабрика = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            // ⚠ Serilog сторонних поставщиков не зовёт: подменяется вся фабрика логов.
            services.AddSingleton<ILoggerFactory>(new LoggerFactory([перо]));
        }));

        // У отдельного хоста свои опции модуля.
        Корни(фабрика.Services).Add(_kmp.Корень);

        var клиент = фабрика.CreateClient();

        // ⚠ Мастер-ключ у каждого хоста свой: ключ API вводится заново.
        (await клиент.PutAsJsonAsync("/api/keymanager/api-key", new { api_key = Ключ }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await клиент.PostAsJsonAsync(
                "/api/keymanager/certificates/private-key",
                new { common_name = "site.example.com", serial_number = "0a1b2c" },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await клиент.PostAsJsonAsync(
                "/api/keymanager/certificates/download",
                new { common_name = "site.example.com", serial_number = "0a1b2c", format = "KEY" },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var строки = перо.Снимок();

        var показ = строки.Single(с => с.Contains("keymanager.private_key_viewed", StringComparison.Ordinal));
        показ.ShouldContain("site.example.com");
        показ.ShouldContain("0a1b2c");

        var выгрузка = строки.Single(с => с.Contains("keymanager.certificate_downloaded", StringComparison.Ordinal));
        выгрузка.ShouldContain("\"format\":\"KEY\"");

        foreach (var строка in строки)
        {
            строка.ShouldNotContain("BEGIN PRIVATE KEY");
            строка.ShouldNotContain(ЗаглушкаKmp.ПарольКлюча);
        }
    }

    /// <summary>Пара «site.example.com + 0a1b2c» в снимке опроса.</summary>
    private async Task СнимокAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
            """
            INSERT INTO keymanager_certificates (common_name, serial_number, expires_on, first_seen_at, last_seen_at)
            VALUES ('site.example.com', '0a1b2c', '2027-10-02', now(), now())
            ON CONFLICT (common_name, serial_number) DO NOTHING
            """,
            Ct);
    }

    /// <summary>Запоминает каждую строку лога хоста.</summary>
    private sealed class ВсеСтроки : ILoggerProvider
    {
        private readonly List<string> _строки = [];

        public List<string> Снимок()
        {
            lock (_строки)
            {
                return [.. _строки];
            }
        }

        public ILogger CreateLogger(string categoryName) => new Перо(_строки);

        public void Dispose()
        {
        }

        private sealed class Перо(List<string> строки) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                lock (строки)
                {
                    строки.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
                }
            }
        }
    }

    /// <summary>Корни, которым соединение с Key Manager Plus доверяет в этом хосте.</summary>
    private static X509Certificate2Collection Корни(IServiceProvider services) =>
        services.GetRequiredService<KeyManagerModuleOptions>().TrustedRoots;

    /// <summary>Доверять заглушке, как рабочий хост — удостоверяющему центру.</summary>
    private void Довериться(ЗаглушкаKmp заглушка) => Корни(host.Services).Add(заглушка.Корень);

    /// <summary>Почта настроена, но сервер недоступен: письма копятся в очереди.</summary>
    private void ПочтаНаНедоступныйСервер()
    {
        host.Settings.Values["mail.enabled"] = true;
        host.Settings.Values["mail.smtp_host"] = "127.0.0.1";
        host.Settings.Values["mail.smtp_port"] = 1;
        host.Settings.Values["mail.smtp_security"] = "tls";
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

    /// <summary>Ждущих писем с вложениями на адрес; насос тем временем пробует недоступный сервер.</summary>
    private Task<int> ЖдутAsync(string адрес) => СчётAsync(
        "length(attachments) > 0 AND next_attempt_at < '9999-01-01'", адрес);

    /// <summary>Отменённых писем на адрес: вложения стёрты, срок — «никогда».</summary>
    private Task<int> ОтменёнAsync(string адрес) => СчётAsync(
        "coalesce(length(attachments), 0) = 0 AND next_attempt_at >= '9999-01-01'", адрес);

    private async Task<int> СчётAsync(string условие, string адрес)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var запрос = "SELECT count(*)::int AS \"Value\" FROM mail_outbox WHERE starts_with(kind, 'certificate_delivery') "
            + "AND sent_at IS NULL AND to_address = {0} AND " + условие;

        var строки = await db.Database.SqlQueryRaw<int>(запрос, адрес).ToListAsync(Ct);

        return строки[0];
    }

    /// <summary>Key Manager Plus в процессе: HTTPS с самоподписанным сертификатом и четырьмя методами.</summary>
    private sealed class ЗаглушкаKmp(string ключ) : IAsyncDisposable
    {
        public static readonly byte[] ЗакрытыйКлюч = "-----BEGIN PRIVATE KEY-----\nзаглушка\n"u8.ToArray();

        public static readonly byte[] Сертификат = "-----BEGIN CERTIFICATE-----\nзаглушка\n"u8.ToArray();

        public const string ПарольКлюча = "пароль-заглушки-не-для-лога";

        private WebApplication? _приложение;
        private X509Certificate2? _сертификат;
        private int _запросов;

        public string Адрес { get; private set; } = "";

        /// <summary>Сертификат заглушки — он же корень своей цепочки.</summary>
        public X509Certificate2 Корень => _сертификат ?? throw new InvalidOperationException("заглушка не запущена");

        public int Запросов => _запросов;

        /// <summary><c>null</c> — отказ, как у Key Manager Plus без пароля.</summary>
        public string? Пароль { get; set; }

        public bool КлючВDer { get; set; }

        public async Task ЗапуститьAsync()
        {
            using var пара = RSA.Create(2048);
            var запрос = new CertificateRequest("CN=kmp.example.test", пара, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // ⚠ Имя из адреса — в SAN: проверку имени хоста не выключить.
            var имена = new SubjectAlternativeNameBuilder();
            имена.AddIpAddress(IPAddress.Loopback);
            имена.AddDnsName("localhost");
            запрос.CertificateExtensions.Add(имена.Build());

            using var временный = запрос.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

            // ⚠ Через PKCS#12: SChannel не принимает эфемерный ключ, а CreateSelfSigned даёт именно его.
            _сертификат = X509CertificateLoader.LoadPkcs12(временный.Export(X509ContentType.Pkcs12), null);

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

                // Как живой 7.1.1: необъявленный параметр — 400.
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
                            ? (КлючВDer ? [0x30, 0x82, 0x04, 0xBD, 0x02, 0x01, 0x00] : ЗакрытыйКлюч)
                            : Сертификат,
                        "application/octet-stream"),
                    "getCertificatePassphrase" => Пароль is { } пароль
                        ? Results.Json(new
                        {
                            name = "GetCertificatePassphrase",
                            result = new { status = "Success", message = "Passphrase fetched successfully" },
                            details = new { Passphrase = пароль },
                        })
                        : Results.Json(new { result = new { status = "Failed", message = "Passphrase not available" } }),
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
