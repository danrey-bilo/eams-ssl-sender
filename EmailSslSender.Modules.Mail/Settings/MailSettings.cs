using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Mail.Settings;

/// <summary>
/// Настройки почты и внешнего адреса установки.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Пароля SMTP здесь НЕТ, и это не пропуск.</b> <c>system_settings</c> хранит значения
/// открытым <c>jsonb</c>, а пароль почтового ящика — секрет. Он лежит запечатанным в
/// <c>system_secrets</c> и наружу отдаётся только признаком «задан». Граница проведена ровно
/// там же, где в эталоне, и по той же причине.
///
/// ⚠ <b>Все восемь помечены настройками защиты.</b> Почта — это канал доставки одноразовых
/// кодов и ссылок сброса: подменивший SMTP-сервер получает их все. Признак выделяет их на экране
/// настроек, и правит их только сессия со свежим вторым фактором (служба настроек).
///
/// ⚠ <b>Подписи по-английски</b> (решение 0027) и перенесены из эталона дословно — он их уже
/// писал по-английски, и русский с украинским живут в карте клиента.
/// </remarks>
internal static class MailSettings
{
    /// <summary>Раздел на экране настроек.</summary>
    public const string Category = "mail";

    /// <summary>Слать ли почту вообще.</summary>
    /// <remarks>
    /// ⚠ Выключено по умолчанию, и это правильное умолчание: пока администратор не заполнил
    /// сервер, слать некуда. Выключение <b>не стирает</b> настройки — иначе «попробовать
    /// выключить» стоило бы повторного заполнения формы.
    /// </remarks>
    public static readonly SettingKey<bool> Enabled = new(
        "mail.enabled",
        Default: false,
        "Send mail",
        "Off by default: until an administrator fills in the server, nothing is sent and "
        + "password reset stays unavailable. Turning it off does not delete the settings.",
        Category,
        IsSecurity: true);

    /// <summary>Сервер исходящей почты.</summary>
    public static readonly SettingKey<string?> SmtpHost = new(
        "mail.smtp_host",
        Default: null,
        "SMTP host",
        "Host name of the outgoing mail server.",
        Category,
        IsSecurity: true);

    /// <summary>Порт.</summary>
    public static readonly SettingKey<int> SmtpPort = new(
        "mail.smtp_port",
        Default: 587,
        "SMTP port",
        "587 for STARTTLS, 465 for implicit TLS, 25 for a relay on the same network.",
        Category,
        IsSecurity: true,
        Minimum: 1,
        Maximum: 65535);

    /// <summary>Как защищено соединение.</summary>
    /// <remarks>
    /// ⚠ Задаётся явно, а не угадывается по порту: 587 существует и без TLS, а молчаливое
    /// понижение защиты — худший вид совместимости.
    /// </remarks>
    public static readonly SettingKey<string> SmtpSecurity = new(
        "mail.smtp_security",
        Default: "starttls",
        "Connection security",
        "Set explicitly rather than guessed from the port: 587 exists without TLS too, and "
        + "silently downgrading protection is the worst kind of compatibility.",
        Category,
        IsSecurity: true,
        Choices: ["starttls", "tls", "none"]);

    /// <summary>Учётная запись SMTP.</summary>
    public static readonly SettingKey<string?> SmtpUsername = new(
        "mail.smtp_username",
        Default: null,
        "SMTP user",
        "Leave empty for a relay that does not ask for credentials.",
        Category,
        IsSecurity: true);

    /// <summary>Адрес отправителя в заголовке From.</summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Заведена вскрытием 44, и до неё адрес УГАДЫВАЛСЯ.</b> `SmtpSender` брал учётную
    /// запись SMTP, а не задан пароль — сочинял <c>eams@домен-получателя</c>. Первое неверно в
    /// самом обычном случае: у множества провайдеров пользователь SMTP — не адрес (живой
    /// пример владельца: <c>d.balochi</c>), и <c>From</c> выходил невалидным. Второе хуже:
    /// письмо «от» домена получателя — гарантированный отказ SPF и прямая дорога в спам.
    ///
    /// ⚠ <b>Пусто — законно, но означает «почта не настроена»</b> (см. <c>MailPolicy.Configured</c>).
    /// Отправлять с угаданного адреса система больше не будет ни при каких настройках: молчание
    /// с внятной причиной лучше письма, которое молча съест спам-фильтр.
    /// </remarks>
    public static readonly SettingKey<string?> FromAddress = new(
        "mail.from_address",
        Default: null,
        "From address",
        "Address in the From header. Not the same thing as the SMTP user: with most providers "
        + "the user name is not an address at all.",
        Category,
        IsSecurity: true);

    /// <summary>Имя отправителя в заголовке From.</summary>
    public static readonly SettingKey<string> FromName = new(
        "mail.from_name",
        Default: "Email SSL Sender",
        "From name",
        "Display name in the From header.",
        Category,
        IsSecurity: true);

    /// <summary>Сколько писем в час на один адрес и вид.</summary>
    /// <remarks>
    /// ⚠ Считается по адресу И виду. Защищает почтовый ящик того, чей адрес злоумышленник
    /// раз за разом скармливает форме сброса.
    /// </remarks>
    public static readonly SettingKey<int> RatePerHour = new(
        "mail.rate_per_hour",
        Default: 5,
        "Letters per hour per address",
        "Counted per address and per kind. Protects the mailbox of someone whose address "
        + "an attacker keeps feeding to the reset form.",
        Category,
        IsSecurity: true,
        Minimum: 1,
        Maximum: 100);

    /// <summary>Сколько раз пробовать доставить.</summary>
    public static readonly SettingKey<int> MaxAttempts = new(
        "mail.max_attempts",
        Default: 5,
        "Delivery attempts",
        "After this many failures the letter is given up on and its error is left for the "
        + "administrator to read.",
        Category,
        IsSecurity: true,
        Minimum: 1,
        Maximum: 50);

    /// <summary>
    /// Внешний адрес установки — из него собираются ссылки в письмах.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Обнуляемость задана ЯВНО, и это тот самый случай, ради которого параметр
    /// существует.</b> Умолчание непустое, а <c>null</c> законен — «адрес не задан, ссылки
    /// собирать не из чего». Вывод по умолчанию дал бы <c>nullable: false</c>, и кнопка
    /// «очистить» на экране не появилась бы (см. <c>SettingKey.IsNullable</c>).
    ///
    /// ⚠ <b>Раздел не <c>mail</c>, а <c>server</c>:</b> адрес нужен не только письмам. Класть
    /// его в почтовый раздел значило бы, что страница раздачи клиента ищет свой адрес в
    /// настройках почты.
    /// </remarks>
    public static readonly SettingKey<string> PublicUrl = new(
        "server.public_url",
        Default: "https://sender.example.com",
        "Public URL",
        "The address the installation is reachable at from outside. Links in letters are built "
        + "from it; empty means links cannot be built.",
        "server",
        IsSecurity: true,
        Nullable: true);
}

/// <summary>Объявление настроек почты для общего экрана настроек.</summary>
internal sealed class MailSettingDeclarations : ISettingDeclarationProvider
{
    public IReadOnlyList<ISettingDeclaration> Declarations { get; } =
    [
        MailSettings.Enabled,
        MailSettings.SmtpHost,
        MailSettings.SmtpPort,
        MailSettings.SmtpSecurity,
        MailSettings.SmtpUsername,
        MailSettings.FromName,
        MailSettings.FromAddress,
        MailSettings.RatePerHour,
        MailSettings.MaxAttempts,
        MailSettings.PublicUrl,
    ];
}
