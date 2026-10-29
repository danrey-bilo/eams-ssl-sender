using System.Net;
using Modules.Core.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Токен приглашения не принимается в адресе: адрес оседает в логах прокси и истории браузера.</summary>
internal sealed class TokenInAddressGuard(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && context.Request.Path.Equals("/api/activation", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                ErrorCodes.MethodNotAllowed,
                "Ссылка приглашения передаётся телом запроса: POST /api/activation/info.",
                HttpStatusCode.MethodNotAllowed);
        }

        return next(context);
    }
}
