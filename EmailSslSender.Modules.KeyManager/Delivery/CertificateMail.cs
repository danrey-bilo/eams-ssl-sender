using System.Globalization;
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
/// ⚠ <b>Письмо короткое нарочно</b> (просьба владельца): имя, срок, причина, файлы. Просьба не
/// пересылать — только когда во вложении действительно есть закрытый ключ или его пароль
/// (<see cref="Warning"/>); безусловное предупреждение читать перестают.
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
            + "Действует до: {expires_on}\n"
            + "Причина: {reason}\n"
            + "Файлы: {files}\n"
            + "{warning}"),
        new(
            Kind,
            MailLocales.Uk,
            "Сертифікат {common_name}",
            "Сертифікат: {common_name}\n"
            + "Дійсний до: {expires_on}\n"
            + "Причина: {reason}\n"
            + "Файли: {files}\n"
            + "{warning}"),
        new(
            Kind,
            MailLocales.En,
            "Certificate {common_name}",
            "Certificate: {common_name}\n"
            + "Valid until: {expires_on}\n"
            + "Reason: {reason}\n"
            + "Files: {files}\n"
            + "{warning}"),
    ];

    public IReadOnlyCollection<string> UnthrottledKinds => [Kind];

    /// <summary>Причина словами на языке письма.</summary>
    public static string Reason(DeliveryReason reason, string locale) => (locale, reason) switch
    {
        (MailLocales.Ru, DeliveryReason.Initial) => "новая подписка",
        (MailLocales.Ru, DeliveryReason.Changed) => "сертификат обновлён",
        (MailLocales.Ru, DeliveryReason.Scheduled) => "по расписанию",
        (MailLocales.Ru, DeliveryReason.Manual) => "вручную",
        (MailLocales.Uk, DeliveryReason.Initial) => "нова підписка",
        (MailLocales.Uk, DeliveryReason.Changed) => "сертифікат оновлено",
        (MailLocales.Uk, DeliveryReason.Scheduled) => "за розкладом",
        (MailLocales.Uk, DeliveryReason.Manual) => "вручну",
        (_, DeliveryReason.Initial) => "new subscription",
        (_, DeliveryReason.Changed) => "certificate renewed",
        (_, DeliveryReason.Scheduled) => "scheduled",
        _ => "sent manually",
    };

    /// <summary>Срок действия в привычном языку виде; неизвестный — прочерк.</summary>
    public static string ExpiresOn(DateOnly? date, string locale) => date switch
    {
        null => "—",
        { } дата when locale is MailLocales.Ru or MailLocales.Uk =>
            дата.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        { } дата => дата.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>Имя вложения с паролем закрытого ключа.</summary>
    /// <remarks>
    /// ⚠ Пароль — вложением, не в теле: тело живёт в очереди до уборки, а вложения стираются
    /// сразу после отправки.
    /// </remarks>
    public const string PassphraseFile = "passphrase.txt";

    /// <summary>Просьба не пересылать письмо — или пусто, если секретов во вложении нет.</summary>
    public static string Warning(bool secret, string locale) => !secret ? string.Empty : locale switch
    {
        MailLocales.Ru => "\nНе пересылайте письмо: в нём закрытый ключ.\n",
        MailLocales.Uk => "\nНе пересилайте лист: у ньому закритий ключ.\n",
        _ => "\nDo not forward this letter: it contains a private key.\n",
    };
}
