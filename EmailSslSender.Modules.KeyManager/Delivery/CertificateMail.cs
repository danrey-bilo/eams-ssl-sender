using System.Globalization;
using EmailSslSender.Modules.KeyManager.Client;
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
/// ⚠ <b>Формат — решение владельца 2026-10-26:</b> что это, сертификат с форматами и сроком, что
/// лежит в архиве. Приветствие и подпись с именем сервиса добавляет рамка почты (<c>MailFrame</c>),
/// причина отправки в письмо не входит. Файлы — одним архивом ZIP (<see cref="CertificateArchive"/>).
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
            "SSL-сертификат {common_name}",
            "Отправляем файлы SSL-сертификата.\n\n"
            + "Сертификат: {common_name}\n"
            + "Форматы: {formats}\n"
            + "Действует до: {expires_on}\n\n"
            + "Во вложении — архив {archive}:\n"
            + "{files}\n"),
        new(
            Kind,
            MailLocales.Uk,
            "SSL-сертифікат {common_name}",
            "Надсилаємо файли SSL-сертифіката.\n\n"
            + "Сертифікат: {common_name}\n"
            + "Формати: {formats}\n"
            + "Дійсний до: {expires_on}\n\n"
            + "У вкладенні — архів {archive}:\n"
            + "{files}\n"),
        new(
            Kind,
            MailLocales.En,
            "SSL certificate {common_name}",
            "Please find the files of the SSL certificate attached.\n\n"
            + "Certificate: {common_name}\n"
            + "Formats: {formats}\n"
            + "Valid until: {expires_on}\n\n"
            + "The attached archive {archive} contains:\n"
            + "{files}\n"),
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

    /// <summary>Форматы так, как их видит человек: по расширению файла — <c>KEY, CRT, PFX</c>.</summary>
    public static string Formats(IEnumerable<string> formats) =>
        string.Join(", ", formats.Select(ф => CertificateFormats.Extension(ф).ToUpperInvariant()));

    /// <summary>Содержимое архива списком — по файлу в строке.</summary>
    public static string Files(IEnumerable<MailAttachment> files) =>
        string.Join("\n", files.Select(ф => "  • " + ф.FileName));

    /// <summary>Имя файла с паролем закрытого ключа внутри архива.</summary>
    /// <remarks>
    /// ⚠ Пароль — файлом в архиве, не в теле: тело живёт в очереди до уборки, а вложение стирается
    /// сразу после отправки.
    /// </remarks>
    public const string PassphraseFile = "passphrase.txt";
}
