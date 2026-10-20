using System.Text;
using EmailSslSender.Modules.Mail.Secrets;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Errors;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace EmailSslSender.Modules.Mail.Sending;

/// <summary>Одно письмо, готовое к отправке.</summary>
internal readonly record struct OutgoingMail(
    string ToAddress, string Subject, string Body, IReadOnlyList<MailAttachment>? Attachments = null);

/// <summary>
/// Кто умеет открыть транспорт для отправки.
/// </summary>
/// <remarks>
/// ⚠ <b>Интерфейс заведён РАДИ ТЕСТОВ, и это законная причина.</b> Насос очереди проверяет
/// порядок разбора — захват порции, попытки, паузы, — и к SMTP это отношения не имеет. Настоящий
/// сервер в таком тесте означал бы сеть или контейнер почтовика ради проверки цикла, который
/// про SMTP ничего не знает; а «одно упавшее письмо не уносит пачку» иначе не проверить вовсе —
/// падение надо уметь устроить.
///
/// ⚠ ⚠ ⚠ <b>С этапа 60.2 транспорт открывается НА ПОРЦИЮ, а не на письмо.</b> Прежде каждое
/// письмо платило полный обряд: <c>Connect</c> + TLS + <c>Auth</c> + <c>Send</c> + <c>Quit</c>.
/// На порции из двадцати это двадцать рукопожатий вместо одного, половина времени порции — и
/// прямой повод для реле счесть нас перебирающим пароли.
/// </remarks>
internal interface IMailSender
{
    /// <summary>
    /// Открыть транспорт. Освобождение сессии закрывает соединение.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Ввода-вывода здесь НЕТ, и это несущее.</b> Соединение поднимается при первой
    /// отправке. Подключись мы тут — недоступный сервер уронил бы <b>всю</b> порцию одним
    /// броском мимо цикла: ни одному письму не досталось бы ни попытки в счётчике, ни строки
    /// с причиной, и очередь копилась бы вечно, ничего о себе не рассказывая.
    /// </remarks>
    IMailSession Open(MailPolicy policy);
}

/// <summary>
/// Открытый транспорт: сколько угодно писем, одно соединение.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Сессия чинит себя сама.</b> Оборвавшееся посреди порции соединение поднимается заново
/// при следующей отправке — иначе одна сорванная сеть уносила бы весь остаток пачки, а прежде
/// (по соединению на письмо) уносила ровно одно письмо. Свойство «одно упавшее письмо не уносит
/// пачку» дороже экономии на рукопожатиях, и терять его нельзя.
/// </remarks>
internal interface IMailSession : IAsyncDisposable
{
    /// <summary>Послать одно письмо. Бросает — значит попытка не удалась.</summary>
    Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default);
}

/// <summary>
/// Отправка писем по SMTP: одно соединение на порцию.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Режим защиты берётся из НАСТРОЙКИ, а не угадывается по порту.</b>
/// <c>SecureSocketOptions.Auto</c> удобен и ровно поэтому опасен: это «угадать по порту» плюс
/// молчаливое понижение защиты — худший вид совместимости. Порт 587 существует и без TLS, и
/// установка, где почта втихую пошла открытым текстом, узнает об этом последней.
///
/// ⚠ <b>Тело ТОЛЬКО текстовое.</b> HTML в письме — это ссылка, чей видимый текст не совпадает
/// с адресом; в письмах, которые сами по себе содержат ссылку сброса пароля, такого быть не
/// должно. Плюс текстовое письмо одинаково читается везде и не тянет за собой вопрос о
/// внешних картинках.
///
/// ⚠ <b>Заголовок <c>Auto-Submitted: auto-generated</c></b> — RFC 3834. Без него автоответчик
/// получателя отвечает нашему ящику, а наш ящик — это адрес, с которого уходят коды входа.
///
/// ⚠ <b>Пароль читается ПРИ ПОДКЛЮЧЕНИИ, а не держится в поле.</b> Открытый секрет, живущий в
/// объекте дольше надобности, — это секрет, переживший свою надобность (правило 14, §3
/// `SECURITY.md`). С этапа 60.2 «надобность» — это соединение, а не письмо: читается он раз на
/// порцию вместо двадцати раз, и живёт не дольше вызова.
/// </remarks>
internal sealed class SmtpSender(SystemSecrets secrets) : IMailSender
{
    public IMailSession Open(MailPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var хост = policy.Host;

        // ⚠ Проверка здесь, а не в сессии: `Open` — единственная дверь, и отказ на ней приходит
        // конвертом (правило 17), а не броском изнутри отправки посреди порции.
        if (string.IsNullOrWhiteSpace(хост))
        {
            throw new AppException(ErrorCodes.Validation, "Адрес почтового сервера не задан.");
        }

        return new SmtpSession(policy, ИмяСервера(хост), secrets);
    }

    /// <summary>
    /// «https://mail.example.com/» → «mail.example.com».
    /// </summary>
    /// <remarks>
    /// ⚠ Схему вписывают по привычке адреса из браузера, и смысл у неё один — это имя сервера.
    /// Порт и путь не срезаются: «host:465» с портом 587 в настройке — противоречие, которое
    /// решать администратору, а не нам; на них сессия отказывает с понятным текстом.
    /// </remarks>
    internal static string ИмяСервера(string хост)
    {
        var имя = хост.Trim();
        var схема = имя.IndexOf("://", StringComparison.Ordinal);

        return (схема >= 0 ? имя[(схема + 3)..] : имя).TrimEnd('/');
    }

    /// <summary>Кодировка тем и тел — одна на всё.</summary>
    static SmtpSender() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}

/// <summary>Одно соединение SMTP на порцию писем.</summary>
internal sealed class SmtpSession(MailPolicy policy, string host, SystemSecrets secrets)
    : IMailSession
{
    /// <summary>
    /// Сколько ждём сервер.
    /// </summary>
    /// <remarks>
    /// ⚠ Тридцать секунд, а не бесконечность: насос ходит раз в пятнадцать секунд, и повисшее
    /// соединение остановило бы разбор очереди целиком — то есть один недоступный сервер задержал
    /// бы все письма, включая те, что ушли бы через другое реле.
    /// </remarks>
    private const int ТаймаутМиллисекунд = 30_000;

    private readonly SmtpClient _клиент = new() { Timeout = ТаймаутМиллисекунд };

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        // ⚠ ⚠ Проверка перед КАЖДЫМ письмом, а не только перед первым: соединение рвётся и
        // посреди порции (реле закрыло по своему пределу, моргнула сеть). Без неё остаток пачки
        // уходил бы в отказ по одной причине — «соединения нет», — и свойство «одно упавшее
        // письмо не уносит пачку» кончилось бы вместе с первым обрывом.
        if (!_клиент.IsConnected)
        {
            await ПодключитьсяAsync(cancellationToken);
        }

        using var сообщение = Собрать(policy, mail);

        await _клиент.SendAsync(сообщение, cancellationToken);
    }

    /// <summary>Подключиться и войти; при любом отказе — разорвать.</summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Неудача ЛЮБОГО шага подключения закрывает соединение</b> (ревью 2026-09-15, mail-3).
    /// После отказа <c>AuthenticateAsync</c> MailKit соединения не рвёт, а проверка перед
    /// отправкой смотрит только на <c>IsConnected</c>: остальные письма порции шли по живому
    /// соединению БЕЗ входа, тратили попытку на «530 Authentication required», и в статусе почты
    /// администратор видел этот вторичный текст вместо настоящего отказа во входе.
    /// Закрытое соединение следующее письмо поднимет заново — со входом, — и свойство «сессия
    /// чинит себя сама» сохраняется.
    ///
    /// ⚠ Проверять <c>IsAuthenticated</c> вместо этого нельзя: реле без входа (пустое имя) —
    /// законная установка, и там признак ложен всегда.
    ///
    /// мутация 2026-09-15: убрал разрыв → покраснел
    /// SmtpSessionTests.Отказ_входа_не_оставляет_соединение_без_входа.
    /// </remarks>
    private async Task ПодключитьсяAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ПодключитьсяИВойтиAsync(cancellationToken);
        }
        catch
        {
            await РазорватьAsync(CancellationToken.None);

            throw;
        }
    }

    /// <summary>Разорвать соединение без <c>QUIT</c>; отказ при этом не важен.</summary>
    private async Task РазорватьAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_клиент.IsConnected)
            {
                await _клиент.DisconnectAsync(quit: false, cancellationToken);
            }
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
            // Соединения уже нет — и это ровно то, чего добивались.
        }
    }

    private async Task ПодключитьсяИВойтиAsync(CancellationToken cancellationToken)
    {
        // ⚠ Здесь, а не в `Open`: отказ в `Open` уронил бы порцию мимо цикла писем. «https://…» и
        // «host:465» MailKit искал бы в DNS целиком и отвечал бы невнятным «хост не найден».
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new InvalidOperationException(
                $"Сервер SMTP — только имя, без схемы и порта (smtp.example.com), а задано «{host}».");
        }

        await _клиент.ConnectAsync(host, policy.Port, Защита(policy.Security), cancellationToken);

        // ⚠ Реле в той же сети авторизации не требует, и требовать её значило бы запретить
        // законную установку. Пустое имя — законное состояние, а не недоделка.
        if (string.IsNullOrWhiteSpace(policy.Username))
        {
            return;
        }

        var пароль = await secrets.GetAsync(SystemSecretNames.SmtpPassword, cancellationToken);

        await _клиент.AuthenticateAsync(policy.Username, пароль ?? string.Empty, cancellationToken);
    }

    /// <summary>
    /// Закрыть соединение по-хорошему.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>QUIT</c> отправляется <b>без</b> токена отмены: останов процесса — не повод бросить
    /// сокет открытым, а реле — считать нас оборвавшимся. Само закрытие занимает миллисекунды.
    ///
    /// ⚠ Отказ на закрытии проглатывается: соединения уже нет, сообщать некому, а бросок из
    /// <c>DisposeAsync</c> подменил бы собой настоящую причину, из-за которой мы сюда попали.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_клиент.IsConnected)
            {
                await _клиент.DisconnectAsync(quit: true, CancellationToken.None);
            }
        }
        catch (Exception ошибка) when (ошибка is not OperationCanceledException)
        {
            // Соединение уже разорвано — прощаться не с кем.
        }
        finally
        {
            _клиент.Dispose();
        }
    }

    /// <summary>Собрать сообщение.</summary>
    private static MimeMessage Собрать(MailPolicy policy, OutgoingMail mail)
    {
        var сообщение = new MimeMessage();

        // ⚠ ⚠ Адрес отправителя ЗАДАН, а не угадан (вскрытие 44). Прежде здесь бралась
        // учётная запись SMTP, а без неё сочинялся `eams@домен-получателя` — и оба варианта
        // неверны: пользователь провайдера обычно не адрес вовсе (живой пример владельца —
        // `d.balochi`), а письмо «от» домена получателя это гарантированный отказ SPF.
        //
        // Пусто быть не может: `MailPolicy.Configured` без адреса возвращает false, и до
        // отправки дело не доходит. Проверка здесь — на случай, если кто-то позовёт сборку
        // мимо этого условия.
        var отправитель = policy.FromAddress
            ?? throw new InvalidOperationException(
                "Адрес отправителя не задан — MailPolicy.Configured обязан был это остановить");

        // ⚠ Отображаемое имя кодируется MimeKit само (RFC 2047) — вручную его собирать не надо,
        // и попытка сделать это руками ломает нелатинские имена.
        сообщение.From.Add(new MailboxAddress(policy.FromName, отправитель));
        сообщение.To.Add(MailboxAddress.Parse(mail.ToAddress));
        сообщение.Subject = mail.Subject;

        // RFC 3834: без этого автоответчик получателя отвечает нашему ящику.
        сообщение.Headers.Add("Auto-Submitted", "auto-generated");

        var текст = new TextPart(TextFormat.Plain)
        {
            Text = mail.Body,
            ContentTransferEncoding = ContentEncoding.Base64,
        };

        if (mail.Attachments is not { Count: > 0 } вложения)
        {
            сообщение.Body = текст;
            return сообщение;
        }

        var смесь = new Multipart("mixed") { текст };

        foreach (var файл in вложения)
        {
            смесь.Add(new MimePart(ContentType.Parse(MailAttachments.Тип(файл.ContentType)))
            {
                Content = new MimeContent(new MemoryStream(файл.Content, writable: false)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = MailAttachments.Имя(файл.FileName),
            });
        }

        сообщение.Body = смесь;
        return сообщение;
    }

    /// <summary>Собрать сообщение — для проверки состава письма без сервера.</summary>
    internal static MimeMessage СобратьДляПроверки(MailPolicy policy, OutgoingMail mail) => Собрать(policy, mail);

    /// <summary>
    /// Режим защиты соединения из настройки.
    /// </summary>
    /// <remarks>
    /// ⚠ <b><c>Auto</c> не возвращается никогда</b>, включая случай неизвестной строки:
    /// незнакомое значение означает ошибку в настройке, и молча понизить защиту — худшее, что
    /// можно на неё ответить. Берётся самый строгий из осмысленных.
    /// </remarks>
    private static SecureSocketOptions Защита(string security) => security switch
    {
        "tls" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };

}
