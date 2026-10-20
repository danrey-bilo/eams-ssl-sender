using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Готова ли база принимать запросы.
/// </summary>
/// <remarks>
/// <b>Текст исключения не логируется намеренно.</b> Строка подключения Npgsql содержит
/// пароль целиком, и при сбое соединения он оказывается в тексте ошибки. Типа исключения
/// достаточно, чтобы понять, что именно недоступно.
/// </remarks>
public sealed class DatabaseReadiness(SenderDbContext db, ILogger<DatabaseReadiness> log) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // `SELECT 1`, а не `CanConnectAsync`: второй считает успехом открытое
            // подключение, а нам нужно, чтобы база отвечала на запросы.
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error)
        {
            log.LogWarning("database_not_ready {ErrorType}", SafeError.Describe(error));
            return HealthCheckResult.Unhealthy("База недоступна");
        }
    }
}
