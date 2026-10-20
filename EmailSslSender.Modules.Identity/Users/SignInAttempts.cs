using System.Net;
using System.Threading.RateLimiting;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>
/// Предел попыток входа на пару «адрес + почта».
/// </summary>
/// <remarks>
/// ⚠ ⚠ ⚠ <b>Решение владельца 0096 (2026-10-19).</b> Предел по одному адресу был пределом на весь
/// офис за NAT: утром люди получали 429. Хост поднял его до потолка против потока, а перебор
/// пароля одной учётной записи держит этот предел — и блокировка после неудач
/// (<c>auth.lockout_threshold</c>), которая работает с любого адреса.
///
/// ⚠ <b>Стоит ДО поиска учётной записи и до Argon2.</b> Отказ одинаков для существующего и
/// несуществующего адреса — оракула нет, — и отбитая попытка не стоит хеша.
///
/// ⚠ Счётчики в памяти процесса: экземпляр приложения один (решение 0002). Почта
/// нормализуется так же, как при поиске, — иначе «Admin@» и «admin@» были бы разными ключами.
/// </remarks>
internal sealed class SignInAttempts(IdentityModuleOptions options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _пределы = PartitionedRateLimiter.Create<string, string>(
        ключ => RateLimitPartition.GetFixedWindowLimiter(
            ключ,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, options.SignInAttemptsPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    /// <summary>Занять попытку или отказать 429 тем же конвертом, что у ограничителя хоста.</summary>
    public void Require(IPAddress? address, string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        using var разрешение = _пределы.AttemptAcquire(
            $"{address?.ToString() ?? "?"}|{email.Trim().ToLowerInvariant()}");

        if (!разрешение.IsAcquired)
        {
            throw new AppException(
                ErrorCodes.RateLimited, "Слишком много попыток. Подождите немного.", HttpStatusCode.TooManyRequests);
        }
    }

    public void Dispose() => _пределы.Dispose();
}
