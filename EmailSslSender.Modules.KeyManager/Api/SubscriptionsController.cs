using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Mail;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Api;

/// <summary>Новая подписка или правка: не присланное поле не меняется.</summary>
internal sealed record SubscriptionIn
{
    /// <summary>Common Name сертификатов: от одного до 50.</summary>
    [JsonPropertyName("common_names")]
    public IReadOnlyList<string>? CommonNames { get; init; }

    /// <summary>Один сертификат — по-прежнему можно и так; вместе с <c>common_names</c> нельзя.</summary>
    [JsonPropertyName("common_name")]
    public string? CommonName { get; init; }

    [JsonPropertyName("recipients")]
    public IReadOnlyList<string>? Recipients { get; init; }

    /// <summary>Типы файлов; не прислано — <c>PEM</c> и <c>KEY</c>.</summary>
    [JsonPropertyName("formats")]
    public IReadOnlyList<string>? Formats { get; init; }

    [JsonPropertyName("include_passphrase")]
    public bool? IncludePassphrase { get; init; }

    [JsonPropertyName("on_change")]
    public bool? OnChange { get; init; }

    /// <summary>Раз в сколько дней слать повторно; <c>null</c> или <c>0</c> — без расписания.</summary>
    [JsonPropertyName("interval_days")]
    public int? IntervalDays { get; init; }

    [JsonPropertyName("locale")]
    public string? Locale { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>
    /// Описание для администраторов, до 500 знаков; пустая строка снимает. Только в системе — в письма не уходит.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// С какого дня (<c>ГГГГ-ММ-ДД</c>, включительно) опрос шлёт подписку сам; пустая строка снимает.
    /// Ручная отправка шлёт всегда.
    /// </summary>
    [JsonPropertyName("active_from")]
    public string? ActiveFrom { get; init; }

    /// <summary>По какой день (<c>ГГГГ-ММ-ДД</c>, включительно) опрос шлёт подписку сам; пустая строка снимает.</summary>
    [JsonPropertyName("active_until")]
    public string? ActiveUntil { get; init; }

    /// <summary>
    /// В какое время суток (<c>ЧЧ:ММ</c>) опрос шлёт подписку; пустая строка снимает — тогда сразу, как
    /// заметит, что пора. Замеченное после этого времени ждёт следующего дня.
    /// </summary>
    [JsonPropertyName("send_time")]
    public string? SendTime { get; init; }

    /// <summary>
    /// Часовой пояс IANA (<c>Europe/Kyiv</c>) дат и времени отправки; нужен, если задано хоть одно из них.
    /// </summary>
    [JsonPropertyName("time_zone")]
    public string? TimeZone { get; init; }
}

/// <summary>Подписка с состоянием последней отправки.</summary>
internal sealed record SubscriptionOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("common_names")] IReadOnlyList<string> CommonNames,
    [property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients,
    [property: JsonPropertyName("formats")] IReadOnlyList<string> Formats,
    [property: JsonPropertyName("include_passphrase")] bool IncludePassphrase,
    [property: JsonPropertyName("on_change")] bool OnChange,
    [property: JsonPropertyName("interval_days")] int? IntervalDays,
    [property: JsonPropertyName("active_from")] DateOnly? ActiveFrom,
    [property: JsonPropertyName("active_until")] DateOnly? ActiveUntil,
    [property: JsonPropertyName("send_time")] string? SendTime,
    [property: JsonPropertyName("time_zone")] string? TimeZone,
    [property: JsonPropertyName("period")] string? Period,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("sent_serials")] IReadOnlyDictionary<string, string> SentSerials,
    [property: JsonPropertyName("last_sent_at")] DateTimeOffset? LastSentAt,
    [property: JsonPropertyName("last_full_sent_at")] DateTimeOffset? LastFullSentAt,
    [property: JsonPropertyName("last_attempt_at")] DateTimeOffset? LastAttemptAt,
    [property: JsonPropertyName("last_error")] string? LastError,
    [property: JsonPropertyName("delivery")] string Delivery)
{
    /// <summary>
    /// Подписка с состоянием доставки последней отправки.
    /// </summary>
    /// <remarks>
    /// <c>delivery</c>: <c>none</c> — ещё не отправлялась, <c>queued</c> — письма в очереди,
    /// <c>sent</c> — ушли, <c>failed</c> — не ушли (причина в <c>last_error</c>). Состояние писем
    /// берётся из очереди на чтении — опрос сверяет его позже и не чаще своего срока.
    ///
    /// <c>period</c>: <c>before</c> — первый день периода ещё не наступил, <c>active</c> — внутри,
    /// <c>after</c> — последний день прошёл; <c>null</c> — периода нет.
    /// </remarks>
    public static SubscriptionOut From(
        CertificateSubscription п, DateTimeOffset now, IReadOnlyDictionary<Guid, MailLetterStatus>? letters = null)
    {
        var (доставка, ошибка) = (п.LastError, п.LastSentAt) switch
        {
            _ when п.PendingMailIds is { Length: > 0 } номера && letters is not null => Состояние(номера, letters, п.LastError),
            (not null, _) => ("failed", п.LastError),
            (null, not null) => ("sent", null),
            _ => ("none", null),
        };

        return new(
            п.Id, п.Description, п.CommonNames, п.Recipients, п.Formats, п.IncludePassphrase, п.OnChange, п.IntervalDays,
            п.ActiveFrom, п.ActiveUntil, п.SendTime?.ToString("HH:mm", CultureInfo.InvariantCulture), п.TimeZone,
            SendSchedule.Period(п, now)?.ToString().ToLowerInvariant(),
            п.Locale, п.Enabled, п.CreatedAt, п.UpdatedAt, п.SentSerials, п.LastSentAt, п.LastFullSentAt,
            п.LastAttemptAt, ошибка, доставка);
    }

    private static (string, string?) Состояние(
        Guid[] номера, IReadOnlyDictionary<Guid, MailLetterStatus> letters, string? lastError)
    {
        var письма = номера.Select(номер => letters.GetValueOrDefault(номер)).ToList();
        var умершие = письма.Where(п => п is { State: MailLetterState.Dead }).Select(п => п!).ToList();

        if (умершие.Count > 0)
        {
            return ("failed", CertificatePolling.Undelivered(умершие));
        }

        return письма.Any(п => п is { State: MailLetterState.Pending }) ? ("queued", lastError) : ("sent", lastError);
    }
}

/// <summary>
/// Подписки на сертификаты Key Manager Plus: кому, какие файлы и когда.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Заведение, правка, удаление и ручная отправка требуют свежего второго фактора</b>
/// (<c>[RequireRecentSecondFactor]</c>, решение владельца 2026-10-20). Подписка рассылает файлы
/// сертификата <b>вместе с закрытым ключом</b> на адреса, которые в ней записаны: тот, кто дописал
/// свой адрес, получает ключ следующим опросом. Чтение кода не спрашивает.
///
/// ⚠ ⚠ <b>Право получателя кончается вместе с подпиской, а не с отправкой письма.</b> Удаление
/// подписки, убранный из неё сертификат и убранный из списка адрес отменяют письма, ещё стоящие в очереди
/// (<see cref="IMailQueue.CancelAsync"/>): иначе закрытый ключ ушёл бы тому, у кого право уже
/// отняли. Отмена — в той же транзакции, что и правка, под блокировкой строки подписки: опрос,
/// отправляющий её в эту минуту, дописал бы номера писем мимо отмены.
///
/// ⚠ В журнал — какие поля изменились и сколько адресов стало, а не сами адреса: адрес
/// получателя — сведение о человеке, а строка журнала уходит в общий лог.
///
/// ⚠ <b>Хотя бы один способ отправки</b> — при обновлении или раз в N дней (решение владельца
/// 2026-10-27): подписка без них молча ничего не слала бы, а это почти всегда невнимательность.
/// </remarks>
[ApiController]
[AuthenticatedEndpoint("ролей в проекте нет: каждая учётная запись — администратор Key Manager Plus")]
[Route("api/certificate-subscriptions")]
internal sealed class SubscriptionsController(
    AppDbContext db,
    IKeyManagerConnector connector,
    CertificateSnapshots snapshots,
    CertificateDelivery delivery,
    IMailQueue mail,
    PollWakeup wakeup,
    ICurrentUser me,
    IAuditWriter audit,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Сколько адресов в одной подписке.</summary>
    internal const int ПотолокАдресов = 20;

    /// <summary>Самый длинный Common Name, который примем.</summary>
    private const int ПотолокИмени = 255;

    /// <summary>Все подписки — по первому Common Name.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<SubscriptionOut>> List(CancellationToken cancellationToken)
    {
        var подписки = (await db.Set<CertificateSubscription>()
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .OrderBy(п => п.CommonNames[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(п => п.Id)
            .ToList();

        var письма = await mail.StatusAsync(
            [.. подписки.SelectMany(п => п.PendingMailIds ?? [])], cancellationToken);

        var сейчас = clock.GetUtcNow();

        return [.. подписки.Select(п => SubscriptionOut.From(п, сейчас, письма))];
    }

    /// <summary>Одна подписка.</summary>
    [HttpGet("{id:guid}")]
    public async Task<SubscriptionOut> Get(Guid id, CancellationToken cancellationToken) =>
        await ВыдатьAsync(await НайтиAsync(id, tracked: false, cancellationToken), cancellationToken);

    /// <summary>
    /// Завести подписку.
    /// </summary>
    /// <remarks>
    /// С <c>on_change</c> текущие сертификаты уходят на ближайшем опросе — опрос будится сразу.
    /// </remarks>
    [HttpPost]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> Create([FromBody] SubscriptionIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var сейчас = clock.GetUtcNow();

        var подписка = new CertificateSubscription
        {
            Id = Guid.CreateVersion7(),
            Description = Описание(body.Description),
            CommonNames = Имена(body),
            Recipients = Адреса(body.Recipients),
            Formats = body.Formats is null ? [.. CertificateFormats.Default] : Форматы(body.Formats),
            IncludePassphrase = body.IncludePassphrase ?? false,
            OnChange = body.OnChange ?? true,
            IntervalDays = Интервал(body.IntervalDays),
            Locale = Язык(body.Locale ?? MailLocales.Ru),
            Enabled = body.Enabled ?? true,
            CreatedAt = сейчас,
            CreatedBy = me.Id,
            UpdatedAt = сейчас,
            UpdatedBy = me.Id,
        };

        Способ(подписка);

        // ⚠ Первое письмо подписки со временем отправки — в ближайшее время отправки, а не в прошедшее сегодня.
        if (Расписание(подписка, body))
        {
            подписка.LastSlotAt = сейчас;
        }

        await db.InTransactionAsync(
            async token =>
            {
                db.Set<CertificateSubscription>().Add(подписка);

                audit.Write(new AuditEntry(
                    KeyManagerActions.SubscriptionCreated,
                    ResourceType: KeyManagerActions.SubscriptionResource,
                    ResourceId: подписка.Id,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["common_names"] = подписка.CommonNames,
                        ["recipients"] = подписка.Recipients.Length,
                        ["description"] = подписка.Description?.Length ?? 0,
                        ["formats"] = подписка.Formats,
                        ["include_passphrase"] = подписка.IncludePassphrase,
                        ["on_change"] = подписка.OnChange,
                        ["interval_days"] = подписка.IntervalDays,
                        ["active_from"] = Дата(подписка.ActiveFrom),
                        ["active_until"] = Дата(подписка.ActiveUntil),
                        ["send_time"] = Время(подписка.SendTime),
                        ["time_zone"] = подписка.TimeZone,
                        ["enabled"] = подписка.Enabled,
                    }));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        wakeup.Signal();

        return Created($"/api/certificate-subscriptions/{подписка.Id}", SubscriptionOut.From(подписка, сейчас));
    }

    /// <summary>
    /// Изменить подписку. Не присланное поле не меняется; <c>interval_days: 0</c> снимает расписание.
    /// </summary>
    /// <remarks>
    /// ⚠ Добавленный сертификат с <c>on_change</c> уйдёт на ближайшем опросе: для него ещё ничего не
    /// отправлено. Убранный — отменяет неотправленные письма подписки: они несут и его файлы; тогда
    /// подписка забывает отправленное, и оставшиеся сертификаты уходят заново ближайшим опросом. Так же
    /// отменяют письма убранный формат, снятый пароль ключа и отключение подписки.
    /// </remarks>
    [HttpPatch("{id:guid}")]
    [RequireRecentSecondFactor]
    public async Task<SubscriptionOut> Update(
        Guid id, [FromBody] SubscriptionIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var подписка = await db.InTransactionAsync(
            async token =>
            {
                var строка = await НайтиAsync(id, tracked: true, token);
                var изменения = new Dictionary<string, object?>(StringComparer.Ordinal);
                var отменено = 0;

                void Поле(string имя, object? было, object? стало)
                {
                    if (!Equals(было, стало))
                    {
                        изменения[имя] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["from"] = было,
                            ["to"] = стало,
                        };
                    }
                }

                if (body.CommonNames is not null || body.CommonName is not null)
                {
                    var имена = Имена(body);
                    var убраны = строка.CommonNames.Except(имена, StringComparer.OrdinalIgnoreCase).ToList();

                    if (!строка.CommonNames.SequenceEqual(имена, StringComparer.Ordinal))
                    {
                        Поле("common_names", string.Join(", ", строка.CommonNames), string.Join(", ", имена));
                    }

                    if (убраны.Count > 0)
                    {
                        // ⚠ Письма в очереди несут файлы и убранного сертификата: его получатели на него
                        // больше не подписаны.
                        отменено += await ОтменитьВсеAsync(строка, "сертификат убран из подписки", token);
                    }

                    строка.SentSerials = new Dictionary<string, string>(
                        строка.SentSerials.Where(пара => имена.Contains(пара.Key, StringComparer.OrdinalIgnoreCase)),
                        StringComparer.OrdinalIgnoreCase);
                    строка.CommonNames = имена;
                }

                if (body.Recipients is not null)
                {
                    var адреса = Адреса(body.Recipients);
                    var убраны = строка.Recipients.Except(адреса, StringComparer.OrdinalIgnoreCase).ToList();
                    var добавлены = адреса.Except(строка.Recipients, StringComparer.OrdinalIgnoreCase).Count();

                    if (убраны.Count > 0 || добавлены > 0)
                    {
                        изменения["recipients"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["from"] = строка.Recipients.Length,
                            ["to"] = адреса.Length,
                            ["added"] = добавлены,
                            ["removed"] = убраны.Count,
                        };
                    }

                    if (убраны.Count > 0)
                    {
                        отменено += await ОтменитьУбранным(строка, убраны, token);
                    }

                    строка.Recipients = адреса;
                }

                if (body.Formats is not null)
                {
                    var форматы = Форматы(body.Formats);
                    Поле("formats", string.Join(",", строка.Formats), string.Join(",", форматы));

                    // ⚠ ⚠ Убранный формат (чаще всего KEY — закрытый ключ) — то же сужение права, что убранный
                    // сертификат: письма в очереди несут его файлы (ревью безопасности 2026-10-27).
                    if (строка.Formats.Except(форматы, StringComparer.OrdinalIgnoreCase).Any())
                    {
                        отменено += await ОтменитьВсеAsync(строка, "формат убран из подписки", token);
                    }

                    строка.Formats = форматы;
                }

                Поле("include_passphrase", строка.IncludePassphrase, body.IncludePassphrase ?? строка.IncludePassphrase);
                Поле("on_change", строка.OnChange, body.OnChange ?? строка.OnChange);
                Поле("enabled", строка.Enabled, body.Enabled ?? строка.Enabled);

                // ⚠ ⚠ Снятый пароль ключа и отключённая подписка тоже отнимают право: письмо, ждущее в очереди
                // (сервер почты лежит), иначе унесло бы passphrase.txt или ключ уже после того, как
                // администратор передумал.
                if (строка.IncludePassphrase && body.IncludePassphrase == false)
                {
                    отменено += await ОтменитьВсеAsync(строка, "пароль ключа больше не отправляется", token);
                }

                if (строка.Enabled && body.Enabled == false)
                {
                    отменено += await ОтменитьВсеAsync(строка, "подписка отключена", token);
                }

                // Включённая заново не догоняет время отправки, пропущенное, пока была выключена.
                if (!строка.Enabled && body.Enabled == true)
                {
                    строка.LastSlotAt = clock.GetUtcNow();
                }

                строка.IncludePassphrase = body.IncludePassphrase ?? строка.IncludePassphrase;
                строка.OnChange = body.OnChange ?? строка.OnChange;
                строка.Enabled = body.Enabled ?? строка.Enabled;

                if (body.IntervalDays is not null)
                {
                    var интервал = Интервал(body.IntervalDays);
                    Поле("interval_days", строка.IntervalDays, интервал);
                    строка.IntervalDays = интервал;
                }

                if (body.ActiveFrom is not null || body.ActiveUntil is not null
                    || body.SendTime is not null || body.TimeZone is not null)
                {
                    var было = (С: Дата(строка.ActiveFrom), По: Дата(строка.ActiveUntil), В: Время(строка.SendTime), Пояс: строка.TimeZone);

                    // ⚠ Новое расписание начинается с ближайшего времени отправки: прошедшее сегодня не догоняется.
                    if (Расписание(строка, body))
                    {
                        Поле("active_from", было.С, Дата(строка.ActiveFrom));
                        Поле("active_until", было.По, Дата(строка.ActiveUntil));
                        Поле("send_time", было.В, Время(строка.SendTime));
                        Поле("time_zone", было.Пояс, строка.TimeZone);
                        строка.LastSlotAt = clock.GetUtcNow();
                    }
                }

                if (body.Locale is not null)
                {
                    var язык = Язык(body.Locale);
                    Поле("locale", строка.Locale, язык);
                    строка.Locale = язык;
                }

                if (body.Description is not null)
                {
                    var описание = Описание(body.Description);

                    // ⚠ В журнал — длина, а не текст: описание пишут свободно, и оно может называть людей.
                    if (!string.Equals(строка.Description, описание, StringComparison.Ordinal))
                    {
                        изменения["description"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["from"] = строка.Description?.Length ?? 0,
                            ["to"] = описание?.Length ?? 0,
                        };
                    }

                    строка.Description = описание;
                }

                if (отменено > 0)
                {
                    изменения["cancelled_letters"] = отменено;
                }

                // ⚠ Способ отправки проверяется, только когда его меняют: подписку 2.0 без способа иначе
                // нельзя было бы и сузить — убрать получателя или формат (ревью надёжности 2026-10-27).
                if (body.OnChange is not null || body.IntervalDays is not null)
                {
                    Способ(строка);
                }

                строка.UpdatedAt = clock.GetUtcNow();
                строка.UpdatedBy = me.Id;

                audit.Write(new AuditEntry(
                    KeyManagerActions.SubscriptionUpdated,
                    ResourceType: KeyManagerActions.SubscriptionResource,
                    ResourceId: строка.Id,
                    Changes: изменения.Count > 0 ? изменения : null));

                await db.SaveChangesAsync(token);

                return строка;
            },
            cancellationToken);

        wakeup.Signal();

        return SubscriptionOut.From(подписка, clock.GetUtcNow());
    }

    /// <summary>Удалить подписку.</summary>
    /// <remarks>
    /// ⚠ Письма подписки, ещё стоящие в очереди, отменяются вместе с ней: закрытый ключ не уходит
    /// по подписке, которой больше нет.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await db.InTransactionAsync(
            async token =>
            {
                var строка = await НайтиAsync(id, tracked: true, token);
                var отменено = await mail.CancelAsync(строка.PendingMailIds ?? [], "подписка удалена", token);

                db.Set<CertificateSubscription>().Remove(строка);

                audit.Write(new AuditEntry(
                    KeyManagerActions.SubscriptionDeleted,
                    ResourceType: KeyManagerActions.SubscriptionResource,
                    ResourceId: id,
                    Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["common_names"] = строка.CommonNames,
                        ["recipients"] = строка.Recipients.Length,
                        ["cancelled_letters"] = отменено,
                    }));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Отменить все неотправленные письма подписки и забыть их номера.
    /// </summary>
    /// <remarks>
    /// Отменённые письма несли и то, что в подписке осталось, — их получатели этого не получат. Поэтому
    /// подписка забывает отправленное, и ближайший опрос пришлёт оставшееся заново уже по новым правилам
    /// (отключённая — после включения).
    /// </remarks>
    private async Task<int> ОтменитьВсеAsync(CertificateSubscription строка, string причина, CancellationToken token)
    {
        var отменены = await mail.CancelAsync(строка.PendingMailIds ?? [], причина, token);

        строка.PendingMailIds = null;
        строка.PendingSerials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        строка.PendingFull = false;

        if (отменены > 0)
        {
            строка.SentSerials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            строка.LastFullSentAt = null;
        }

        return отменены;
    }

    /// <summary>
    /// Отменить письма подписки, адресованные убранным получателям, и забыть их номера.
    /// </summary>
    /// <remarks>
    /// ⚠ Остальным получателям письма уходят как шли: их право не менялось, а отметка «отправлено»
    /// без писем оставила бы их без сертификата до следующего выпуска.
    /// </remarks>
    private async Task<int> ОтменитьУбранным(
        CertificateSubscription строка, IReadOnlyCollection<string> убраны, CancellationToken token)
    {
        if (строка.PendingMailIds is not { Length: > 0 } номера)
        {
            return 0;
        }

        var письма = await mail.StatusAsync(номера, token);
        var отменить = номера
            .Where(номер => письма.TryGetValue(номер, out var письмо)
                && убраны.Contains(письмо.ToAddress, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (отменить.Count == 0)
        {
            return 0;
        }

        var отменено = await mail.CancelAsync(отменить, "получатель убран из подписки", token);
        var остались = номера.Except(отменить).ToArray();

        строка.PendingMailIds = остались.Length > 0 ? остались : null;

        return отменено;
    }

    /// <summary>
    /// Отправить все текущие сертификаты подписки сейчас — без проверки срока, и отключённой тоже.
    /// </summary>
    /// <remarks>
    /// ⚠ Синхронно: администратор видит отказ Key Manager Plus или почты в ответе, а не в логе.
    /// Письма при этом ложатся в очередь, как и при опросе.
    ///
    /// ⚠ ⚠ Начатая отправка доводится до конца и без того, кто её начал: выгрузка большой подписки
    /// (до 50 сертификатов в нескольких форматах) может пережить терпение Cloudflare (100 с), и обрыв
    /// запроса иначе откатывал бы её каждый раз — такую подписку нельзя было бы отправить вручную
    /// никогда (ревью надёжности 2026-10-27).
    /// </remarks>
    [HttpPost("{id:guid}/send")]
    [RequireRecentSecondFactor]
    public async Task<SubscriptionOut> Send(Guid id, CancellationToken cancellationToken)
    {
        await НайтиAsync(id, tracked: false, cancellationToken);

        DeliveryResult итог;

        try
        {
            using var клиент = await connector.OpenAsync(CancellationToken.None);
            var снимок = await snapshots.TakeAsync(клиент, CancellationToken.None);

            итог = await delivery.DeliverAsync(id, клиент, снимок, manual: true, new DownloadCache(), CancellationToken.None);
        }
        catch (KeyManagerException ошибка)
        {
            throw KeyManagerController.Unavailable(ошибка);
        }

        return итог.Outcome switch
        {
            DeliveryOutcome.Sent => await ВыдатьAsync(await НайтиAsync(id, tracked: false, cancellationToken), cancellationToken),
            DeliveryOutcome.Gone => throw AppException.NotFound("Подписка не найдена."),
            DeliveryOutcome.Busy => throw new AppException(
                KeyManagerErrorCodes.DeliveryInProgress,
                "Эту подписку прямо сейчас отправляет опрос — повторите через минуту.",
                HttpStatusCode.Conflict),
            DeliveryOutcome.CertificateMissing => throw new AppException(
                KeyManagerErrorCodes.CertificateNotFound, итог.Error!, HttpStatusCode.Conflict),
            DeliveryOutcome.KeyManagerFailed => throw KeyManagerController.Unavailable(
                new KeyManagerException(итог.Error!)),
            DeliveryOutcome.MailNotConfigured => throw new AppException(
                ErrorCodes.MailNotConfigured, итог.Error!, HttpStatusCode.Conflict),
            _ => throw new AppException(
                ErrorCodes.Internal, итог.Error ?? "Отправка не удалась.", HttpStatusCode.InternalServerError),
        };
    }

    private async Task<SubscriptionOut> ВыдатьAsync(CertificateSubscription подписка, CancellationToken cancellationToken) =>
        SubscriptionOut.From(
            подписка, clock.GetUtcNow(), await mail.StatusAsync(подписка.PendingMailIds ?? [], cancellationToken));

    /// <summary>
    /// Подписка по номеру; для правки — под блокировкой строки.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Правка ждёт блокировку (<c>FOR UPDATE</c>, без <c>SKIP LOCKED</c>): отправка держит строку
    /// секунды, пока выгружает файлы, и по выходе пишет новые номера писем. Прочитай правка строку
    /// мимо блокировки — её отмена писем не увидела бы свежих номеров, а запись затёрла бы их
    /// прежними.
    /// </remarks>
    private async Task<CertificateSubscription> НайтиAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var набор = tracked
            ? db.Set<CertificateSubscription>().FromSql($"SELECT * FROM certificate_subscriptions WHERE id = {id} FOR UPDATE")
            : db.Set<CertificateSubscription>().AsNoTracking().Where(п => п.Id == id);

        return await набор.FirstOrDefaultAsync(cancellationToken)
            ?? throw AppException.NotFound("Подписка не найдена.");
    }

    /// <summary>
    /// Common Name сертификатов: от одного до <see cref="CertificateSubscription.MaxCertificates"/>, без
    /// повторов (без учёта регистра — это имена DNS), в присланном порядке.
    /// </summary>
    private static string[] Имена(SubscriptionIn body)
    {
        if (body.CommonNames is not null && body.CommonName is not null)
        {
            throw Неверно("common_names", "Сертификаты — либо списком common_names, либо одним common_name.");
        }

        var имена = (body.CommonNames ?? (body.CommonName is null ? [] : [body.CommonName]))
            .Select(имя => имя?.Trim() ?? string.Empty)
            .Where(имя => имя.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (имена.Length == 0 || имена.Length > CertificateSubscription.MaxCertificates)
        {
            throw Неверно(
                "common_names",
                $"Нужно от 1 до {CertificateSubscription.MaxCertificates} сертификатов (Common Name).");
        }

        if (имена.FirstOrDefault(имя => имя.Length > ПотолокИмени) is { } длинное)
        {
            throw Неверно("common_names", $"Common Name длиннее {ПотолокИмени} знаков: {длинное[..40]}…");
        }

        return имена;
    }

    /// <summary>
    /// Описание: без пробелов по краям, переводы строк — <c>\n</c>, без прочих управляющих знаков; пустое —
    /// <c>null</c>, длиннее <see cref="CertificateSubscription.MaxDescription"/> — отказ.
    /// </summary>
    private static string? Описание(string? text)
    {
        var описание = new string((text ?? string.Empty)
                .ReplaceLineEndings("\n")
                .Where(знак => знак == '\n' || !char.IsControl(знак))
                .ToArray())
            .Trim();

        if (описание.Length > CertificateSubscription.MaxDescription)
        {
            throw Неверно("description", $"Описание — до {CertificateSubscription.MaxDescription} знаков.");
        }

        return описание.Length > 0 ? описание : null;
    }

    /// <summary>
    /// Период и время отправки из запроса — в подписку: не присланное не меняется, пустая строка снимает.
    /// </summary>
    /// <returns>Изменилось ли что-нибудь.</returns>
    /// <remarks>
    /// ⚠ Часовой пояс хранится только при датах или времени: без них он ничего не значит, а с ними без
    /// него «9:00» — неизвестно чьи.
    /// </remarks>
    private static bool Расписание(CertificateSubscription подписка, SubscriptionIn body)
    {
        var с = body.ActiveFrom is null ? подписка.ActiveFrom : День(body.ActiveFrom, "active_from");
        var по = body.ActiveUntil is null ? подписка.ActiveUntil : День(body.ActiveUntil, "active_until");
        var время = body.SendTime is null ? подписка.SendTime : Время(body.SendTime);
        var пояс = body.TimeZone is null ? подписка.TimeZone : Пояс(body.TimeZone);

        if (с is not null && по < с)
        {
            throw Неверно("active_until", "Последний день раньше первого.");
        }

        if (с is null && по is null && время is null)
        {
            пояс = null;
        }
        else if (пояс is null)
        {
            throw Неверно(
                "time_zone", "Нужен часовой пояс (IANA, например Europe/Kyiv): по нему считаются дни и время отправки.");
        }

        var изменилось = с != подписка.ActiveFrom || по != подписка.ActiveUntil || время != подписка.SendTime
            || !string.Equals(пояс, подписка.TimeZone, StringComparison.Ordinal);

        подписка.ActiveFrom = с;
        подписка.ActiveUntil = по;
        подписка.SendTime = время;
        подписка.TimeZone = пояс;

        return изменилось;
    }

    /// <summary>День <c>ГГГГ-ММ-ДД</c>; пустая строка — <c>null</c>.</summary>
    private static DateOnly? День(string text, string поле)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var день)
            && день.Year is >= 2000 and <= 2100
                ? день
                : throw Неверно(поле, "День — в виде ГГГГ-ММ-ДД.");
    }

    /// <summary>Время суток <c>ЧЧ:ММ</c> (секунды отбрасываются); пустая строка — <c>null</c>.</summary>
    private static TimeOnly? Время(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return TimeOnly.TryParseExact(
                text.Trim(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var время)
            ? new TimeOnly(время.Hour, время.Minute)
            : throw Неверно("send_time", "Время отправки — в виде ЧЧ:ММ.");
    }

    /// <summary>Часовой пояс IANA, известный базе часовых поясов; пустая строка — <c>null</c>.</summary>
    private static string? Пояс(string text)
    {
        var пояс = text.Trim();

        if (пояс.Length == 0)
        {
            return null;
        }

        return пояс.Length <= 64 && SendSchedule.Zone(пояс) is not null
            ? пояс
            : throw Неверно("time_zone", $"Неизвестный часовой пояс: {пояс[..Math.Min(пояс.Length, 64)]}.");
    }

    private static string? Дата(DateOnly? день) => день?.ToString("O", CultureInfo.InvariantCulture);

    private static string? Время(TimeOnly? время) => время?.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Хотя бы один способ отправки — при обновлении или по расписанию.</summary>
    private static void Способ(CertificateSubscription подписка)
    {
        if (!подписка.OnChange && подписка.IntervalDays is null)
        {
            throw Неверно(
                "on_change",
                "Выберите, когда отправлять: при обновлении в Key Manager Plus, раз в N дней или оба варианта.");
        }
    }

    /// <summary>
    /// Адреса получателей: каждый — адрес, без повторов, от одного до двадцати.
    /// </summary>
    /// <remarks>
    /// ⚠ Строка без <c>@</c> адресом не становится (вскрытие 43): письмо с ключом, ушедшее «в
    /// никуда», хуже отказа здесь.
    /// </remarks>
    private static string[] Адреса(IReadOnlyList<string>? recipients)
    {
        var адреса = (recipients ?? [])
            .Select(а => а?.Trim() ?? string.Empty)
            .ToList();

        if (адреса.Count == 0 || адреса.Count > ПотолокАдресов)
        {
            throw Неверно("recipients", $"Нужно от 1 до {ПотолокАдресов} адресов получателей.");
        }

        var плохие = адреса
            .Where(а => а.Length > 254
                || !MailAddress.TryCreate(а, out var разобран)
                || !string.Equals(разобран.Address, а, StringComparison.Ordinal))
            .ToList();

        if (плохие.Count > 0)
        {
            throw Неверно("recipients", "Не адрес: " + string.Join(", ", плохие));
        }

        return [.. адреса.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string[] Форматы(IReadOnlyList<string> formats)
    {
        var типы = formats.Select(ф => (Исходный: ф, Тип: CertificateFormats.Normalize(ф))).ToList();
        var плохие = типы.Where(т => т.Тип is null).Select(т => т.Исходный).ToList();

        if (типы.Count == 0 || плохие.Count > 0)
        {
            throw Неверно(
                "formats",
                $"Типы файлов — из списка {string.Join(", ", CertificateFormats.All)}"
                + (плохие.Count > 0 ? $"; не подходит: {string.Join(", ", плохие)}" : string.Empty));
        }

        return [.. типы.Select(т => т.Тип!).Distinct(StringComparer.Ordinal)];
    }

    private static int? Интервал(int? days) => days switch
    {
        null or 0 => null,
        >= 1 and <= 365 => days,
        _ => throw Неверно("interval_days", "Повтор — от 1 до 365 дней; 0 или null — без расписания."),
    };

    private static string Язык(string locale) =>
        MailLocales.All.Contains(locale, StringComparer.Ordinal)
            ? locale
            : throw Неверно("locale", $"Язык письма — один из: {string.Join(", ", MailLocales.All)}.");

    private static AppException Неверно(string поле, string текст) => new(
        ErrorCodes.Validation,
        текст,
        HttpStatusCode.BadRequest,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = поле });
}
