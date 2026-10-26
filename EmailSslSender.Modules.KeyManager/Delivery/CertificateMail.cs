using System.Globalization;
using EmailSslSender.Modules.KeyManager.Client;
using Modules.Core.Contracts.Mail;

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
/// Письмо с сертификатом: виды и шаблоны на трёх языках.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Вид снят с предела <c>mail.rate_per_hour</c></b> (<see cref="UnthrottledKinds"/>). Предел
/// защищает чужой ящик от публичных форм; эту рассылку настраивает администратор со свежим вторым
/// фактором, и предел пять писем в час на адрес оставил бы сотрудника на шести подписках без
/// шестого сертификата.
///
/// ⚠ <b>Формат — решение владельца 2026-10-26:</b> что это, сертификат с форматами и сроком, что
/// лежит в архиве — списком, по файлу в строке. Приветствие и подпись с именем сервиса добавляет рамка
/// модуля почты, причина отправки в письмо не входит. Файлы — одним архивом ZIP
/// (<see cref="CertificateArchive"/>).
///
/// ⚠ ⚠ <b>Вид письма несёт число файлов</b> (<see cref="KindFor"/>): модуль почты вырезает переводы
/// строк из подставляемых значений (подложенный абзац в теле, заголовок в теме), и список одним значением
/// слипся бы в строку. Поэтому список живёт в шаблоне — строка на файл, — а шаблонов по одному на
/// число файлов: от одного до <see cref="MaxFiles"/>.
/// </remarks>
internal sealed class CertificateMail : IMailTemplateProvider
{
    /// <summary>Приставка вида письма. Полный вид — <see cref="KindFor"/>, он же <c>mail_outbox.kind</c>.</summary>
    public const string Kind = "certificate_delivery";

    /// <summary>Имя файла с паролем закрытого ключа внутри архива.</summary>
    /// <remarks>
    /// ⚠ Пароль — файлом в архиве, не в теле: тело живёт в очереди до уборки, а вложение стирается
    /// сразу после отправки.
    /// </remarks>
    public const string PassphraseFile = "passphrase.txt";

    /// <summary>Больше файлов в архиве не бывает: все форматы и файл с паролем ключа.</summary>
    public static int MaxFiles { get; } = CertificateFormats.All.Count + 1;

    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        .. Enumerable.Range(1, MaxFiles).SelectMany(n => new[]
        {
            new MailTemplate(
                KindFor(n),
                MailLocales.Ru,
                "SSL-сертификат {common_name}",
                "Отправляем файлы SSL-сертификата.\n\n"
                + "Сертификат: {common_name}\n"
                + "Форматы: {formats}\n"
                + "Действует до: {expires_on}\n\n"
                + "Во вложении — архив {archive}:\n"
                + Список(n)),
            new MailTemplate(
                KindFor(n),
                MailLocales.Uk,
                "SSL-сертифікат {common_name}",
                "Надсилаємо файли SSL-сертифіката.\n\n"
                + "Сертифікат: {common_name}\n"
                + "Формати: {formats}\n"
                + "Дійсний до: {expires_on}\n\n"
                + "У вкладенні — архів {archive}:\n"
                + Список(n)),
            new MailTemplate(
                KindFor(n),
                MailLocales.En,
                "SSL certificate {common_name}",
                "Please find the files of the SSL certificate attached.\n\n"
                + "Certificate: {common_name}\n"
                + "Formats: {formats}\n"
                + "Valid until: {expires_on}\n\n"
                + "The attached archive {archive} contains:\n"
                + Список(n)),
        }),
    ];

    public IReadOnlyCollection<string> UnthrottledKinds { get; } =
        [.. Enumerable.Range(1, MaxFiles).Select(KindFor)];

    /// <summary>Вид письма под архив из <paramref name="files"/> файлов.</summary>
    public static string KindFor(int files)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(files, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(files, MaxFiles);

        return string.Create(CultureInfo.InvariantCulture, $"{Kind}_{files}");
    }

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

    /// <summary>Содержимое архива значениями шаблона: <c>file1</c>, <c>file2</c>… — по файлу на строку списка.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Files(IReadOnlyList<MailAttachment> files) =>
        files.Select((файл, номер) => KeyValuePair.Create(Файл(номер + 1), файл.FileName));

    private static string Файл(int номер) => string.Create(CultureInfo.InvariantCulture, $"file{номер}");

    private static string Список(int n) =>
        string.Concat(Enumerable.Range(1, n).Select(номер => "  • {" + Файл(номер) + "}\n"));
}
