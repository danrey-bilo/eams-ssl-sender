using System.Threading.RateLimiting;
using EmailSslSender.Modules.Identity.Users;

namespace EmailSslSender.Modules.Identity.TwoFactor;

/// <summary>
/// Сколько кодов можно предъявить по одной расписке второго фактора.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Неверный код расписку не гасит, но и бесконечно её не держит</b> (ревью 2026-10-20).
/// Гасить на первой же опечатке нельзя: человек, ошибившийся цифрой, начинал бы вход заново
/// (<c>TwoFactorTests.Опечатка_позволяет_повторить_код_в_той_же_расписке</c>). Не гасить вовсе —
/// значит, что расписка, а с ней и код письмом, привязанный к ней, перебираются до самой
/// блокировки учётной записи. Компромисс: <see cref="Limit"/> попыток на расписку, затем она
/// гасится в базе, и вход начинается с пароля.
///
/// ⚠ <b>Попытка ЗАНИМАЕТСЯ до проверки кода</b>, как и попытка учётной записи
/// (<see cref="LoginFailures.TakeAsync"/>): одновременные запросы по одной расписке получают не
/// больше <see cref="Limit"/> проверок на всех.
///
/// ⚠ <b>Счёт в памяти процесса, а гашение — в базе.</b> Экземпляр приложения один (решение
/// 0002), как и у предела входа (<see cref="SignInAttempts"/>); колонка ради счётчика, который
/// живёт минуты, стоила бы миграции. Перезапуск обнуляет счёт у ещё живых расписок — то есть
/// даёт перебирающему ещё <see cref="Limit"/> попыток на расписку, не больше: несущий предел
/// здесь — блокировка учётной записи, она в базе и перезапуска не замечает. Погашенная расписка
/// перезапуска не переживает тем более.
///
/// ⚠ Окно счёта — час, не меньше наибольшего срока расписки (<c>auth.two_factor_challenge_minutes</c>
/// ≤ 60): окно короче вернуло бы попытки расписке, которая ещё жива.
/// </remarks>
internal sealed class ChallengeAttempts : IDisposable
{
    /// <summary>Сколько кодов можно предъявить по одной расписке.</summary>
    public const int Limit = 5;

    /// <summary>Окно счёта — не короче наибольшего срока расписки.</summary>
    private static readonly TimeSpan Окно = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<string> _попытки = PartitionedRateLimiter.Create<string, string>(
        ключ => RateLimitPartition.GetFixedWindowLimiter(
            ключ,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Limit,
                Window = Окно,
                QueueLimit = 0,
            }));

    /// <summary>Занять попытку по расписке. <c>false</c> — попытки кончились.</summary>
    public bool Take(string challenge)
    {
        using var разрешение = _попытки.AttemptAcquire(Ключ(challenge));

        return разрешение.IsAcquired;
    }

    /// <summary>Остались ли у расписки попытки.</summary>
    public bool Exhausted(string challenge) =>
        _попытки.GetStatistics(Ключ(challenge))?.CurrentAvailablePermits is <= 0;

    /// <summary>Ключ — хеш расписки, а не она сама: открытый секрет незачем держать в словаре.</summary>
    private static string Ключ(string challenge) => Invitations.CodeBinding(challenge);

    public void Dispose() => _попытки.Dispose();
}
