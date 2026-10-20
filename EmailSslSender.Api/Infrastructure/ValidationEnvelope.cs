using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Ошибки разбора и валидации — тем же конвертом, что и всё остальное.
/// </summary>
/// <remarks>
/// ⚠ <b>Найдено живой проверкой 2026-08-22.</b> Атрибут <c>[ApiController]</c> сам отвечает
/// на непрошедший разбор — и отвечает <c>ProblemDetails</c> по RFC 9457: <c>type</c>,
/// <c>title</c>, <c>errors</c>, <c>traceId</c>. Это мимо нашего middleware, мимо
/// <c>request_id</c> и мимо машинных кодов. То есть клиент, написанный под наш конверт,
/// на кривом теле получал бы форму, которую не умеет разбирать, — и показал бы пустую
/// ошибку вместо внятной.
///
/// Сам middleware ошибок этого не ловит: до него дело не доходит, ответ формируется
/// внутри MVC ещё до входа в действие.
///
/// ⚠ <b>Тексты нарушений уезжают в <c>details</c>, но не в сообщение.</b> Сообщения
/// валидации содержат имена полей и присланные значения — а присылают туда в том числе
/// пароли. В общий текст ошибки, который попадёт в лог клиента, им нельзя.
/// </remarks>
internal static class ValidationEnvelope
{
    /// <summary>Подключить свой ответ на ошибки разбора.</summary>
    public static IMvcBuilder UseErrorEnvelope(this IMvcBuilder mvc)
    {
        mvc.ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var fields = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .ToDictionary(
                        entry => Name(entry.Key),
                        entry => (object?)entry.Value!.Errors
                            .Select(error => error.ErrorMessage)
                            .Where(message => message.Length > 0)
                            .ToList());

                var body = new ErrorResponse(
                    ErrorCodes.Validation,
                    "Запрос не прошёл проверку",
                    fields,
                    context.HttpContext.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var id)
                        ? id as string ?? string.Empty
                        : string.Empty);

                return new ObjectResult(body) { StatusCode = StatusCodes.Status400BadRequest };
            };
        });

        return mvc;
    }

    /// <summary>
    /// Имя поля в том виде, в каком его прислали.
    /// </summary>
    /// <remarks>
    /// MVC называет поля путями вида <c>$.password</c> или именами свойств C#. Клиент
    /// присылал <c>password</c> — им и отвечаем, иначе форма не знает, какое поле подсветить.
    /// </remarks>
    private static string Name(string key) =>
        key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
}
