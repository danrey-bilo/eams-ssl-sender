using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Modules.Identity;
using EmailSslSender.Modules.Mail;
using EmailSslSender.Modules.Settings;
using EmailSslSender.Modules.Tokens;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
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
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (rateLimits.Of(context.Request.Path) is not { } group)
        {
            return RateLimitPartition.GetNoLimiter("прочее");
        }

        var address = context.Connection.RemoteIpAddress?.ToString() ?? "неизвестно";

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{group.Name}:{address}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = group.PerAddress,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    limiter.OnRejected = (context, token) => new ValueTask(RateLimitGroups.RejectAsync(
        context.HttpContext,
        context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null,
        token));
});

builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ErrorEnvelopeDocument>());

builder.Services.AddSenderDatabase(ConnectionSecrets.Postgres(builder.Configuration));

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadiness>("database", tags: ["ready"]);

// ⚠ За обратным прокси адрес клиента приходит заголовком, а заголовок подделывается. Доверяем
// ему только от названных прокси: иначе предел попыток входа обходился бы подстановкой
// X-Forwarded-For.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;

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

// Страницы, на которые ведут ссылки из писем: приглашение и сброс пароля. Токен едет во
// фрагменте адреса (`#token=…`) — он не уходит на сервер с запросом страницы и не оседает в
// логах прокси. Разбор — в самих страницах.
MapPage("/activate", "activate.html", "ссылка из приглашения: человек ещё не может войти");
MapPage("/password-reset", "password-reset.html", "ссылка из письма сброса: человек не вошёл");

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

void MapPage(string route, string file, string reason) =>
    app.MapGet(route, () => Results.File(
            Path.Combine(app.Environment.ContentRootPath, "wwwroot", file), "text/html; charset=utf-8"))
        .WithMetadata(new PublicEndpointAttribute(reason));

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

        var password = Bootstrap.ReadPassword(Bootstrap.Argument(args, "--password-file"));

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

static async Task<int> MigrateCommandAsync(WebApplication app)
{
    var pending = await app.PendingMigrationsAsync();

    if (pending.Count == 0)
    {
        Console.WriteLine($"{Bootstrap.Migrate}: схема уже совпадает с кодом, накатывать нечего");
        return 0;
    }

    Console.WriteLine($"{Bootstrap.Migrate}: применяю {pending.Count}");

    foreach (var name in pending)
    {
        Console.WriteLine($"  {name}");
    }

    await app.MigrateAsync();

    Console.WriteLine($"{Bootstrap.Migrate}: готово");
    return 0;
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
