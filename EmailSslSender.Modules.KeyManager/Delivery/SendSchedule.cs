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

/// <summary>
/// Период «с — по» и время отправки подписки: когда опрос вправе её слать.
/// </summary>
/// <remarks>
/// ⚠ ⚠ Решение владельца 2026-10-28: подписка может начинать и заканчивать рассылку в заданные дни и
/// слать в заданное время суток. Это держит только опрос — ручная отправка шлёт всегда.
///
/// ⚠ ⚠ <b>Время отправки — «в это время», а не «не раньше».</b> Опрос разбирает каждое время отправки один
/// раз (<see cref="CertificateSubscription.LastSlotAt"/>): шлёт, что пора, и до следующего дня молчит —
/// обновление, замеченное в 10:00 при времени 09:00, уходит завтра в 09:00. Пропущенное время (сервис
/// стоял, Key Manager Plus не отвечал) догоняется первым опросом после него.
///
/// ⚠ Дни и время — по часовому поясу подписки (IANA, из браузера администратора), с переходом на летнее
/// время. Пояс, которого нет в базе часовых поясов (её обновили), считается UTC: письмо уйдёт со сдвигом,
/// но уйдёт — сохранение подписки неизвестный пояс не пропускает.
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

    /// <summary>
    /// Можно ли опросу слать подписку сейчас и какое время отправки он при этом разбирает.
    /// </summary>
    /// <returns>
    /// <c>Allowed</c> — можно; <c>Slot</c> — разбираемое время отправки, если оно задано: его надо отметить
    /// разобранным, когда отправка состоялась или слать оказалось нечего.
    /// </returns>
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

    /// <summary>
    /// Сколько дней календаря подписки между двумя мгновениями — для «раз в N дней» со временем отправки.
    /// </summary>
    /// <remarks>
    /// ⚠ Со временем отправки «раз в N дней» считается днями, а не часами: отправка в 09:00:40 иначе не
    /// подошла бы через неделю в 09:00:10, и письмо ждало бы ещё сутки.
    /// </remarks>
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

    /// <summary>Мгновение «день, время» в поясе.</summary>
    /// <remarks>
    /// ⚠ Времени, которого в этот день нет (часы переведены вперёд), — первая минута после перевода.
    /// </remarks>
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
