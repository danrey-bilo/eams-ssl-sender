namespace EmailSslSender.Shared.Contracts.Authz;

/// <summary>
/// Эндпоинт открыт и тому, кому велено сначала подключить второй фактор.
/// </summary>
/// <remarks>
/// Второе ограничение того же рода, что и смена пароля: до подключения фактора открыто только
/// помеченное — посмотреть себя, настроить приложение-аутентификатор, выйти. Иначе требование
/// «фактор обязателен» выполнялось бы на словах.
/// </remarks>
/// <param name="reason">Почему это нужно до подключения фактора.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhileTwoFactorEnrollmentRequiredAttribute(string reason) : Attribute
{
    /// <summary>Почему это нужно до подключения фактора.</summary>
    public string Reason { get; } = reason;
}
