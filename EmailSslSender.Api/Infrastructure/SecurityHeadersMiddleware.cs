namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Заголовки защиты на каждый ответ: API, панель, проверки здоровья.
/// </summary>
/// <remarks>
/// ⚠ <b>HSTS — только на ответ по HTTPS.</b> Схему за туннелем даёт <c>X-Forwarded-Proto</c>
/// (ему верим только от сети туннеля), а проверка здоровья внутри контейнера и локальный
/// <c>127.0.0.1:8001</c> идут по HTTP — им заголовок не нужен и браузер его там не примет.
///
/// ⚠ <b><c>no-store</c> на всё под <c>/api/</c>:</b> ответы несут сессии, настройки и списки
/// получателей; общий кэш прокси или браузера не должен их хранить. Панель ставит свой
/// <c>Cache-Control</c> сама (<see cref="PanelFiles"/>).
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>Год в секундах — срок HSTS.</summary>
    private const string Hsts = "max-age=31536000; includeSubDomains";

    /// <summary>Панели не нужны ни камера, ни геопозиция, ни платежи — запрещено всё.</summary>
    private const string Permissions =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), "
        + "payment=(), usb=(), interest-cohort=()";

    public Task InvokeAsync(HttpContext context)
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

        return next(context);
    }
}
