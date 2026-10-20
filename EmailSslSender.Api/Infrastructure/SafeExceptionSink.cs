using EmailSslSender.Shared.Errors;
using Serilog.Core;
using Serilog.Events;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Приёмник логов, до которого исключение доезжает цепочкой типов и стеком, но не текстом.
/// </summary>
/// <remarks>
/// ⚠ ⚠ ⚠ <b>Правило 14 на последнем рубеже</b> (проработка API 2026-10-19, решение 0095). Свой код
/// исключений объектом не пишет — это держит <c>Eams.ArchTests/LogExceptions.cs</c>. Но сторож
/// видит только исходники решения, а EF Core, Npgsql и ASP.NET пишут свои ошибки сами и с
/// объектом: «ошибка при чтении результатов запроса», «сбой сохранения», «сбой соединения».
/// Форматтер кладёт текст и стек всех вложенных исключений в поле <c>@x</c>, а в тексте сбоя
/// базы бывают значения колонок и куски строки подключения. Свод при этом утверждал «текст
/// исключения не пишется больше нигде» — для чужих логгеров это было неправдой.
///
/// Здесь исключение снимается с события целиком, а вместо него приезжают два поля:
/// <c>ExceptionTypes</c> — та же цепочка, что даёт <see cref="SafeError.Describe"/>, и
/// <c>ExceptionStack</c> — стек верхнего исключения: адреса кода, секретов в нём нет.
/// </remarks>
public sealed class SafeExceptionSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    /// <summary>Поле с цепочкой типов исключения.</summary>
    public const string TypesProperty = "ExceptionTypes";

    /// <summary>Поле со стеком верхнего исключения.</summary>
    public const string StackProperty = "ExceptionStack";

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Exception is not { } error)
        {
            inner.Emit(logEvent);

            return;
        }

        var поля = logEvent.Properties
            .Where(пара => пара.Key is not (TypesProperty or StackProperty))
            .Select(пара => new LogEventProperty(пара.Key, пара.Value))
            .Append(new LogEventProperty(TypesProperty, new ScalarValue(SafeError.Describe(error))))
            .ToList();

        if (error.StackTrace is { } стек)
        {
            поля.Add(new LogEventProperty(StackProperty, new ScalarValue(стек)));
        }

        inner.Emit(new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception: null,
            logEvent.MessageTemplate,
            поля,
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default));
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
