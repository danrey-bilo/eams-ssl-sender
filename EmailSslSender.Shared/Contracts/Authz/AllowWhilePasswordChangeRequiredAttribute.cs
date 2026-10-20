namespace EmailSslSender.Shared.Contracts.Authz;

/// <summary>
/// Эндпоинт открыт и тому, кому велено сменить пароль.
/// </summary>
/// <remarks>
/// Учётная запись, заведённая с паролем от администратора, ограничена до смены пароля: ей
/// открыто только помеченное этим атрибутом — сменить пароль, посмотреть себя, выйти. Иначе
/// временный пароль, переданный по телефону, работал бы как постоянный.
/// </remarks>
/// <param name="reason">Почему это нужно до смены пароля.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhilePasswordChangeRequiredAttribute(string reason) : Attribute
{
    /// <summary>Почему это нужно до смены пароля.</summary>
    public string Reason { get; } = reason;
}
