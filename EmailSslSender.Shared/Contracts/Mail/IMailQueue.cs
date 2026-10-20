namespace EmailSslSender.Shared.Contracts.Mail;

/// <summary>Чем кончилась постановка письма в очередь.</summary>
/// <remarks>
/// ⚠ ⚠ <b>Перечисление, а не исключения, и это исправление найденного дефекта, а не вкус.</b>
/// В эталоне «почта не настроена» и «слишком часто» — два исключения, и <b>три места ловят их
/// по-разному</b>: два ловят оба, а отправка кода входа — <b>только</b> «слишком часто». То
/// есть ненастроенная почта прошла бы наверх исключением и уронила бы вход.
///
/// Дыры там сегодня нет лишь потому, что выбор способа второго фактора сам сверяется с
/// настроенностью почты <b>до</b> отправки. Конструкция держится на том, что каждый следующий
/// вызывающий не забудет <c>except</c>, — а он забудет.
/// </remarks>
public enum MailQueueResult
{
    /// <summary>Письмо в очереди. Уедет с фиксацией единицы работы вызывающего.</summary>
    Queued,

    /// <summary>Почта не настроена: слать некуда, и это не ошибка вызывающего.</summary>
    NotConfigured,

    /// <summary>Предел частоты на этот адрес и вид исчерпан.</summary>
    RateLimited,
}

/// <summary>
/// Поставить письмо в очередь.
/// </summary>
/// <remarks>
/// Устроен как <see cref="Audit.IAuditWriter"/> — и в одном месте нарочно наоборот.
///
/// ⚠ <b>Постановка СИНХРОННАЯ и БЕЗ сохранения.</b> Строка добавляется в текущую единицу работы
/// и уезжает в базу с <c>SaveChangesAsync</c> вызывающего. Отдельная фиксация была бы ошибкой:
/// письмо «пароль сменён» не должно существовать, если сама смена откатилась.
///
/// ⚠ <b>Метод НЕ БРОСАЕТ.</b> Ни «почта не настроена», ни «слишком часто» не исключения — см.
/// <see cref="MailQueueResult"/>. Тотальный метод убирает класс ошибки целиком.
///
/// ⚠ <b>Готовность спрашивается отдельно</b> — это единственная причина, по которой контракт не
/// состоит из одного метода. <c>Identity</c> нужен ответ <b>до</b> решения, каким быть второму
/// фактору: предложить код письмом при ненастроенной почте значит запереть человека снаружи.
///
/// ⚠ <b>Локаль приезжает СТРОКОЙ, и модуль почты её не выбирает.</b> Кому писать и на каком
/// языке — знает вызывающий; почта берёт готовое. Иначе ей понадобился бы доступ к чужим
/// предпочтениям, то есть право читать настройки любого человека.
/// </remarks>
public interface IMailQueue
{
    /// <summary>Настроена ли почта настолько, чтобы письмо имело шанс уйти.</summary>
    /// <remarks>
    /// ⚠ Отвечает на вопрос «есть ли смысл предлагать письмо», а не «дойдёт ли оно». Второе не
    /// знает никто до попытки.
    /// </remarks>
    Task<bool> CanSendAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Поставить письмо в очередь.
    /// </summary>
    /// <param name="toAddress">Кому. Один адрес: рассылка это N вызовов.</param>
    /// <param name="kind">Вид письма из справочника — он же ключ ограничения частоты.</param>
    /// <param name="locale">Язык получателя: <c>uk</c>, <c>ru</c> или <c>en</c>.</param>
    /// <param name="values">Что подставить в шаблон: <c>link</c>, <c>code</c>, <c>minutes</c>.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<MailQueueResult> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Поставить письмо с вложениями в очередь.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Вложения запечатаны в очереди так же, как тело</b>, и стираются из неё, как только
    /// письмо ушло или исчерпало попытки: в них бывает закрытый ключ сертификата, и лежать в базе
    /// до уборки ему незачем.
    /// </remarks>
    /// <param name="toAddress">Кому. Один адрес: рассылка это N вызовов.</param>
    /// <param name="kind">Вид письма из справочника.</param>
    /// <param name="locale">Язык получателя: <c>uk</c>, <c>ru</c> или <c>en</c>.</param>
    /// <param name="values">Что подставить в шаблон.</param>
    /// <param name="attachments">Файлы письма. Пустой список — обычное письмо.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<MailQueueResult> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<MailAttachment> attachments,
        CancellationToken cancellationToken = default);
}

/// <summary>Один файл письма.</summary>
/// <param name="FileName">Имя файла у получателя. Путь отбрасывается.</param>
/// <param name="ContentType">Тип содержимого; неразборчивый становится <c>application/octet-stream</c>.</param>
/// <param name="Content">Байты файла.</param>
public sealed record MailAttachment(string FileName, string ContentType, byte[] Content)
{
    /// <summary>Содержимое не печатается: во вложении бывает закрытый ключ.</summary>
    public override string ToString() => $"{FileName} ({ContentType}, {Content.Length} байт)";
}
