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
/// Письмо с сертификатом: вид и шаблоны на трёх языках.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Вид снят с предела <c>mail.rate_per_hour</c></b> (<see cref="UnthrottledKinds"/>). Предел
/// защищает чужой ящик от публичных форм; эту рассылку настраивает администратор со свежим вторым
/// фактором, и предел пять писем в час на адрес оставил бы сотрудника на шести подписках без
/// шестого сертификата.
///
/// ⚠ <b>Три строки, и это решение владельца</b>: имя, срок, файлы. Ни причины отправки, ни
/// предупреждений — получателю нужны файлы, а не пояснения.
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
            + "Файлы: {files}\n"),
        new(
            Kind,
            MailLocales.Uk,
            "Сертифікат {common_name}",
            "Сертифікат: {common_name}\n"
            + "Дійсний до: {expires_on}\n"
            + "Файли: {files}\n"),
        new(
            Kind,
            MailLocales.En,
            "Certificate {common_name}",
            "Certificate: {common_name}\n"
            + "Valid until: {expires_on}\n"
            + "Files: {files}\n"),
    ];

    public IReadOnlyCollection<string> UnthrottledKinds => [Kind];

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
}
