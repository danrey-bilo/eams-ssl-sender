using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Api.Schema;
using EmailSslSender.Modules.KeyManager;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Modules.Audit;
using Modules.Core.Composition;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Modules.Crypto;
using Modules.Hosting;
using Modules.Identity;
using Modules.Mail;
using Modules.Settings;
using Modules.Tokens;
using Npgsql;
using Serilog;

// ⚠ Проба здоровья — до построения приложения: ей не нужны ни секреты, ни база.
if (args is [Bootstrap.Health, ..])
{
    return await Bootstrap.ProbeAsync();
}

var builder = WebApplication.CreateBuilder(args);

var rateLimits = new RateLimitGroups(builder.Configuration);

// ⚠ Исключения — через SafeExceptionSink: текст исключения Npgsql несёт строку подключения.
builder.Host.UseSerilog((context, config) => config
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .ReadFrom.Configuration(context.Configuration)
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Sink(new SafeExceptionSink(new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter())
        .CreateLogger())));

// ⚠ Движок прав — ДО модуля токенов: токены оборачивают последнюю регистрацию IPermissionChecker.
builder.Services.AddScoped<IPermissionChecker, AdministratorPermissions>();

builder.Services
    .AddControllers(mvc =>
    {
        // ⚠ Закрыто по умолчанию: действие без объявления доступа — 403.
        mvc.Filters.Add<PermissionFilter>();
    })
    .AddJsonOptions(json => json.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All))
    .AddIdentityModule(options =>
    {
        options.SignInAttemptsPerMinute = rateLimits.LoginPerMinute;

        // ⚠ Имя куки прежнее: сменённое выводит всех на вход.
        options.TotpIssuer = Product.Name;
        options.RefreshCookieName = "__Host-sender-refresh";

        // Аутентификатор обязателен всем; код письмом лишь пускает в его настройку.
        options.RequireAuthenticatorForEveryone = true;
        options.AuthenticatorExcludesEmailCode = true;
        options.ChallengeCodeAttempts = 5;
        options.RefreshCookieStaysCookie = true;
        options.LastSuperuserCountsOnlySignInCapable = true;

        // ⚠ Argon2 берёт 64 МиБ на хеш.
        options.PasswordHashConcurrency = 4;
        options.PasswordHashQueueLimit = 32;

        options.Features.TrustedClients = false;
        options.Features.ExtensionSignIn = false;
        options.Features.Directory = false;
        options.Features.StepUpEmail = false;
    })
    .AddTokensModule(options =>
    {
        // Только вход в панель.
        options.Features.ApiKeys = false;
        options.Features.DeviceTokens = false;
        options.Features.DownloadTickets = false;
    })
    .AddSettingsModule(options => options.Features.UserPreferences = false)
    .AddMailModule(options =>
    {
        options.RunMaintenance = true;

        // Рамка письма с именем сервиса.
        options.ProductName = Product.Name;
        options.Frame = true;

        // Текст + HTML: перечень сертификатов — таблицей.
        options.Html = true;

        // ⚠ Пароль SMTP — только тому серверу, порту, защите и логину, для которых его ввели.
        options.BindSmtpPassword = true;
    })
    .AddKeyManagerModule(options => options.RunPoller = true)
    // ⚠ Журнал — ПОСЛЕДНИМ: его писатели перекрывают прежние регистрации.
    .AddAuditModule(options =>
    {
        options.RunMaintenance = true;
        options.StrictActionCatalog = builder.Environment.IsDevelopment();

        // ⚠ Журнал хранится всегда; граница — на случай, если срок однажды включат.
        options.Features.Retention = false;
        options.MinimumRetentionMonths = 12;

        // Без Redis детектор молчал бы; показ ключа и так пишется в журнал.
        options.Features.MassRevealDetection = false;
    })
    .UseErrorEnvelope();

// ⚠ Набор миграций проекта — ПОСЛЕДНИМ: он правит значения поверх таблиц модулей.
builder.Services.AddModuleSchema(SenderSchema.Declaration);

// Без мастер-ключа нет старта.
var masterKeys = MasterKeyFile.Load(builder.Configuration);
builder.Services.AddSingleton(masterKeys.Ring);
builder.Services.AddSingleton<ISecretEnvelope, SecretEnvelope>();
builder.Services.AddSingleton<IIntegrityProtector, IntegrityProtector>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPublicOrigin, PublicOrigin>();
builder.Services.AddScoped<RequestCurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<RequestCurrentUser>());
builder.Services.AddScoped<CurrentUserMiddleware>();
builder.Services.AddSingleton(TimeProvider.System);

// Каждая строка журнала — ещё и строка «audit …» в лог.
builder.Services.AddScoped<LogAuditWriter>();
builder.Services.Decorate<IAuditWriter>((журнал, sp) => new LoggedAuditWriter(журнал, sp.GetRequiredService<LogAuditWriter>()));
builder.Services.Decorate<ISecurityEventWriter>(
    (журнал, sp) => new LoggedSecurityEventWriter(журнал, sp.GetRequiredService<LogAuditWriter>()));
builder.Services.Decorate<IAuthorizationAuditor>(
    (журнал, sp) => new LoggedAuthorizationAuditor(журнал, sp.GetRequiredService<LogAuditWriter>()));

// Пути объявляют модули, числа — проект.
builder.Services.AddModuleRateLimits(rateLimits.Policies);
rateLimits.ChainProjectLimits(builder.Services);

builder.Services.AddSingleton(new AdvisoryLockSpace(0x53454E44));

builder.Services.AddSingleton(new EndpointDocsOptions
{
    ExtensionPrefix = "x-sender",
    Title = $"{Product.Name} API",
    Description = "Контракт сервера рассылки SSL-сертификатов из ManageEngine Key Manager Plus. "
        + "Модуль операции — расширение x-sender-module, как она пускает — x-sender-access.",
});

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<ErrorEnvelopeDocument>();

    // Контроллеры модулей internal — штатный генератор их XML-комментарии не берёт.
    options.AddOperationTransformer<EndpointDocsDocument>();
    options.AddDocumentTransformer<EndpointDocsDocument>();
});

// Файлов API не принимает: 256 КБ вместо 30 МБ Kestrel.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);

builder.Services.AddSenderDatabase(ConnectionSecrets.Postgres(builder.Configuration));

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadiness>("database", tags: ["ready"]);

// ⚠ X-Forwarded-* — только от названных прокси; -Host не принимается: Cloudflare пропускает его от клиента.
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

// После номера запроса и заголовков прокси: журналу нужны оба.
app.UseMiddleware<AuditContextMiddleware>();
app.UseMiddleware<TokenInAddressGuard>();
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

app.MapPanel();

if (Bootstrap.IsCommand(args))
{
    return await RunCommandAsync(app, args);
}

app.MapControllers();

// ⚠ Схему накатывает `migrate`, не старт: отставшая схема — отказ стартовать.
await app.RequireCurrentSchemaAsync();

// По версии видно, что обновление подняло новый образ.
app.Logger.LogInformation("sender_starting {Version}", Bootstrap.InstallationVersion(typeof(Program).Assembly));

await app.RunAsync();

return 0;

// 0 — цепочка журнала сошлась, 2 — разрыв.
static async Task<int> VerifyAuditChainAsync(WebApplication app)
{
    var итог = await AuditModule.CheckChainAsync(app.Services);

    foreach (var месяц in итог.Lines)
    {
        Console.WriteLine(
            $"{Bootstrap.VerifyAuditChain}: {месяц.Partition} — звеньев {месяц.Chained}"
            + (месяц.Unchained > 0 ? $", до цепочки {месяц.Unchained}" : string.Empty)
            + (месяц.Ok ? ", сошлось" : $", ⚠ РАЗРЫВ: {месяц.Broken}"));
    }

    Console.WriteLine(итог.Ok
        ? $"{Bootstrap.VerifyAuditChain}: журнал цел"
        : $"{Bootstrap.VerifyAuditChain}: ⚠ журнал изменён задним числом — сверьте с якорями audit_chain_anchor в логе");

    return итог.Ok ? 0 : 2;
}

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

        if (args[0] == Bootstrap.VerifyAuditChain)
        {
            return await VerifyAuditChainAsync(app);
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

        // Пароль, фактор, блокировка и сессии — одной транзакцией модуля.
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

// Учётные записи из окружения (Portainer без консоли): первый администратор — только на пустой установке,
// ⚠ сброс доступа — один раз на пару «адрес + пароль», иначе повторялся бы при каждом обновлении стека.
static async Task StartupAccountsAsync(WebApplication app)
{
    var config = app.Configuration;

    if (config["SENDER_INIT_ADMIN_EMAIL"] is { Length: > 0 } первый
        && config["SENDER_INIT_ADMIN_PASSWORD"] is { Length: > 0 } парольПервого)
    {
        if (await AnyUserAsync(app))
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
        var метка = Bootstrap.ResetAccessMark(MasterKeyFile.Content(config).Content, кому, новый);

        await using var db = new NpgsqlConnection(app.Services.GetRequiredService<DatabaseStartup.SchemaConnection>().Value);
        await db.OpenAsync();

        await using (var было = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM sender_reset_access_marks WHERE mark = @m)", db))
        {
            было.Parameters.AddWithValue("m", метка);

            if (await было.ExecuteScalarAsync() is true)
            {
                Console.WriteLine(
                    $"{Bootstrap.Migrate}: SENDER_RESET_ACCESS_* с этим паролем уже применены — пропуск. Уберите их "
                    + "из .env; сбросить доступ снова — задайте другой пароль.");
                return;
            }
        }

        var итог = await IdentityModule.ResetAccessAsync(app.Services, кому, новый);

        await using (var запомнить = new NpgsqlCommand(
            "INSERT INTO sender_reset_access_marks (mark) VALUES (@m) ON CONFLICT (mark) DO NOTHING", db))
        {
            запомнить.Parameters.AddWithValue("m", метка);
            await запомнить.ExecuteNonQueryAsync();
        }

        Console.WriteLine(Bootstrap.ResetAccessSummary(итог));
        Console.WriteLine(
            $"{Bootstrap.Migrate}: сброс применён один раз — следующие обновления стека его не повторят. Уберите "
            + "SENDER_RESET_ACCESS_* из .env: пароль в переменных стека виден в Portainer и docker inspect.");
    }
}

// Включая удалённые: они тоже след того, что установка не пуста.
static async Task<bool> AnyUserAsync(WebApplication app)
{
    await using var db = new NpgsqlConnection(app.Services.GetRequiredService<DatabaseStartup.SchemaConnection>().Value);
    await db.OpenAsync();

    await using var есть = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM users)", db);

    return await есть.ExecuteScalarAsync() is true;
}

static async Task<int> MigrateCommandAsync(WebApplication app)
{
    try
    {
        var done = await app.MigrateAsync();

        if (done.Count == 0)
        {
            Console.WriteLine($"{Bootstrap.Migrate}: схема уже совпадает с кодом, накатывать нечего");
        }
        else
        {
            Console.WriteLine($"{Bootstrap.Migrate}: сделано {done.Count}");

            foreach (var name in done)
            {
                Console.WriteLine($"  {name}");
            }
        }

        // ⚠ До учётных записей из окружения: им может понадобиться второй фактор.
        var прежние = new LegacySecrets(
            app.Services.GetRequiredService<ISecretEnvelope>(),
            LegacySecrets.Keys(MasterKeyFile.Content(app.Configuration).Content));

        foreach (var итог in await прежние.RunAsync(app.Services.GetRequiredService<DatabaseStartup.SchemaConnection>().Value))
        {
            if (итог.Переложено > 0 || итог.НеОткрылось > 0)
            {
                Console.WriteLine(
                    $"{Bootstrap.Migrate}: {итог.Name} — переложено в конверт модулей {итог.Переложено} из {итог.Просмотрено}"
                    + (итог.НеОткрылось > 0
                        ? $", ⚠ НЕ ОТКРЫЛОСЬ {итог.НеОткрылось}: значение задаётся заново (аутентификатор — reset-access)"
                        : string.Empty));
            }
        }

        // Ключ API из 1.x без привязки — к текущему адресу, иначе опрос отказывал бы.
        if (await KeyManagerModule.BindLegacyApiKeyAsync(app.Services))
        {
            Console.WriteLine($"{Bootstrap.Migrate}: ключ API Key Manager Plus привязан к текущему адресу");
        }

        // Роль сервиса — каждым migrate, и без новых миграций (смена пароля).
        if (RuntimeRole.FromConfiguration(app.Configuration) is var (role, password))
        {
            await using var connection = new NpgsqlConnection(
                app.Services.GetRequiredService<DatabaseStartup.SchemaConnection>().Value);

            await connection.OpenAsync();
            await RuntimeRole.EnsureAsync(connection, role, password);

            Console.WriteLine($"{Bootstrap.Migrate}: роль сервиса {role} — только чтение и запись строк");
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
    catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
    {
        // Служба api ходит ролью без DDL — подсказать, где меняют схему.
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
    var нечитаемыхВсего = 0;

    foreach (var target in targets)
    {
        int просмотрено = 0, переложено = 0, нечитаемых = 0;
        string? курсор = null;

        do
        {
            var порция = await target.RewrapAsync(batch, курсор);
            просмотрено += порция.Seen;
            переложено += порция.Rewrapped;
            нечитаемых += порция.Unreadable;
            курсор = порция.Next;
        }
        while (курсор is not null);

        всего += переложено;
        нечитаемыхВсего += нечитаемых;

        Console.WriteLine(
            $"  {target.Name}: просмотрено {просмотрено}, переложено {переложено}"
            + (нечитаемых > 0 ? $", НЕ ОТКРЫЛОСЬ {нечитаемых}" : string.Empty));
    }

    if (нечитаемыхВсего > 0)
    {
        // ⚠ Выбросить прежнюю версию ключа — потерять неоткрывшееся навсегда.
        Console.WriteLine(
            $"{Bootstrap.RewrapKeys}: ⚠ не открылось {нечитаемыхВсего} — прежние версии ключа из файла НЕ убирать, "
            + "пока не разобрались.");
        return 1;
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

    // ⚠ Без текста исключения: в нём строка подключения.
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
