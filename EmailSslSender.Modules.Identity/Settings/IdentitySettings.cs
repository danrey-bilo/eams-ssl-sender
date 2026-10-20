using EmailSslSender.Shared.Contracts.Settings;

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
    /// Сколько минут после кода из приложения на этой сессии открыты опасные действия.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Решение владельца 2026-10-20</b>, заменившее «второй фактор — только при входе»:
    /// выгрузка сертификатов и ключей, ключ API Key Manager Plus, подписки, пароль SMTP,
    /// управление людьми и правка настроек защиты требуют кода, подтверждённого в ЭТОЙ сессии не
    /// раньше окна (<c>StepUpFreshness</c>, <c>[RequireRecentSecondFactor]</c>). Вход с кодом
    /// считается подтверждением.
    ///
    /// Десять минут: хватает на одну работу подряд — завести подписку, выгрузить ключ, поправить
    /// получателей, — и мало, чтобы вкладка, оставленная открытой на обед, раздавала ключи.
    /// Потолок — два часа: дольше окно перестаёт быть «свежим» и становится вторым входом.
    /// </remarks>
    public static readonly SettingKey<int> StepUpMinutes = new("auth.step_up_minutes",
        Default: 10,
        "Fresh second factor window, minutes",
        "How long a code from the authenticator app, confirmed in a session, keeps sensitive "
        + "actions open in that session: certificate and private key export, the Key Manager Plus "
        + "API key, subscriptions, the SMTP password, user management and security settings. "
        + "Signing in with a code counts as a confirmation; after the window the action asks for "
        + "a new code.",
        "auth",
        IsSecurity: true,
        Minimum: 1, Maximum: 120);

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
