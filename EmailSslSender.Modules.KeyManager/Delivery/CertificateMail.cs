using EmailSslSender.Shared.Contracts.Mail;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Почему сертификат уходит письмом.</summary>
internal enum DeliveryReason
{
    /// <summary>Подписку завели — первая отправка.</summary>
    Initial,

    /// <summary>У Common Name появился новый серийный номер: сертификат продлён или перевыпущен.</summary>
    Changed,

    /// <summary>Подошёл срок повторной отправки.</summary>
    Scheduled,

    /// <summary>Администратор отправил вручную.</summary>
    Manual,
}

/// <summary>
/// Письмо с сертификатом: вид, шаблоны на трёх языках, подписи причин.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Вид снят с предела <c>mail.rate_per_hour</c></b> (<see cref="UnthrottledKinds"/>). Предел
/// защищает чужой ящик от публичных форм; эту рассылку настраивает вошедший администратор, и
/// предел пять писем в час на адрес оставил бы сотрудника на шести подписках без шестого
/// сертификата.
///
/// ⚠ <b>Тело только текстовое</b>, как у всех писем установки, и прямо просит не пересылать
/// письмо: во вложении бывает закрытый ключ.
/// </remarks>
internal sealed class CertificateMail : IMailTemplateProvider
{
    /// <summary>Вид письма. Он же <c>mail_outbox.kind</c>.</summary>
    public const string Kind = "certificate_delivery";

    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(
            Kind,
            MailLocales.Ru,
            "Сертификат {common_name}",
            "Сертификат: {common_name}\n"
            + "Серийный номер: {serial_number}\n"
            + "Действует до: {expires_on}\n"
            + "Причина отправки: {reason}\n\n"
            + "Файлы во вложении: {files}\n"
            + "{passphrase}\n"
            + "Письмо отправлено автоматически по подписке на сертификат в ManageEngine Key Manager Plus.\n"
            + "Во вложении может быть закрытый ключ: не пересылайте это письмо и удалите его, когда\n"
            + "установите сертификат."),
        new(
            Kind,
            MailLocales.Uk,
            "Сертифікат {common_name}",
            "Сертифікат: {common_name}\n"
            + "Серійний номер: {serial_number}\n"
            + "Дійсний до: {expires_on}\n"
            + "Причина надсилання: {reason}\n\n"
            + "Файли у вкладенні: {files}\n"
            + "{passphrase}\n"
            + "Лист надіслано автоматично за підпискою на сертифікат у ManageEngine Key Manager Plus.\n"
            + "У вкладенні може бути закритий ключ: не пересилайте цей лист і видаліть його, коли\n"
            + "встановите сертифікат."),
        new(
            Kind,
            MailLocales.En,
            "Certificate {common_name}",
            "Certificate: {common_name}\n"
            + "Serial number: {serial_number}\n"
            + "Valid until: {expires_on}\n"
            + "Reason: {reason}\n\n"
            + "Attached files: {files}\n"
            + "{passphrase}\n"
            + "This letter was sent automatically by a certificate subscription in ManageEngine Key\n"
            + "Manager Plus. The attachment may contain a private key: do not forward this letter and\n"
            + "delete it once the certificate is installed."),
    ];

    public IReadOnlyCollection<string> UnthrottledKinds => [Kind];

    /// <summary>Причина словами на языке письма.</summary>
    public static string Reason(DeliveryReason reason, string locale) => (locale, reason) switch
    {
        (MailLocales.Ru, DeliveryReason.Initial) => "подписка оформлена, первая отправка",
        (MailLocales.Ru, DeliveryReason.Changed) => "сертификат обновлён в Key Manager Plus",
        (MailLocales.Ru, DeliveryReason.Scheduled) => "плановая отправка по расписанию",
        (MailLocales.Ru, DeliveryReason.Manual) => "отправлено администратором вручную",
        (MailLocales.Uk, DeliveryReason.Initial) => "підписку оформлено, перше надсилання",
        (MailLocales.Uk, DeliveryReason.Changed) => "сертифікат оновлено в Key Manager Plus",
        (MailLocales.Uk, DeliveryReason.Scheduled) => "планове надсилання за розкладом",
        (MailLocales.Uk, DeliveryReason.Manual) => "надіслано адміністратором вручну",
        (_, DeliveryReason.Initial) => "subscription created, first delivery",
        (_, DeliveryReason.Changed) => "the certificate was renewed in Key Manager Plus",
        (_, DeliveryReason.Scheduled) => "scheduled delivery",
        _ => "sent manually by an administrator",
    };

    /// <summary>Имя вложения с паролем закрытого ключа.</summary>
    public const string PassphraseFile = "passphrase.txt";

    /// <summary>
    /// Строка о пароле ключа или пустая, если пароль не шлём.
    /// </summary>
    /// <remarks>
    /// ⚠ Самого пароля в теле нет: тело живёт в очереди до уборки, а вложения стираются сразу после
    /// отправки.
    /// </remarks>
    public static string Passphrase(bool attached, string locale) => !attached ? string.Empty : locale switch
    {
        MailLocales.Ru => $"Пароль закрытого ключа — во вложении {PassphraseFile}.\n",
        MailLocales.Uk => $"Пароль закритого ключа — у вкладенні {PassphraseFile}.\n",
        _ => $"The private key passphrase is in the attached {PassphraseFile}.\n",
    };

    /// <summary>Неизвестный срок действия.</summary>
    public const string UnknownDate = "—";
}
