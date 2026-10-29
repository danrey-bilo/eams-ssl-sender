using EmailSslSender.Modules.KeyManager.Domain;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Где подписка относительно своего периода «с — по».</summary>
internal enum SchedulePeriod
{
    /// <summary>Первый день ещё не наступил.</summary>
    Before,

    /// <summary>Внутри периода.</summary>
    Active,

    /// <summary>Последний день прошёл.</summary>
    After,
}

/// <summary>Период «с — по» и время отправки: когда опрос вправе слать подписку (ручная шлёт всегда).</summary>
/// <remarks>
/// ⚠ Время — «в это время», а не «не раньше»: каждое разбирается один раз
/// (<see cref="CertificateSubscription.LastSlotAt"/>), пропущенное догоняется. Неизвестный пояс — UTC.
/// </remarks>
internal static class SendSchedule
{
    /// <summary>Часовой пояс по имени IANA; неизвестный или пустой — <c>null</c>.</summary>
    public static TimeZoneInfo? Zone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var пояс) ? пояс : null;

    /// <summary>Где подписка относительно периода. <c>null</c> — периода нет.</summary>
    public static SchedulePeriod? Period(CertificateSubscription subscription, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.ActiveFrom is null && subscription.ActiveUntil is null)
        {
            return null;
        }

        var сегодня = День(now, Пояс(subscription));

        return subscription.ActiveFrom > сегодня ? SchedulePeriod.Before
            : subscription.ActiveUntil < сегодня ? SchedulePeriod.After
            : SchedulePeriod.Active;
    }

    /// <summary>Можно ли опросу слать подписку сейчас и какое время отправки он разбирает.</summary>
    /// <returns><c>Slot</c> — разбираемое время отправки, если задано: после прохода отметить разобранным.</returns>
    public static (bool Allowed, DateTimeOffset? Slot) Check(CertificateSubscription subscription, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.ActiveFrom is null && subscription.ActiveUntil is null && subscription.SendTime is null)
        {
            return (true, null);
        }

        var пояс = Пояс(subscription);
        var сегодня = День(now, пояс);

        if (subscription.ActiveUntil < сегодня)
        {
            return (false, null);
        }

        if (subscription.SendTime is not { } время)
        {
            return (!(subscription.ActiveFrom > сегодня), null);
        }

        // Последнее наступившее время отправки: сегодняшнее, если оно уже было, иначе вчерашнее.
        var местное = TimeZoneInfo.ConvertTime(now, пояс);
        var день = TimeOnly.FromTimeSpan(местное.TimeOfDay) >= время ? сегодня : сегодня.AddDays(-1);

        if (subscription.ActiveFrom > день)
        {
            return (false, null);
        }

        var слот = Момент(день, время, пояс);

        return subscription.LastSlotAt >= слот ? (false, null) : (true, слот);
    }

    /// <summary>Дни календаря подписки между мгновениями: «раз в N дней» со временем — днями, не часами.</summary>
    public static int DaysBetween(CertificateSubscription subscription, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var пояс = Пояс(subscription);

        return День(to, пояс).DayNumber - День(from, пояс).DayNumber;
    }

    private static TimeZoneInfo Пояс(CertificateSubscription subscription) =>
        Zone(subscription.TimeZone) ?? TimeZoneInfo.Utc;

    private static DateOnly День(DateTimeOffset мгновение, TimeZoneInfo пояс) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(мгновение, пояс).DateTime);

    /// <summary>Мгновение «день, время» в поясе; пропущенное переводом часов — первая минута после.</summary>
    private static DateTimeOffset Момент(DateOnly день, TimeOnly время, TimeZoneInfo пояс)
    {
        var местное = день.ToDateTime(время, DateTimeKind.Unspecified);

        for (var минут = 0; минут < 180 && пояс.IsInvalidTime(местное); минут++)
        {
            местное = местное.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(местное, пояс), TimeSpan.Zero);
    }
}
