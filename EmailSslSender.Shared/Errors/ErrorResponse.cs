using System.Text.Json.Serialization;

namespace EmailSslSender.Shared.Errors;

/// <summary>
/// Единственная форма, в которой ошибка уходит наружу.
/// </summary>
/// <remarks>
/// Форма одна на всю систему, и это сквозное соглашение, а не стиль: клиент разбирает
/// ответ одним кодом для всех эндпоинтов. Стоит одному модулю ответить иначе — и разбор
/// придётся ветвить, а ветка, написанная под один эндпоинт, однажды встретит второй.
///
/// Имена полей в <c>snake_case</c>: контракт совпадает с Python-версией, чтобы уже
/// написанный веб-интерфейс переехал без правок.
/// </remarks>
/// <param name="Code">Машинный код из <see cref="ErrorCodes"/>.</param>
/// <param name="Message">Текст для человека.</param>
/// <param name="Details">Машинные подробности или <c>null</c>.</param>
/// <param name="RequestId">
/// Номер обращения. Присутствует <b>всегда</b> — по нему сотрудник поддержки находит
/// строку в логе. Половина текстов ошибок кончается словами «обратитесь к администратору»,
/// и без номера это предложение бессмысленно.
/// </param>
public sealed record ErrorResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, object?>? Details,
    [property: JsonPropertyName("request_id")] string RequestId);
