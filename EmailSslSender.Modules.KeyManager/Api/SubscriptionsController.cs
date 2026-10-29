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

    /// <summary>Один сертификат; вместе с <c>common_names</c> нельзя.</summary>
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

    /// <summary>Описание для администраторов, до 500 знаков; в письма не уходит. Пустая строка снимает.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>С какого дня (<c>ГГГГ-ММ-ДД</c>, включительно) опрос шлёт подписку сам; пустая строка снимает.</summary>
    [JsonPropertyName("active_from")]
    public string? ActiveFrom { get; init; }

    /// <summary>По какой день (<c>ГГГГ-ММ-ДД</c>, включительно) опрос шлёт подписку сам; пустая строка снимает.</summary>
    [JsonPropertyName("active_until")]
    public string? ActiveUntil { get; init; }

    /// <summary>Время отправки опросом (<c>ЧЧ:ММ</c>); пустая строка снимает — тогда сразу.</summary>
    [JsonPropertyName("send_time")]
    public string? SendTime { get; init; }

    /// <summary>Часовой пояс IANA (<c>Europe/Kyiv</c>); нужен при датах или времени отправки.</summary>
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
    /// <c>delivery</c>: <c>none</c>, <c>queued</c>, <c>sent</c>, <c>failed</c>; <c>period</c>: <c>before</c>,
    /// <c>active</c>, <c>after</c> или <c>null</c>.
    /// </summary>
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

/// <summary>Подписки на сертификаты Key Manager Plus: кому, какие файлы и когда.</summary>
/// <remarks>
/// ⚠ Сужение права (удаление, убранный сертификат, адрес, формат) отменяет письма в очереди — в той же
/// транзакции под блокировкой строки. В журнал — число адресов, не сами адреса.
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

    /// <summary>Завести подписку; с <c>on_change</c> сертификаты уходят ближайшим опросом.</summary>
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

        // Прошедшее сегодня время отправки не догоняется.
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

    /// <summary>Изменить подписку: не присланное поле не меняется; <c>interval_days: 0</c> снимает расписание.</summary>
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
                        // ⚠ Письма в очереди несут и файлы убранного сертификата.
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

                    // ⚠ Убранный формат (обычно KEY) сужает право: письма в очереди несут его файлы.
                    if (строка.Formats.Except(форматы, StringComparer.OrdinalIgnoreCase).Any())
                    {
                        отменено += await ОтменитьВсеAsync(строка, "формат убран из подписки", token);
                    }

                    строка.Formats = форматы;
                }

                Поле("include_passphrase", строка.IncludePassphrase, body.IncludePassphrase ?? строка.IncludePassphrase);
                Поле("on_change", строка.OnChange, body.OnChange ?? строка.OnChange);
                Поле("enabled", строка.Enabled, body.Enabled ?? строка.Enabled);

                // ⚠ Снятый пароль ключа и отключение тоже отнимают право: отменяем письма в очереди.
                if (строка.IncludePassphrase && body.IncludePassphrase == false)
                {
                    отменено += await ОтменитьВсеAsync(строка, "пароль ключа больше не отправляется", token);
                }

                if (строка.Enabled && body.Enabled == false)
                {
                    отменено += await ОтменитьВсеAsync(строка, "подписка отключена", token);
                }

                // Включённая заново не догоняет пропущенное время отправки.
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

                    // Новое расписание не догоняет прошедшее сегодня время.
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

                    // ⚠ В журнал — длина, а не текст: описание может называть людей.
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

                // Только при смене способа: старую подписку без способа иначе нельзя было бы и сузить.
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

    /// <summary>Удалить подписку и отменить её письма в очереди.</summary>
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

    /// <summary>Отменить все письма подписки в очереди; отменённое забывается и уйдёт заново.</summary>
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

    /// <summary>Отменить письма убранным получателям; остальным письма уходят как шли.</summary>
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

    /// <summary>Отправить все текущие сертификаты подписки сейчас — без проверки срока, и отключённой тоже.</summary>
    /// <remarks>⚠ Без отмены по запросу: большая выгрузка дольше терпения Cloudflare (100 с), обрыв откатывал бы её.</remarks>
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

    /// <summary>Подписка по номеру; для правки — под блокировкой строки.</summary>
    /// <remarks>⚠ Правка ждёт <c>FOR UPDATE</c> без <c>SKIP LOCKED</c>, иначе затёрла бы номера писем идущей отправки.</remarks>
    private async Task<CertificateSubscription> НайтиAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var набор = tracked
            ? db.Set<CertificateSubscription>().FromSql($"SELECT * FROM certificate_subscriptions WHERE id = {id} FOR UPDATE")
            : db.Set<CertificateSubscription>().AsNoTracking().Where(п => п.Id == id);

        return await набор.FirstOrDefaultAsync(cancellationToken)
            ?? throw AppException.NotFound("Подписка не найдена.");
    }

    /// <summary>Common Name без повторов (регистр не важен — это имена DNS), в присланном порядке.</summary>
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

    /// <summary>Период и время отправки — в подписку; <c>true</c>, если что-то изменилось.</summary>
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

    /// <summary>Время <c>ЧЧ:ММ</c>, секунды отбрасываются.</summary>
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

    /// <summary>Без способа отправки подписка молча ничего бы не слала.</summary>
    private static void Способ(CertificateSubscription подписка)
    {
        if (!подписка.OnChange && подписка.IntervalDays is null)
        {
            throw Неверно(
                "on_change",
                "Выберите, когда отправлять: при обновлении в Key Manager Plus, раз в N дней или оба варианта.");
        }
    }

    /// <summary>⚠ Только точные адреса: письмо с ключом «в никуда» хуже отказа.</summary>
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
