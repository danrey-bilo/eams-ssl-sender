using System.Net;

namespace EmailSslSender.Shared.Errors;

/// <summary>
/// Ошибка, которую можно показать наружу: с машинным кодом, статусом и подробностями.
/// </summary>
/// <remarks>
/// Голых <c>throw new Exception("bad")</c> в проекте не бывает. Такое исключение выходит
/// наружу как <see cref="ErrorCodes.Internal"/> без подробностей — то есть клиент не может
/// ни распознать беду, ни объяснить её человеку.
///
/// <b>Текст сообщения виден пользователю.</b> Значит в нём не должно быть ни имён таблиц,
/// ни строк подключения, ни того, что стало известно из внутренней ошибки. Всё это идёт в
/// лог рядом с идентификатором запроса.
/// </remarks>
public class AppException : Exception
{
    /// <param name="code">Машинный код из <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Текст для человека. Уезжает клиенту как есть.</param>
    /// <param name="status">Код ответа HTTP.</param>
    /// <param name="details">
    /// Машинные подробности: чего не хватило, какой лимит превышен. Секретов здесь не бывает —
    /// поле уезжает клиенту целиком.
    /// </param>
    public AppException(
        string code,
        string message,
        HttpStatusCode status = HttpStatusCode.BadRequest,
        IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        Status = status;
        Details = details;
    }

    /// <summary>Машинный код — по нему клиент решает, что делать.</summary>
    public string Code { get; }

    /// <summary>Код ответа HTTP.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Машинные подробности или <c>null</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; }

    // --- готовые случаи ------------------------------------------------------------
    // Не «для удобства»: одинаковая беда должна выходить наружу одинаково, а свободное
    // создание исключения на каждом вызове однажды даст три разных статуса на один смысл.

    /// <summary>Не найдено — или найдено, но показывать нельзя.</summary>
    public static AppException NotFound(string message = "Не найдено") =>
        new(ErrorCodes.NotFound, message, HttpStatusCode.NotFound);

    /// <summary>Испорченный курсор пагинации.</summary>
    public static AppException InvalidCursor() =>
        new(ErrorCodes.InvalidCursor, "Недействительный курсор", HttpStatusCode.UnprocessableContent);
}
