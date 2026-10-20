using System.Net;
using System.Net.Mail;
using System.Text.Json.Serialization;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Api;

/// <summary>Новая подписка.</summary>
internal sealed record SubscriptionIn
{
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
}

/// <summary>Подписка с состоянием последней отправки.</summary>
internal sealed record SubscriptionOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("common_name")] string CommonName,
    [property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients,
    [property: JsonPropertyName("formats")] IReadOnlyList<string> Formats,
    [property: JsonPropertyName("include_passphrase")] bool IncludePassphrase,
    [property: JsonPropertyName("on_change")] bool OnChange,
    [property: JsonPropertyName("interval_days")] int? IntervalDays,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("last_sent_serial")] string? LastSentSerial,
    [property: JsonPropertyName("last_sent_at")] DateTimeOffset? LastSentAt,
    [property: JsonPropertyName("last_attempt_at")] DateTimeOffset? LastAttemptAt,
    [property: JsonPropertyName("last_error")] string? LastError)
{
    public static SubscriptionOut From(CertificateSubscription п) => new(
        п.Id, п.CommonName, п.Recipients, п.Formats, п.IncludePassphrase, п.OnChange, п.IntervalDays,
        п.Locale, п.Enabled, п.CreatedAt, п.UpdatedAt, п.LastSentSerial, п.LastSentAt, п.LastAttemptAt,
        п.LastError);
}

/// <summary>
/// Подписки на сертификаты Key Manager Plus: кому, какие файлы и когда.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Заведение, правка, удаление и ручная отправка требуют свежего второго фактора.</b>
/// Подписка рассылает файлы сертификата <b>вместе с закрытым ключом</b> на адреса, которые в ней
/// записаны: тот, кто дописал свой адрес, получает ключ следующим опросом.
/// </remarks>
[ApiController]
[Route("api/certificate-subscriptions")]
internal sealed class SubscriptionsController(
    SenderDbContext db,
    IKeyManagerConnector connector,
    CertificateDelivery delivery,
    PollWakeup wakeup,
    ICurrentUser me,
    IAuditWriter audit,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Сколько адресов в одной подписке.</summary>
    internal const int ПотолокАдресов = 20;

    /// <summary>Самый длинный Common Name, который примем.</summary>
    private const int ПотолокИмени = 255;

    /// <summary>Все подписки по Common Name.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<SubscriptionOut>> List(CancellationToken cancellationToken) =>
        [.. (await db.Set<CertificateSubscription>()
                .AsNoTracking()
                .OrderBy(п => п.CommonName)
                .ThenBy(п => п.Id)
                .ToListAsync(cancellationToken))
            .Select(SubscriptionOut.From)];

    /// <summary>Одна подписка.</summary>
    [HttpGet("{id:guid}")]
    public async Task<SubscriptionOut> Get(Guid id, CancellationToken cancellationToken) =>
        SubscriptionOut.From(await НайтиAsync(id, tracked: false, cancellationToken));

    /// <summary>
    /// Завести подписку.
    /// </summary>
    /// <remarks>
    /// С <c>on_change</c> текущий сертификат уходит на ближайшем опросе — опрос будится сразу.
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
            CommonName = Имя(body.CommonName),
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

        await db.InTransactionAsync(
            async token =>
            {
                db.Set<CertificateSubscription>().Add(подписка);

                audit.Write(new AuditEntry(
                    KeyManagerActions.SubscriptionCreated,
                    ResourceType: KeyManagerActions.SubscriptionResource,
                    ResourceId: подписка.Id));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        wakeup.Signal();

        return Created($"/api/certificate-subscriptions/{подписка.Id}", SubscriptionOut.From(подписка));
    }

    /// <summary>
    /// Изменить подписку. Не присланное поле не меняется; <c>interval_days: 0</c> снимает расписание.
    /// </summary>
    /// <remarks>
    /// ⚠ Смена Common Name забывает прежнюю отправку: для другого сертификата «уже отправлено» не
    /// значит ничего, и с <c>on_change</c> новый уйдёт на ближайшем опросе.
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

                if (body.CommonName is not null)
                {
                    var имя = Имя(body.CommonName);

                    if (!string.Equals(имя, строка.CommonName, StringComparison.Ordinal))
                    {
                        строка.CommonName = имя;
                        строка.LastSentSerial = null;
                        строка.LastSentAt = null;
                        строка.LastError = null;
                    }
                }

                if (body.Recipients is not null)
                {
                    строка.Recipients = Адреса(body.Recipients);
                }

                if (body.Formats is not null)
                {
                    строка.Formats = Форматы(body.Formats);
                }

                строка.IncludePassphrase = body.IncludePassphrase ?? строка.IncludePassphrase;
                строка.OnChange = body.OnChange ?? строка.OnChange;
                строка.Enabled = body.Enabled ?? строка.Enabled;

                if (body.IntervalDays is not null)
                {
                    строка.IntervalDays = Интервал(body.IntervalDays);
                }

                if (body.Locale is not null)
                {
                    строка.Locale = Язык(body.Locale);
                }

                строка.UpdatedAt = clock.GetUtcNow();
                строка.UpdatedBy = me.Id;

                audit.Write(new AuditEntry(
                    KeyManagerActions.SubscriptionUpdated,
                    ResourceType: KeyManagerActions.SubscriptionResource,
                    ResourceId: строка.Id));

                await db.SaveChangesAsync(token);

                return строка;
            },
            cancellationToken);

        wakeup.Signal();

        return SubscriptionOut.From(подписка);
    }

    /// <summary>Удалить подписку.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRecentSecondFactor]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await db.InTransactionAsync(
            async token =>
            {
                db.Set<CertificateSubscription>().Remove(await НайтиAsync(id, tracked: true, token));

                audit.Write(new AuditEntry(
                    KeyManagerActions.SubscriptionDeleted,
                    ResourceType: KeyManagerActions.SubscriptionResource,
                    ResourceId: id));

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Отправить текущий сертификат подписки сейчас — без проверки срока, и отключённой тоже.
    /// </summary>
    /// <remarks>
    /// ⚠ Синхронно: администратор видит отказ Key Manager Plus или почты в ответе, а не в логе.
    /// Письма при этом ложатся в очередь, как и при опросе.
    /// </remarks>
    [HttpPost("{id:guid}/send")]
    [RequireRecentSecondFactor]
    public async Task<SubscriptionOut> Send(Guid id, CancellationToken cancellationToken)
    {
        await НайтиAsync(id, tracked: false, cancellationToken);

        DeliveryResult итог;

        try
        {
            using var клиент = await connector.OpenAsync(cancellationToken);
            var список = await клиент.ListAsync(cancellationToken);

            итог = await delivery.DeliverAsync(id, клиент, список, manual: true, new DownloadCache(), cancellationToken);
        }
        catch (KeyManagerException ошибка)
        {
            throw KeyManagerController.Unavailable(ошибка);
        }

        return итог.Outcome switch
        {
            DeliveryOutcome.Sent => SubscriptionOut.From(await НайтиAsync(id, tracked: false, cancellationToken)),
            DeliveryOutcome.Gone => throw AppException.NotFound("Подписка не найдена."),
            DeliveryOutcome.Busy => throw new AppException(
                ErrorCodes.DeliveryInProgress,
                "Эту подписку прямо сейчас отправляет опрос — повторите через минуту.",
                HttpStatusCode.Conflict),
            DeliveryOutcome.CertificateMissing => throw new AppException(
                ErrorCodes.KeyManagerCertificateNotFound, итог.Error!, HttpStatusCode.Conflict),
            DeliveryOutcome.KeyManagerFailed => throw KeyManagerController.Unavailable(
                new KeyManagerException(итог.Error!)),
            DeliveryOutcome.MailNotConfigured => throw new AppException(
                ErrorCodes.MailNotConfigured, итог.Error!, HttpStatusCode.Conflict),
            _ => throw new AppException(
                ErrorCodes.Internal, итог.Error ?? "Отправка не удалась.", HttpStatusCode.InternalServerError),
        };
    }

    private async Task<CertificateSubscription> НайтиAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var набор = db.Set<CertificateSubscription>().AsQueryable();

        if (!tracked)
        {
            набор = набор.AsNoTracking();
        }

        return await набор.FirstOrDefaultAsync(п => п.Id == id, cancellationToken)
            ?? throw AppException.NotFound("Подписка не найдена.");
    }

    private static string Имя(string? commonName)
    {
        var имя = commonName?.Trim();

        if (string.IsNullOrEmpty(имя) || имя.Length > ПотолокИмени)
        {
            throw Неверно("common_name", $"Нужен Common Name сертификата, до {ПотолокИмени} знаков.");
        }

        return имя;
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
