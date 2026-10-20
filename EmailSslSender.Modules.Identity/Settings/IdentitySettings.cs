using System.Net;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Identity.Settings;

/// <summary>Настройки модуля личностей.</summary>
/// <remarks>
/// Сроки сессий (токен доступа, простой, абсолютный срок, окно повторного обмена) объявляет
/// модуль токенов — <c>TokenSettings</c>: они про предъявляемый секрет, а не про учётную запись.
/// </remarks>
internal static class IdentitySettings
{
    /// <summary>Минимальная длина пароля.</summary>
    public static readonly SettingKey<int> PasswordMinLength = new("auth.password_min_length",
        Default: 12,
        "Minimum password length",
        "How many characters a password must contain at least.",
        "auth",
        IsSecurity: true,
        Minimum: 8, Maximum: 256);

    /// <summary>Требовать заглавные буквы.</summary>
    public static readonly SettingKey<bool> RequireUppercase = new("auth.password_require_uppercase",
        true,
        "Require an uppercase letter",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Требовать строчные буквы.</summary>
    public static readonly SettingKey<bool> RequireLowercase = new("auth.password_require_lowercase",
        true,
        "Require a lowercase letter",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Требовать цифру.</summary>
    public static readonly SettingKey<bool> RequireDigit = new("auth.password_require_digit",
        true,
        "Require a digit",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Требовать знак, не букву и не цифру.</summary>
    public static readonly SettingKey<bool> RequireSymbol = new("auth.password_require_symbol",
        true,
        "Require a special character",
        "",
        "auth",
        IsSecurity: true);

    /// <summary>Сколько неудачных входов подряд до блокировки.</summary>
    /// <remarks>
    /// Счётчик привязан к учётной записи, а не к адресу: OWASP прямо требует этого, иначе
    /// перебор с тысячи адресов не упирается ни во что.
    /// </remarks>
    public static readonly SettingKey<int> LockoutThreshold = new("auth.lockout_threshold",
        10,
        "Failed sign-ins before lockout",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 100);

    /// <summary>На сколько блокируется вход, минут.</summary>
    public static readonly SettingKey<int> LockoutMinutes = new("auth.lockout_minutes",
        15,
        "Lockout duration, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 10_080);

    /// <summary>
    /// Чем подтверждают вход по умолчанию.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Приложение-аутентификатор обязательно всем</b> (в EAMS — правило для
    /// суперпользователя, решение 0082; здесь каждая учётная запись — администратор). Пока его
    /// не подключили, вход по паролю выдаёт сессию, в которой открыта только настройка
    /// аутентификатора (<c>SignInService</c>, <c>SessionIdentityResolver</c>). Эта настройка
    /// решает, чем спрашивать код у того, кто аутентификатор уже подключал: письмом или
    /// приложением.
    ///
    /// ⚠ <b>Настройка задаёт умолчание, а не приказ.</b> У человека с заведённым
    /// аутентификатором фактор остаётся <c>totp</c>, что бы здесь ни стояло: понижать
    /// фактическую защиту настройкой установки нельзя.
    ///
    /// ⚠ ⚠ <b>«Выключить» среди вариантов НЕТ:</b> фактор обязателен всем, варьируется только
    /// способ. Переключатель <c>none</c> снимал бы второй фактор со всей установки одним
    /// нажатием.
    /// </remarks>
    public static readonly SettingKey<string> SecondFactor = new(
        "auth.second_factor",
        Default: "email",
        "Second factor",
        "How sign-in is confirmed by default. There is no off: the factor is required for "
        + "everyone, only the method varies. Someone who has set up an authenticator keeps using "
        + "it unless they pick e-mail themselves.",
        "auth",
        IsSecurity: true,
        Choices: ["totp", "email"]);

    /// <summary>
    /// Сколько живёт расписка между вводом пароля и вводом кода.
    /// </summary>
    /// <remarks>
    /// Пять минут: достаточно, чтобы достать телефон и найти приложение, и мало, чтобы
    /// расписка, подсмотренная в логах прокси, чего-то стоила.
    /// </remarks>
    public static readonly SettingKey<int> TwoFactorChallengeMinutes = new("auth.two_factor_challenge_minutes",
        5,
        "Two-factor challenge lifetime, minutes",
        "",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 60);

    /// <summary>
    /// Сколько держать погашенные ссылки и коды.
    /// </summary>
    /// <remarks>
    /// Не про безопасность, а про размер таблицы: доступ мёртвая ссылка не даёт ни секунды,
    /// а место занимает.
    /// </remarks>
    public static readonly SettingKey<int> LinkRetentionDays = new("identity.link_retention_days",
        30,
        "Keep spent links, days",
        "",
        "auth",
        Minimum: 1, Maximum: 3650);

    /// <summary>Сколько действует ссылка приглашения, минут.</summary>
    public static readonly SettingKey<int> InvitationTtlMinutes = new("auth.invitation_ttl_minutes",
        Default: 60,
        "Invitation lifetime, minutes",
        "After how many minutes the invitation link stops working.",
        "auth",
        IsSecurity: true,
        Minimum: 5, Maximum: 43_200);
}
