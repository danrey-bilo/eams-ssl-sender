using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Единственное место, где исключение превращается в ответ.
/// </summary>
/// <remarks>
/// Стоит первым в конвейере, чтобы накрыть и то, что бросили следующие слои.
///
/// <b>Наружу уходит ровно две вещи:</b> известное <see cref="AppException"/> — со своим
/// кодом, статусом и подробностями; всё остальное — <see cref="ErrorCodes.Internal"/> с
/// номером обращения и без единой подробности. Текст непредвиденного исключения содержит
/// то, что знает код: имена таблиц, куски запросов, а у сбоя подключения — строку
/// подключения с паролем целиком. Отдать его клиенту нельзя ни при каких настройках.
///
/// ⚠ ⚠ <b>Конвертом отвечают и отказы самой платформы</b> (проработка API 2026-10-19, решение
/// 0095, правило 17). Прежде тело больше предела, обрыв тела и медленное тело
/// (<c>BadHttpRequestException</c>) уходили пятисоткой <c>INTERNAL_ERROR</c> со строкой
/// Error в логе, а неизвестный адрес, чужой метод и чужой тип содержимого — пустым телом:
/// клиент разбирал конверт и не находил его.
/// </remarks>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    // Кодировщик, не превращающий кириллицу в экранированные последовательности: умолчание
    // System.Text.Json экранирует всё вне ASCII, и человек получал бы сообщение об ошибке
    // в виде За… Здесь не HTML, а тело ответа JSON, поэтому разрешён весь
    // диапазон символов, а не отключена защита целиком.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);

            await ЗавернутьПустойОтказAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Клиент ушёл, не дождавшись. Это не ошибка сервера, и писать её как ошибку
            // значит засорять лог тем, что случается каждый день при закрытии вкладки.
            log.LogDebug("request_aborted {Path}", context.Request.Path);
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Тот же уход клиента, но посреди чтения тела: платформа сообщает его обрывом потока.
            log.LogDebug("request_aborted {Path}", context.Request.Path);
        }
        catch (BadHttpRequestException error)
        {
            // Отказ платформы, а не сбой сервера: тело больше предела, оборвано или идёт слишком
            // медленно. Уровень Warning — это не наша поломка; текст не пишется по той же
            // причине, что и ниже.
            log.LogWarning(
                "bad_request {Path} {Status} {Error}", context.Request.Path, error.StatusCode, SafeError.Describe(error));

            await WriteAsync(
                context,
                (HttpStatusCode)error.StatusCode,
                new ErrorResponse(
                    error.StatusCode == StatusCodes.Status413PayloadTooLarge ? ErrorCodes.PayloadTooLarge : ErrorCodes.Validation,
                    error.StatusCode == StatusCodes.Status413PayloadTooLarge
                        ? "Запрос больше разрешённого"
                        : "Запрос не удалось прочитать",
                    Details: null,
                    RequestId(context)),
                context.RequestAborted);
        }
        catch (AppException error)
        {
            await WriteAsync(
                context,
                error.Status,
                new ErrorResponse(error.Code, error.Message, error.Details, RequestId(context)),
                context.RequestAborted);
        }
        catch (Exception error)
        {
            // ⚠ ⚠ ⚠ ЦЕПОЧКА ТИПОВ И СТЕК — ДА, ТЕКСТ — НЕТ, и это последнее место в решении,
            // где так было не всегда. Прежняя редакция передавала `error` объектом с доводом
            // «логгер разложит сам, и ничего не уедет клиенту». Про клиента довод верен и
            // проверен (`ErrorEnvelope.Непредвиденное_исключение_не_выносит_наружу_ни_слова_из_себя`),
            // а про ЛОГ — нет: `RenderedCompactJsonFormatter` кладёт поле `@x` с текстом и стеком
            // всех вложенных исключений. Текст `NpgsqlException` несёт строку подключения — с
            // паролем, потому что пароль подставляется внутри приложения; `PostgresException.Detail`
            // при нарушении уникальности несёт значение колонки, то есть адрес почты.
            //
            // ⚠ Логи забирает docker, их пересылают в тикет. То есть путь «пароль рабочей роли
            // базы менеджера паролей → архив логов → переписка» был открыт и держался тем, что
            // непредвиденных исключений мало.
            //
            // ⚠ Стек остаётся ОТДЕЛЬНЫМ полем: без него последний рубеж перестаёт быть местом,
            // где вообще можно узнать, откуда прилетело. `StackTrace` — это адреса нашего кода,
            // секретов в нём нет; `Message` — единственное, что несёт чужие значения.
            //
            // Разбор — этап 59.3; до него это был единственный элемент списка позволенного в
            // `Eams.ArchTests/LogExceptions.cs`, и правило 14 честно называло его открытым
            // вопросом владельцу. Вопрос закрыт: стек и цепочка типов дают то же, ради чего
            // передавали объект.
            log.LogError(
                "unhandled_exception {Path} {Error} {Stack}",
                context.Request.Path,
                SafeError.Describe(error),
                error.StackTrace);

            await WriteAsync(
                context,
                HttpStatusCode.InternalServerError,
                new ErrorResponse(
                    ErrorCodes.Internal,
                    "Внутренняя ошибка. Сообщите администратору номер обращения.",
                    Details: null,
                    RequestId(context)),
                context.RequestAborted);
        }
    }

    /// <summary>
    /// Отказ платформы без тела — конвертом: неизвестный адрес, чужой метод, чужой тип.
    /// </summary>
    /// <remarks>
    /// Только под <c>/api</c>: сокет и проверки здоровья отвечают по своим правилам. Ответ,
    /// у которого уже есть тело или тип, не трогается — его написал тот, кто знал, что пишет.
    /// </remarks>
    private static async Task ЗавернутьПустойОтказAsync(HttpContext context)
    {
        var ответ = context.Response;

        if (ответ.HasStarted
            || ответ.StatusCode < 400
            || ответ.ContentLength is not null
            || ответ.ContentType is not null
            || !context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
        {
            return;
        }

        (string Code, string Message)? конверт = ответ.StatusCode switch
        {
            StatusCodes.Status404NotFound => (ErrorCodes.NotFound, "Адрес не найден"),
            StatusCodes.Status405MethodNotAllowed => (ErrorCodes.MethodNotAllowed, "Этот метод адрес не поддерживает"),
            StatusCodes.Status415UnsupportedMediaType => (ErrorCodes.Validation, "Тип содержимого не поддерживается"),
            StatusCodes.Status413PayloadTooLarge => (ErrorCodes.PayloadTooLarge, "Запрос больше разрешённого"),
            _ => null,
        };

        if (конверт is { } что)
        {
            await WriteAsync(
                context,
                (HttpStatusCode)ответ.StatusCode,
                new ErrorResponse(что.Code, что.Message, Details: null, RequestId(context)),
                context.RequestAborted);
        }
    }

    private static string RequestId(HttpContext context) =>
        context.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var value) && value is string id
            ? id
            : string.Empty;

    private static async Task WriteAsync(
        HttpContext context, HttpStatusCode status, ErrorResponse body, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            // Тело уже пошло к клиенту — заменить его нечем. Молча оборвать ответ честнее,
            // чем дописать конверт в середину недописанного JSON.
            return;
        }

        // Clear() сбрасывает и заголовки — вместе с номером обращения, который поставил
        // RequestIdMiddleware. Найдено тестом: в теле номер был, в заголовке пусто, и
        // сотрудник поддержки получил бы от человека номер, которого нет в ответе.
        context.Response.Clear();
        context.Response.Headers[RequestIdMiddleware.HeaderName] = body.RequestId;
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), cancellationToken);
    }
}
