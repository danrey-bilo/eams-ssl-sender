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

    /// <summary>Новый серийный номер у Common Name: продлён или перевыпущен.</summary>
    Changed,

    /// <summary>Подошёл срок повторной отправки.</summary>
    Scheduled,

    /// <summary>Администратор отправил вручную.</summary>
    Manual,
}

/// <summary>Письмо подписки: вид, значения подстановок и таблица сертификатов.</summary>
/// <param name="Kind">Вид письма — <c>mail_outbox.kind</c>.</param>
/// <param name="Values">Значения подстановок шаблона.</param>
/// <param name="Table">Строка на сертификат.</param>
internal sealed record CertificateLetter(string Kind, IReadOnlyDictionary<string, string> Values, MailTable Table)
{
    /// <summary>Таблицы письма по именам заполнителей — для постановки в очередь.</summary>
    public IReadOnlyDictionary<string, MailTable> Tables =>
        new Dictionary<string, MailTable>(StringComparer.Ordinal) { [CertificateMail.TableName] = Table };
}

/// <summary>Письмо с сертификатами: виды и шаблоны на трёх языках.</summary>
/// <remarks>
/// ⚠ Виды сняты с предела <c>mail.rate_per_hour</c>: рассылку настраивает администратор. Заполнитель
/// <c>{table:certificates}</c> обязан быть во всех языках, иначе постановка письма отказывает.
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

            // Не в шаблоне, но в значениях письма: видно, какой выпуск ушёл.
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
