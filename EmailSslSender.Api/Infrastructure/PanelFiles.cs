using System.Text.RegularExpressions;
using Modules.Core.Contracts.Authz;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Веб-панель из <c>wwwroot</c>: файлы публичны, данные — только из API после входа.</summary>
/// <remarks>⚠ Закрытый набор, не каталог: оболочка на трёх адресах, файлы — из <c>assets</c> по маске имени.</remarks>
internal static partial class PanelFiles
{
    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; "
        + "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".woff2"] = "font/woff2",
    };

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*\.(js|css|svg|png|woff2)$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetName();

    public static void MapPanel(this WebApplication app)
    {
        var root = Path.Combine(app.Environment.ContentRootPath, "wwwroot");

        foreach (var (route, reason) in new[]
        {
            ("/", "оболочка панели: данных в ней нет, они приходят из API после входа"),

            // Токен ссылки — во фрагменте адреса: не уходит на сервер и в логи прокси.
            ("/activate", "ссылка из приглашения: человек ещё не может войти"),
            ("/password-reset", "ссылка из письма сброса: человек не вошёл"),
        })
        {
            app.MapGet(route, (HttpContext context) => Shell(context, root))
                .WithMetadata(new PublicEndpointAttribute(reason));
        }

        app.MapGet("/assets/{file}", (string file, HttpContext context) => Asset(context, root, file))
            .WithMetadata(new PublicEndpointAttribute("код и стили панели: данных в них нет"));
    }

    private static IResult Shell(HttpContext context, string root)
    {
        var index = Path.Combine(root, "index.html");

        if (!File.Exists(index))
        {
            return Results.Text(
                "Панель не собрана: npm --prefix EmailSslSender.Web ci && npm --prefix EmailSslSender.Web run build",
                "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status404NotFound);
        }

        Secure(context);
        context.Response.Headers.CacheControl = "no-cache";

        return Results.File(index, "text/html; charset=utf-8");
    }

    private static IResult Asset(HttpContext context, string root, string file)
    {
        if (!AssetName().IsMatch(file) || !Types.TryGetValue(Path.GetExtension(file), out var type))
        {
            return Results.NotFound();
        }

        var path = Path.Combine(root, "assets", file);

        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        Secure(context);

        // Имя файла несёт хэш содержимого (Vite).
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        return Results.File(path, type);
    }

    private static void Secure(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = Csp;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
    }
}
