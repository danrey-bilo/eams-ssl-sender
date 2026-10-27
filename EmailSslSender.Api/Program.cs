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

// ⚠ Движок прав — ДО модуля токенов, и порядок несущий: токены оборачивают последнюю регистрацию
// IPermissionChecker, сужая права ключа до прав хозяина. Ролей в проекте нет (AdministratorPermissions).
builder.Services.AddScoped<IPermissionChecker, AdministratorPermissions>();

// Модули EAMS — по строке на модуль; что в них выключено и почему — у каждого звена. Хост знает,
// какие модули подключены, но не знает, что внутри.
builder.Services
    .AddControllers(mvc =>
    {
        // ⚠ DEFAULT DENY: каждое действие объявляет доступ — вход, право или открытость с причиной;
        // без объявления закрыто (PermissionFilter модуля хостинга).
        mvc.Filters.Add<PermissionFilter>();
    })
    .AddJsonOptions(json => json.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All))
    .AddIdentityModule(options =>
    {
        // ⚠ Предел на пару «адрес + почта» держит модуль: почту знает он.
        options.SignInAttemptsPerMinute = rateLimits.LoginPerMinute;

        // ⚠ Имя куки — прежнее: сменённое выводит всех на вход. Издатель — имя сервиса в аутентификаторе.
        options.TotpIssuer = Product.Name;
        options.RefreshCookieName = "__Host-sender-refresh";

        // Политика входа проекта (решение владельца, решение EAMS 0120): приложение-аутентификатор
        // обязательно всем, код письмом его не заменяет и лишь пускает в настройку, по одной расписке —
        // не больше пяти кодов.
        options.RequireAuthenticatorForEveryone = true;
        options.AuthenticatorExcludesEmailCode = true;
        options.ChallengeCodeAttempts = 5;
        options.RefreshCookieStaysCookie = true;
        options.LastSuperuserCountsOnlySignInCapable = true;

        // ⚠ Argon2 берёт 64 МиБ на хеш: четыре разом — потолок памяти под пароли в контейнере.
        options.PasswordHashConcurrency = 4;
        options.PasswordHashQueueLimit = 32;

        // Нет расширения браузера, настольного клиента и справочника сотрудников; переподтверждение —
        // только аутентификатором.
        options.Features.TrustedClients = false;
        options.Features.ExtensionSignIn = false;
        options.Features.Directory = false;
        options.Features.StepUpEmail = false;
    })
    .AddTokensModule(options =>
    {
        // Ключей интеграции, токенов машин и билетов скачивания проекту не нужно: только вход в панель.
        options.Features.ApiKeys = false;
        options.Features.DeviceTokens = false;
        options.Features.DownloadTickets = false;
    })
    .AddSettingsModule(options => options.Features.UserPreferences = false)
    .AddMailModule(options =>
    {
        options.RunMaintenance = true;

        // Письмо — «Hi», содержание, «Thank You,» и имя сервиса (решение владельца 2026-10-26).
        options.ProductName = Product.Name;
        options.Frame = true;

        // Письма уходят текстом и HTML: перечень сертификатов — таблицей, как в уведомлении Key Manager Plus
        // (решение владельца 2026-10-27). Текстовая часть остаётся — таблица в ней строкой на сертификат.
        options.Html = true;

        // ⚠ Пароль SMTP уходит только тому серверу, порту, защите и логину, для которых его ввели.
        options.BindSmtpPassword = true;
    })
    .AddKeyManagerModule(options => options.RunPoller = true)
    .UseErrorEnvelope();

// ⚠ Набор миграций проекта — ПОСЛЕДНИМ: он правит значения поверх таблиц модулей.
builder.Services.AddModuleSchema(SenderSchema.Declaration);

// Мастер-ключ: без него нет старта (разбор у MasterKeyFile). Им обёрнуты ключи данных пароля
// SMTP, секретов TOTP, ключа API Key Manager Plus и тел писем в очереди.
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

// Журнал — строками лога (LogAuditWriter): один писатель на запрос закрывает все контракты журнала.
builder.Services.AddScoped<IAuditContextAccessor, AuditContextAccessor>();
builder.Services.AddScoped<LogAuditWriter>();
builder.Services.AddScoped<IAuditWriter>(sp => sp.GetRequiredService<LogAuditWriter>());
builder.Services.AddScoped<ISecurityEventWriter>(sp => sp.GetRequiredService<LogAuditWriter>());
builder.Services.AddScoped<IAuthorizationAuditor>(sp => sp.GetRequiredService<LogAuditWriter>());

// Предел частоты по адресу — потолок против потока; перебор одной учётной записи держат предел на
// пару «адрес + почта» и блокировка после неудач. Пути объявляют модули, числа — проект.
builder.Services.AddModuleRateLimits(rateLimits.Policies);
rateLimits.ChainProjectLimits(builder.Services);

// Пространство замков проекта: проход миграций берёт замок в нём.
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

    // ⚠ Описания операций модулей: штатный генератор берёт XML-комментарии только у публичных
    // членов, а контроллеры модулей internal.
    options.AddOperationTransformer<EndpointDocsDocument>();
    options.AddDocumentTransformer<EndpointDocsDocument>();
});

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

// После номера обращения и разбора заголовков прокси: журналу нужны оба.
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

// Версия в журнале: по ней видно, что обновление стека действительно подняло новый образ.
app.Logger.LogInformation("sender_starting {Version}", Bootstrap.InstallationVersion(typeof(Program).Assembly));

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

        // ⚠ Пароль, фактор, блокировка и сессии — одной транзакцией (разбор у модуля учётных записей).
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
        var итог = await IdentityModule.ResetAccessAsync(app.Services, кому, новый);
        Console.WriteLine(Bootstrap.ResetAccessSummary(итог));
        Console.WriteLine(
            $"{Bootstrap.Migrate}: ⚠ уберите SENDER_RESET_ACCESS_* из .env и обновите стек — пока они "
            + "заданы, доступ сбрасывается при каждом развёртывании.");
    }
}

// Есть ли хоть одна учётная запись, включая удалённые: первый администратор заводится только на пустой
// установке, и удалённая учётная запись — тоже след того, что установка не пуста.
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
        // ⚠ Наборы модулей и проекта (решение EAMS 0114): существующая установка дотягивается прежними
        // миграциями хоста до моста к модулям, базовые модулей отмечаются, остальное применяется по
        // порядку. Отмеченное печатается отдельно: оператор обязан отличить «выполнено» от «отмечено,
        // схема уже была».
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

        // ⚠ Секреты прежнего конверта «sender:» перекладываются в конверт модулей — до роли сервиса и
        // до учётных записей из окружения: им может понадобиться второй фактор (разбор у LegacySecrets).
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

        // Ключ API Key Manager Plus, заданный в 1.x до привязки к адресу, привязывается к текущему адресу —
        // без этого опрос отказывал бы до повторного ввода ключа (так было в 1.x, шаг потерялся в 2.0).
        if (await KeyManagerModule.BindLegacyApiKeyAsync(app.Services))
        {
            Console.WriteLine($"{Bootstrap.Migrate}: ключ API Key Manager Plus привязан к текущему адресу");
        }

        // ⚠ Роль сервиса приводится в порядок и без новых миграций: сменили файл пароля или
        // ставите поверх старой установки — `migrate` и есть способ это применить.
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
        // ⚠ Не открывшееся прежней версией ключа не переложено: выбросить её из файла значит потерять
        // эти значения навсегда.
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
