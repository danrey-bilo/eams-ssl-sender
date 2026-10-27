using System.Globalization;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Domain;
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

/// <summary>Письмо подписки: вид, значения подстановок и таблица сертификатов.</summary>
/// <param name="Kind">Вид письма — <c>mail_outbox.kind</c>.</param>
/// <param name="Values">Значения подстановок шаблона.</param>
/// <param name="Table">Строка на сертификат: имя, форматы, срок, сколько дней осталось.</param>
internal sealed record CertificateLetter(string Kind, IReadOnlyDictionary<string, string> Values, MailTable Table)
{
    /// <summary>Таблицы письма по именам заполнителей — для постановки в очередь.</summary>
    public IReadOnlyDictionary<string, MailTable> Tables =>
        new Dictionary<string, MailTable>(StringComparer.Ordinal) { [CertificateMail.TableName] = Table };
}

/// <summary>
/// Письмо с сертификатами: виды и шаблоны на трёх языках.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Виды сняты с предела <c>mail.rate_per_hour</c></b> (<see cref="UnthrottledKinds"/>). Предел
/// защищает чужой ящик от публичных форм; эту рассылку настраивает администратор со свежим вторым
/// фактором, и предел пять писем в час на адрес оставил бы сотрудника на шести подписках без
/// шестого сертификата.
///
/// ⚠ <b>Формат — решение владельца:</b> что это, таблица сертификатов (имя, форматы, срок, сколько дней
/// осталось — как в уведомлении Key Manager Plus, без серийного номера и отпечатка) и что лежит в архиве.
/// Приветствие и подпись с именем сервиса добавляет рамка модуля почты, причина отправки в письмо не
/// входит. Файлы — одним архивом ZIP (<see cref="CertificateArchive"/>).
///
/// ⚠ ⚠ <b>Таблица — строкой-заполнителем <c>{table:certificates}</c></b> (модуль почты, решение 0121): модуль
/// вырезает переводы строк из подставляемых значений, и список одним значением слипся бы в строку. Таблицу
/// модуль рисует сам — в HTML-части таблицей, в текстовой строкой на сертификат. Заполнитель обязан быть
/// во всех трёх языках: таблица без заполнителя — отказ постановки, а не письмо без таблицы.
///
/// Видов два (<see cref="KindFor"/>): письма с одним и с несколькими сертификатами отличаются только
/// словами — «сертификат» или «сертификаты», папки в архиве.
/// </remarks>
internal sealed class CertificateMail : IMailTemplateProvider
{
    /// <summary>Вид письма с одним сертификатом, он же <c>mail_outbox.kind</c>.</summary>
    public const string Kind = "certificate_delivery";

    /// <summary>Вид письма с несколькими сертификатами.</summary>
    public const string SeveralKind = "certificate_delivery_several";

    /// <summary>Имя таблицы сертификатов — заполнитель <c>{table:certificates}</c> в шаблонах.</summary>
    public const string TableName = "certificates";

    /// <summary>Имя файла с паролем закрытого ключа внутри архива.</summary>
    /// <remarks>
    /// ⚠ Пароль — файлом в архиве, не в теле: тело живёт в очереди до уборки, а вложение стирается
    /// сразу после отправки.
    /// </remarks>
    public const string PassphraseFile = "passphrase.txt";

    /// <summary>Сколько имён перечисляется в теме письма; остальные — «и ещё N».</summary>
    private const int ИмёнВТеме = 3;

    private const string Таблица = "{table:" + TableName + "}";

    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(Kind, MailLocales.Ru, "SSL-сертификат {names}",
            "Отправляем файлы SSL-сертификата.\n\n" + Таблица + "\n\nВо вложении — архив {archive}.\n"),
        new(SeveralKind, MailLocales.Ru, "SSL-сертификаты {names}",
            "Отправляем файлы SSL-сертификатов.\n\n" + Таблица
            + "\n\nВо вложении — архив {archive}: в нём папка на каждый сертификат.\n"),
        new(Kind, MailLocales.Uk, "SSL-сертифікат {names}",
            "Надсилаємо файли SSL-сертифіката.\n\n" + Таблица + "\n\nУ вкладенні — архів {archive}.\n"),
        new(SeveralKind, MailLocales.Uk, "SSL-сертифікати {names}",
            "Надсилаємо файли SSL-сертифікатів.\n\n" + Таблица
            + "\n\nУ вкладенні — архів {archive}: у ньому тека на кожен сертифікат.\n"),
        new(Kind, MailLocales.En, "SSL certificate {names}",
            "Please find the files of the SSL certificate attached.\n\n" + Таблица
            + "\n\nThe attached archive {archive} contains the files.\n"),
        new(SeveralKind, MailLocales.En, "SSL certificates {names}",
            "Please find the files of the SSL certificates attached.\n\n" + Таблица
            + "\n\nThe attached archive {archive} contains a folder per certificate.\n"),
    ];

    public IReadOnlyCollection<string> UnthrottledKinds { get; } = [Kind, SeveralKind];

    /// <summary>Вид письма с <paramref name="certificates"/> сертификатами.</summary>
    public static string KindFor(int certificates)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(certificates, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(certificates, CertificateSubscription.MaxCertificates);

        return certificates == 1 ? Kind : SeveralKind;
    }

    /// <summary>Заголовки колонок таблицы на языке письма.</summary>
    public static IReadOnlyList<string> Columns(string locale) => locale switch
    {
        MailLocales.Ru => ["Сертификат", "Форматы", "Действует до", "Осталось дней"],
        MailLocales.Uk => ["Сертифікат", "Формати", "Дійсний до", "Залишилось днів"],
        _ => ["Certificate", "Formats", "Valid until", "Days to expire"],
    };

    /// <summary>Собрать письмо подписки.</summary>
    /// <param name="certificates">Отправляемые сертификаты — в порядке подписки.</param>
    /// <param name="formats">Типы файлов подписки.</param>
    /// <param name="archive">Имя архива во вложении.</param>
    /// <param name="locale">Язык письма.</param>
    /// <param name="now">Сейчас — от него считаются оставшиеся дни.</param>
    public static CertificateLetter Letter(
        IReadOnlyList<CertificateFiles> certificates,
        IReadOnlyList<string> formats,
        string archive,
        string locale,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificates);

        var сегодня = DateOnly.FromDateTime(now.UtcDateTime);
        var форматы = Formats(formats);

        var таблица = new MailTable(
            Columns(locale),
            [
                .. certificates.Select(с => (IReadOnlyList<string>)
                [
                    с.Certificate.CommonName,
                    форматы,
                    ExpiresOn(с.Certificate.ExpiresOn, locale),
                    DaysLeft(с.Certificate.ExpiresOn, сегодня, locale),
                ]),
            ]);

        var значения = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["names"] = Names(certificates.Select(с => с.Certificate.CommonName).ToList(), locale),
            ["archive"] = archive,

            // ⚠ Серийные номера в шаблон не входят, но едут в значениях: по ним видно, какой выпуск ушёл.
            ["serial_numbers"] = string.Join(", ", certificates.Select(с => с.Certificate.SerialNumber)),
        };

        return new CertificateLetter(KindFor(certificates.Count), значения, таблица);
    }

    /// <summary>Срок действия в привычном языку виде; неизвестный — прочерк.</summary>
    public static string ExpiresOn(DateOnly? date, string locale) => date switch
    {
        null => "—",
        { } дата when locale is MailLocales.Ru or MailLocales.Uk =>
            дата.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        { } дата => дата.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>Сколько дней осталось до конца срока; истёкший — словом, неизвестный — прочерк.</summary>
    public static string DaysLeft(DateOnly? date, DateOnly today, string locale) => date switch
    {
        null => "—",
        { } дата when дата.DayNumber < today.DayNumber => locale switch
        {
            MailLocales.Ru => "истёк",
            MailLocales.Uk => "минув",
            _ => "expired",
        },
        { } дата => (дата.DayNumber - today.DayNumber).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Форматы так, как их видит человек: по расширению файла — <c>KEY, CRT, PFX</c>.</summary>
    public static string Formats(IEnumerable<string> formats) =>
        string.Join(", ", formats.Select(ф => CertificateFormats.Extension(ф).ToUpperInvariant()));

    /// <summary>Имена для темы: первые три и «и ещё N».</summary>
    public static string Names(IReadOnlyList<string> names, string locale)
    {
        ArgumentNullException.ThrowIfNull(names);

        var первые = string.Join(", ", names.Take(ИмёнВТеме));
        var ещё = names.Count - ИмёнВТеме;

        return ещё <= 0
            ? первые
            : первые + locale switch
            {
                MailLocales.Ru => $" и ещё {ещё}",
                MailLocales.Uk => $" та ще {ещё}",
                _ => $" and {ещё} more",
            };
    }
}
