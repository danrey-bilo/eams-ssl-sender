namespace EmailSslSender.Shared.Contracts.Tokens;

/// <summary>Почему сессия кончилась.</summary>
/// <remarks>
/// ⚠ <b>Живёт в контракте, а не внутри модуля, потому что это ОБЩИЙ СЛОВАРЬ.</b> Причину
/// называет тот, у кого что-то случилось с человеком (сменили пароль, отключили), а хранит
/// и показывает её модуль токенов. Строка, повторённая с двух сторон руками, разошлась бы
/// молча — и человек увидел бы на экране входа «сессия кончилась» вместо «пароль изменён».
/// </remarks>
public static class RevokeReasons
{
    /// <summary>Человек вышел сам.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>Отозвал он же из списка сессий или администратор.</summary>
    public const string Revoked = "revoked";

    /// <summary>
    /// Предъявлен уже потраченный токен обновления.
    /// </summary>
    /// <remarks>
    /// ⚠ Самая важная из причин. Означает, что токен есть у двоих: у владельца и у того,
    /// кто его унёс. Кто из них предъявил его вторым — неизвестно, поэтому сессия
    /// закрывается целиком.
    /// </remarks>
    public const string Stolen = "token_reuse";

    /// <summary>Учётная запись отключена.</summary>
    public const string UserInactive = "user_inactive";

    /// <summary>Сменили пароль — значит выгоняем всех, кто мог его знать.</summary>
    public const string PasswordChanged = "password_changed";

    /// <summary>Второй фактор сняли — телефон потерян или украден.</summary>
    public const string TwoFactorReset = "two_factor_reset";
}
