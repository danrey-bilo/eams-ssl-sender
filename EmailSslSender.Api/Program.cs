using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Modules.Identity;
using EmailSslSender.Modules.KeyManager;
using EmailSslSender.Modules.Mail;
using EmailSslSender.Modules.Settings;
using EmailSslSender.Modules.Tokens;
using EmailSslSender.Shared.Api;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

// ⚠ Проба здоровья контейнера — ДО построения приложения: ей не нужны ни секреты, ни база, и
// падать по их причинам она не должна. Разбор у Bootstrap.ProbeAsync.
if (args is [Bootstrap.Health, ..])
{
    return await Bootstrap.ProbeAsync();
}

var builder = WebApplication.CreateBuilder(args);

var rateLimits = new RateLimitGroups(builder.Configuration);

// Структурные логи в stdout одной строкой JSON на событие. ⚠ Исключения чужих логгеров (EF
// Core, Npgsql, ASP.NET) проходят через SafeExceptionSink: текст исключения Npgsql несёт строку
// подключения целиком, в лог уходит цепочка типов и стек.
builder.Host.UseSerilog((context, config) => config
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .ReadFrom.Configuration(context.Configuration)
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Sink(new SafeExceptionSink(new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter())
        .CreateLogger())));

// ⚠ Порядок модулей значим ровно в одном месте: учётные записи раньше токенов не обязательны,
// но токены обязаны быть — без них вход невозможен, и TokenContractCheck роняет старт.
builder.Services
    .AddControllers(mvc =>
    {
        // ⚠ DEFAULT DENY: каждое действие требует входа, кроме помеченных [PublicEndpoint].
        mvc.Filters.Add<AuthenticationFilter>();
    })
    .AddJsonOptions(json => json.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All))
    .AddIdentityModule(options => options.SignInAttemptsPerMinute = rateLimits.LoginPerMinute)
    .AddTokensModule()
    .AddSettingsModule()
    .AddMailModule(options => options.RunMaintenance = true)
    .AddKeyManagerModule(options => options.RunPoller = true)
    .UseErrorEnvelope();

// Мастер-ключ: без него нет старта (разбор у MasterKeyFile). Им обёрнуты ключи данных пароля
// SMTP, секретов TOTP и тел писем в очереди.
var masterKeys = MasterKeyFile.Load(builder.Configuration);
builder.Services.AddSingleton(masterKeys.Ring);
builder.Services.AddSingleton<ISecretEnvelope, SecretEnvelope>();
builder.Services.AddSingleton<IIntegrityProtector, IntegrityProtector>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPublicOrigin, PublicOrigin>();
builder.Services.AddScoped<RequestCurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<RequestCurrentUser>());
builder.Services.AddScoped<CurrentUserMiddleware>();
builder.Services.AddScoped<IAuditWriter, LogAuditWriter>();
builder.Services.AddSingleton(TimeProvider.System);

// Предел частоты по адресу — потолок против потока; перебор одной учётной записи держат
// предел на пару «адрес + почта» и блокировка после неудач (RateLimitGroups).
builder.Services.AddSingleton(rateLimits);
builder.Services.AddRateLimiter(limiter =>
{
    var поАдресу = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (rateLimits.Of(context.Request.Path) is not { } group)
        {
            return RateLimitPartition.GetNoLimiter("прочее");
        }

        // ⚠ IPv6 — по сети /64, а не по адресу: своя /64 у абонента — это 2⁶⁴ адресов, и предел
        // «на адрес» обходился бы сменой адреса на каждую попытку (RateLimitAddress).
        var address = RateLimitAddress.Key(context.Connection.RemoteIpAddress) ?? "неизвестно";

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{group.Name}:{address}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = group.PerAddress,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    // ⚠ Второе звено — предел на всех клиентов вместе для групп, где запрос считает argon2 без
    // входа (ревью безопасности 2026-10-20): предел «на адрес» поток со множества адресов не
    // держит, а память и процессор у сервера одни.
    var всего = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        rateLimits.Of(context.Request.Path) is { Total: { } total } group
            ? RateLimitPartition.GetFixedWindowLimiter(
                "всего:" + group.Name,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = total,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })
            : RateLimitPartition.GetNoLimiter("прочее"));

    limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(поАдресу, всего);

    limiter.OnRejected = (context, token) => new ValueTask(RateLimitGroups.RejectAsync(
        context.HttpContext,
        context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null,
        token));
});

builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ErrorEnvelopeDocument>());

// ⚠ Тело запроса — не больше 256 КБ (по умолчанию Kestrel ждёт до 30 МБ). Самые большие тела
// здесь — настройки и подписка на двадцать адресов; файлов API не принимает, а анонимные
// вход и сброс пароля не должны держать память сервера чужими мегабайтами.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);

builder.Services.AddSenderDatabase(ConnectionSecrets.Postgres(builder.Configuration));

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadiness>("database", tags: ["ready"]);

// ⚠ За обратным прокси адрес клиента приходит заголовком, а заголовок подделывается. Доверяем
// ему только от названных прокси: иначе предел попыток входа обходился бы подстановкой
// X-Forwarded-For.
//
// ⚠ X-Forwarded-Host НЕ принимается: Cloudflare пропускает его от клиента как есть, а имя
// хоста приложению не нужно — ссылки в письмах строятся только из настройки server.public_url.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    foreach (var proxy in Trusted("ForwardedHeaders:KnownProxies"))
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }

    foreach (var network in Trusted("ForwardedHeaders:KnownNetworks"))
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }

    IEnumerable<string> Trusted(string key) =>
        (builder.Configuration[key] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
});

var app = builder.Build();

masterKeys.Announce(app.Logger);

app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseRouting();
app.UseRateLimiter();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseMiddleware<UserRateLimitMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi()
        .WithMetadata(new PublicEndpointAttribute(
            "документ схемы, поднимается только в разработке; в бою маршрута нет"));
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
    .WithMetadata(new PublicEndpointAttribute(
        "живость опрашивает оркестратор, у которого нет и не может быть учётной записи"));

app.MapHealthChecks("/api/ready", new HealthCheckOptions { ResponseWriter = WriteReadinessAsync })
    .WithMetadata(new PublicEndpointAttribute(
        "готовность опрашивает оркестратор до того, как появится хоть один пользователь"));

// Веб-панель и страницы ссылок из писем — React-приложение из wwwroot (разбор у PanelFiles).
app.MapPanel();

if (Bootstrap.IsCommand(args))
{
    return await RunCommandAsync(app, args);
}

app.MapControllers();

// ⚠ Схема накатывается командой `migrate`, а не стартом: иначе `docker compose up` и есть
// миграция, и копию базы до неё снять негде. Отставшая схема — отказ стартовать.
await app.RequireCurrentSchemaAsync();

await app.RunAsync();

return 0;

static async Task<int> RunCommandAsync(WebApplication app, string[] args)
{
    try
    {
        if (args[0] == Bootstrap.ExportOpenApi)
        {
            return await ExportOpenApiAsync(app, args);
        }

        if (args[0] == Bootstrap.RewrapKeys)
        {
            return await RewrapAsync(app, args);
        }

        if (args[0] == Bootstrap.Migrate)
        {
            return await MigrateCommandAsync(app);
        }

        Bootstrap.ValidateSuperuserArguments(args);

        var email = Bootstrap.Argument(args, "--email")
            ?? throw new AppException(
                ErrorCodes.Validation, "Не задан --email", HttpStatusCode.BadRequest);

        if (args[0] == Bootstrap.ResetSuperadminTwoFactor)
        {
            var userId = await IdentityModule.ResetSuperuserTwoFactorAsync(app.Services, email);
            Console.WriteLine(
                $"{Bootstrap.ResetSuperadminTwoFactor}: второй фактор снят у {email} ({userId}); "
                + "все сессии закрыты. Следующий вход потребует подключить аутентификатор.");
            return 0;
        }

        var password = Bootstrap.ReadPassword(
            Bootstrap.Argument(args, "--password-file"),
            args[0] == Bootstrap.ResetAccess ? "Новый пароль: " : "Пароль администратора: ");

        // ⚠ Пароль, фактор, блокировка и сессии — одной транзакцией (разбор у AccessRecovery).
        // Отключённую учётную запись команда не включает и говорит об этом в итоге.
        if (args[0] == Bootstrap.ResetAccess)
        {
            var итог = await IdentityModule.ResetAccessAsync(app.Services, email, password);
            Console.WriteLine(Bootstrap.ResetAccessSummary(итог));
            return 0;
        }

        if (args[0] == Bootstrap.SetSuperadminPassword)
        {
            var userId = await IdentityModule.SetSuperuserPasswordAsync(app.Services, email, password);
            Console.WriteLine(
                $"{Bootstrap.SetSuperadminPassword}: пароль изменён у {email} ({userId}); "
                + "все сессии закрыты. Второй фактор сохранён.");
            return 0;
        }

        var id = await IdentityModule.BootstrapSuperuserAsync(
            app.Services, email, Bootstrap.Argument(args, "--display-name"), password);

        Console.WriteLine(
            $"{Bootstrap.CreateSuperadmin}: заведён администратор {email} ({id}). "
            + "Первый вход потребует подключить приложение-аутентификатор.");

        return 0;
    }
    catch (AppException error)
    {
        await Console.Error.WriteLineAsync($"Ошибка: {error.Message}");
        return 1;
    }
}

// Учётные записи из переменных окружения — для установки через Portainer, где нет консоли.
//
// ⚠ ⚠ В образе нет оболочки, и Portainer не может ни спросить пароль, ни выполнить
// create-superadmin / reset-access. Служба migrate выполняется при каждом развёртывании стека:
// - SENDER_INIT_ADMIN_EMAIL + SENDER_INIT_ADMIN_PASSWORD — первый администратор, ТОЛЬКО если
//   учётных записей ещё нет вовсе; иначе пропуск;
// - SENDER_RESET_ACCESS_EMAIL + SENDER_RESET_ACCESS_PASSWORD — то же, что reset-access. Повторяется
//   при каждом развёртывании, пока переменные заданы: после входа их убирают из стека.
static async Task StartupAccountsAsync(WebApplication app)
{
    var config = app.Configuration;

    if (config["SENDER_INIT_ADMIN_EMAIL"] is { Length: > 0 } первый
        && config["SENDER_INIT_ADMIN_PASSWORD"] is { Length: > 0 } парольПервого)
    {
        if (await IdentityModule.AnyUserAsync(app.Services))
        {
            Console.WriteLine(
                $"{Bootstrap.Migrate}: SENDER_INIT_ADMIN_* пропущены — учётные записи уже есть. Уберите их из .env.");
        }
        else
        {
            await IdentityModule.BootstrapSuperuserAsync(app.Services, первый, null, парольПервого);
            Console.WriteLine(
                $"{Bootstrap.Migrate}: заведён первый администратор {первый}. При первом входе — "
                + "подключить приложение-аутентификатор. Уберите SENDER_INIT_ADMIN_PASSWORD из .env.");
        }
    }

    if (config["SENDER_RESET_ACCESS_EMAIL"] is { Length: > 0 } кому
        && config["SENDER_RESET_ACCESS_PASSWORD"] is { Length: > 0 } новый)
    {
        var итог = await IdentityModule.ResetAccessAsync(app.Services, кому, новый);
        Console.WriteLine(Bootstrap.ResetAccessSummary(итог));
        Console.WriteLine(
            $"{Bootstrap.Migrate}: ⚠ уберите SENDER_RESET_ACCESS_* из .env и обновите стек — пока они "
            + "заданы, доступ сбрасывается при каждом развёртывании.");
    }
}

static async Task<int> MigrateCommandAsync(WebApplication app)
{
    try
    {
        var pending = await app.PendingMigrationsAsync();

        if (pending.Count == 0)
        {
            Console.WriteLine($"{Bootstrap.Migrate}: схема уже совпадает с кодом, накатывать нечего");
        }
        else
        {
            Console.WriteLine($"{Bootstrap.Migrate}: применяю {pending.Count}");

            foreach (var name in pending)
            {
                Console.WriteLine($"  {name}");
            }

            await app.MigrateAsync();
        }

        // ⚠ Роль сервиса приводится в порядок и без новых миграций: сменили файл пароля или
        // ставите поверх старой установки — `migrate` и есть способ это применить.
        if (RuntimeRole.FromConfiguration(app.Configuration) is var (role, password))
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
            var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();

            await connection.OpenAsync();
            await RuntimeRole.EnsureAsync(connection, role, password);

            Console.WriteLine($"{Bootstrap.Migrate}: роль сервиса {role} — только чтение и запись строк");
        }

        // ⚠ Секреты, заданные до привязки к адресу, привязываются здесь, к текущим настройкам:
        // консоль установки — доверенное место, а во время работы такой секрет не открывается
        // (ILegacySecretBinder).
        await using (var scope = app.Services.CreateAsyncScope())
        {
            foreach (var binder in scope.ServiceProvider.GetServices<ILegacySecretBinder>())
            {
                if (await binder.BindAsync())
                {
                    Console.WriteLine($"{Bootstrap.Migrate}: {binder.Name} привязан к текущим настройкам");
                }
            }
        }

        await StartupAccountsAsync(app);

        Console.WriteLine($"{Bootstrap.Migrate}: готово");
        return 0;
    }
    catch (AppException error)
    {
        await Console.Error.WriteLineAsync($"Ошибка: {error.Message}");
        return 1;
    }
    catch (Npgsql.PostgresException error) when (error.SqlState == Npgsql.PostgresErrorCodes.InsufficientPrivilege)
    {
        // ⚠ Служба api ходит ролью без права менять схему — и это задумано. Сказать, где её
        // меняют, полезнее, чем «permission denied for schema public».
        await Console.Error.WriteLineAsync(
            $"Ошибка: у этой роли нет права менять схему. Схему накатывает служба migrate: "
            + "docker compose run --rm migrate");
        return 1;
    }
}

static async Task<int> RewrapAsync(WebApplication app, string[] args)
{
    var batch = int.TryParse(
        Bootstrap.Argument(args, "--batch"), CultureInfo.InvariantCulture, out var parsed)
        ? Math.Clamp(parsed, 1, 10_000)
        : 200;

    await using var scope = app.Services.CreateAsyncScope();

    var ring = scope.ServiceProvider.GetRequiredService<MasterKeyRing>();
    var targets = scope.ServiceProvider.GetServices<IRewrapTarget>().ToList();

    Console.WriteLine(
        $"{Bootstrap.RewrapKeys}: действующая версия ключа {ring.CurrentVersion}, "
        + $"целей {targets.Count}, порция {batch}");

    var всего = 0;

    foreach (var target in targets)
    {
        int просмотрено = 0, переложено = 0;
        string? курсор = null;

        do
        {
            var порция = await target.RewrapAsync(batch, курсор);
            просмотрено += порция.Seen;
            переложено += порция.Rewrapped;
            курсор = порция.Next;
        }
        while (курсор is not null);

        всего += переложено;
        Console.WriteLine($"  {target.Name}: просмотрено {просмотрено}, переложено {переложено}");
    }

    Console.WriteLine(всего > 0
        ? $"{Bootstrap.RewrapKeys}: переложено {всего}. Прежние версии ключа больше не нужны живой "
          + "базе; копии базы, снятые раньше, по-прежнему открываются только ими."
        : $"{Bootstrap.RewrapKeys}: перекладывать было нечего — всё уже под версией {ring.CurrentVersion}.");

    return 0;
}

static async Task<int> ExportOpenApiAsync(WebApplication app, string[] args)
{
    var куда = Bootstrap.Argument(args, "--out")
        ?? throw new AppException(
            ErrorCodes.Validation, "Не задан --out", HttpStatusCode.BadRequest);

    var документ = await app.Services
        .GetRequiredKeyedService<Microsoft.AspNetCore.OpenApi.IOpenApiDocumentProvider>("v1")
        .GetOpenApiDocumentAsync(default);

    var буфер = new StringWriter();
    документ.SerializeAsV3(new Microsoft.OpenApi.OpenApiJsonWriter(буфер));

    await File.WriteAllTextAsync(куда, буфер + Environment.NewLine, new UTF8Encoding(false));

    Console.WriteLine($"{Bootstrap.ExportOpenApi}: документ записан в {куда}");
    return 0;
}

static async Task WriteReadinessAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json; charset=utf-8";

    if (report.Status == HealthStatus.Healthy)
    {
        await context.Response.WriteAsync("{\"status\":\"ok\"}", context.RequestAborted);
        return;
    }

    // ⚠ Наружу — имя проверки и «недоступно», без текста исключения: строка подключения в нём
    // целиком.
    var failed = report.Entries
        .Where(entry => entry.Value.Status != HealthStatus.Healthy)
        .ToDictionary(entry => entry.Key, entry => (object?)"недоступно");

    var body = new ErrorResponse(
        ErrorCodes.NotReady,
        "Зависимости недоступны",
        failed,
        context.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var id) ? id as string ?? "" : "");

    await context.Response.WriteAsync(JsonSerializer.Serialize(body), context.RequestAborted);
}

/// <summary>Точка входа — для интеграционных тестов (<c>WebApplicationFactory</c>).</summary>
public partial class Program;
