namespace EmailSslSender.Modules.Identity.TwoFactor;

/// <summary>
/// Чем подтверждают вход.
/// </summary>
/// <remarks>
/// Строками, а не перечислением: значение уходит в API (<c>effective_method</c>,
/// <c>available_methods</c>, <c>method</c> расписки входа) строкой как есть.
/// </remarks>
internal static class SecondFactorMethods
{
    /// <summary>Одноразовый код письмом.</summary>
    public const string Email = "email";

    /// <summary>Код из приложения-аутентификатора.</summary>
    public const string Totp = "totp";

    /// <summary>Фактора нет.</summary>
    public const string None = "none";
}
