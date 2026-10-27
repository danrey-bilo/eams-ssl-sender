using Modules.Core.Contracts.Audit;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Обернуть последнюю регистрацию службы: обёртка получает прежнюю реализацию и решает, что с ней делать.
/// </summary>
/// <remarks>
/// ⚠ Обёртывается ПОСЛЕДНЯЯ регистрация — та, что и разрешалась бы без обёртки. Время жизни прежнее:
/// обёртка писателя журнала живёт столько же, сколько сам писатель (областью запроса).
/// </remarks>
internal static class ServiceDecoration
{
    public static IServiceCollection Decorate<T>(this IServiceCollection services, Func<T, IServiceProvider, T> decorate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);

        var прежняя = services.LastOrDefault(служба => служба.ServiceType == typeof(T) && !служба.IsKeyedService)
            ?? throw new InvalidOperationException($"Обернуть нечего: {typeof(T).Name} не зарегистрирован.");

        services.Remove(прежняя);
        services.Add(ServiceDescriptor.Describe(
            typeof(T), sp => decorate((T)Создать(sp, прежняя), sp), прежняя.Lifetime));

        return services;
    }

    private static object Создать(IServiceProvider sp, ServiceDescriptor прежняя) =>
        прежняя.ImplementationInstance
        ?? прежняя.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, прежняя.ImplementationType!);
}

/// <summary>Событие — в журнал модуля audit и строкой «audit …» в лог.</summary>
internal sealed class LoggedAuditWriter(IAuditWriter journal, LogAuditWriter log) : IAuditWriter
{
    public void Write(AuditEntry entry)
    {
        log.Write(entry);
        journal.Write(entry);
    }
}

/// <summary>Событие безопасности — в журнал модуля audit и строкой в лог.</summary>
/// <remarks>Сначала лог: строка в логе остаётся, даже если запись в журнал своим подключением не удалась.</remarks>
internal sealed class LoggedSecurityEventWriter(ISecurityEventWriter journal, LogAuditWriter log) : ISecurityEventWriter
{
    public async Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        await log.RecordAsync(securityEvent, cancellationToken);
        await journal.RecordAsync(securityEvent, cancellationToken);
    }
}

/// <summary>Решение о праве — в журнал модуля audit (отказы и обходы суперпользователя) и отказ строкой в лог.</summary>
internal sealed class LoggedAuthorizationAuditor(IAuthorizationAuditor journal, LogAuditWriter log) : IAuthorizationAuditor
{
    public async Task RecordAsync(
        Guid? actorId,
        AuditDecision decision,
        string? resourceType,
        Guid? resourceId,
        CancellationToken cancellationToken = default)
    {
        await log.RecordAsync(actorId, decision, resourceType, resourceId, cancellationToken);
        await journal.RecordAsync(actorId, decision, resourceType, resourceId, cancellationToken);
    }
}
