using System.Net;
using Modules.Core.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Ссылка приглашения не принимается в адресе запроса.
/// </summary>
/// <remarks>
/// ⚠ ⚠ Модуль учётных записей держит <c>GET /api/activation?token=…</c> ради совместимости с
/// клиентами EAMS. Адрес запроса оседает в журналах туннеля, прокси и в истории браузера, и
/// действующая ссылка — пропуск к заданию пароля — лежала бы там открытым текстом весь свой срок.
/// Панель проекта с первой версии шлёт токен телом (<c>POST /api/activation/info</c>), поэтому старый
/// путь закрыт здесь, до маршрутизации: так было и до перехода на модули.
/// </remarks>
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
