using System.Threading.RateLimiting;
using EmailSslSender.Shared.Contracts.Authz;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Предел частоты на человека — для дорогих действий того, кто уже вошёл.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Решение владельца 0096.</b> Ограничитель по адресу стоит ДО опознания: ему личность не
/// нужна, а отбитая попытка не платит запросов опознания. Но за офисным NAT адрес у всех один, и
/// предел «на адрес» был пределом на весь офис. Здесь — после <c>CurrentUserMiddleware</c> — свой
/// предел у каждого человека; адресный остаётся потолком в несколько раз выше.
///
/// ⚠ Не вошёл — не режется здесь: дальше его ждёт отказ во входе, а поток без токена держит
/// потолок по адресу.
///
/// Счётчики в памяти процесса: экземпляр приложения один (решение 0002).
/// </remarks>
internal sealed class UserRateLimitMiddleware(RequestDelegate next, RateLimitGroups groups)
{
    private readonly PartitionedRateLimiter<(RateLimitGroups.Group Группа, Guid Кто)> _пределы =
        PartitionedRateLimiter.Create<(RateLimitGroups.Group Группа, Guid Кто), (RateLimitGroups.Group, Guid)>(
            ключ => RateLimitPartition.GetFixedWindowLimiter(
                ключ,
                свой => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = свой.Item1.PerUser ?? int.MaxValue,
                    Window = TimeSpan.FromMinutes(1),

                    // Очереди нет: ждать в очереди — это не «медленнее», это «висит».
                    QueueLimit = 0,
                }));

    public async Task InvokeAsync(HttpContext context, ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        if (groups.Of(context.Request.Path) is { PerUser: not null } группа && user.Id is { } кто)
        {
            using var разрешение = _пределы.AttemptAcquire((группа, кто));

            if (!разрешение.IsAcquired)
            {
                await RateLimitGroups.RejectAsync(
                    context,
                    разрешение.TryGetMetadata(MetadataName.RetryAfter, out var через) ? через : null,
                    context.RequestAborted);

                return;
            }
        }

        await next(context);
    }
}
