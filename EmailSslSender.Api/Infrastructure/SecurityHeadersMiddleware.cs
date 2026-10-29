namespace EmailSslSender.Api.Infrastructure;

/// <summary>Заголовки защиты на каждый ответ; HSTS — только по HTTPS, <c>no-store</c> — под <c>/api/</c>.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Hsts = "max-age=31536000; includeSubDomains";

    private const string Permissions =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), "
        + "payment=(), usb=(), interest-cohort=()";

    public Task InvokeAsync(HttpContext context)
    {
        // ⚠ В момент отправки: обработчик ошибок зовёт Response.Clear(), а OnStarting его переживает.
        context.Response.OnStarting(() =>
        {
            Apply(context);
            return Task.CompletedTask;
        });

        return next(context);
    }

    private static void Apply(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = Permissions;
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        if (context.Request.IsHttps)
        {
            headers.StrictTransportSecurity = Hsts;
        }

        if (context.Request.Path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store";
        }
    }
}
