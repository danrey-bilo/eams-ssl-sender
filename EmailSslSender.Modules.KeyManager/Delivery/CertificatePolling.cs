using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Domain;
using Modules.Core.Contracts.Mail;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Итог прохода опроса.</summary>
/// <param name="Polled">Состоялся ли опрос: настроено и Key Manager Plus ответил.</param>
/// <param name="Certificates">Сколько сертификатов он вернул.</param>
/// <param name="Sent">Сколько подписок отправлено.</param>
internal readonly record struct PollRun(bool Polled, int Certificates, int Sent);

/// <summary>Один проход: сверить прежние письма, спросить Key Manager Plus, отправить подписки, которым пора.</summary>
/// <remarks>⚠ Вебхуков у Key Manager Plus нет: обновление — новый серийный у того же Common Name.</remarks>
internal sealed class CertificatePolling(
    AppDbContext db,
    IKeyManagerConnector connector,
    CertificateSnapshots snapshots,
    CertificateDelivery delivery,
    IMailQueue mail,
    TimeProvider clock,
    ILogger<CertificatePolling> log)
{
    public async Task<PollRun> RunAsync(CancellationToken cancellationToken = default)
    {
        var настройки = await connector.ReadAsync(cancellationToken);

        if (!настройки.Configured)
        {
            return default;
        }

        await СверитьПисьмаAsync(cancellationToken);

        CertificateSnapshot снимок;
        IKeyManagerClient клиент;

        try
        {
            клиент = await connector.OpenAsync(cancellationToken);
        }
        catch (Exception ошибка) when (!(ошибка is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            await НеудачаОпросаAsync(ошибка);
            return default;
        }

        using (клиент)
        {
            try
            {
                снимок = await snapshots.TakeAsync(клиент, cancellationToken);
            }
            catch (Exception ошибка) when (!(ошибка is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                await НеудачаОпросаAsync(ошибка);
                return default;
            }

            await ЗаписатьОпросAsync(снимок.Certificates.Count, null);

            var подписки = await db.Set<CertificateSubscription>()
                .AsNoTracking()
                .Where(п => п.Enabled)
                .OrderBy(п => п.Id)
                .Select(п => п.Id)
                .ToListAsync(cancellationToken);

            var кэш = new DownloadCache();
            var отправлено = 0;

            foreach (var id in подписки)
            {
                var итог = await delivery.DeliverAsync(id, клиент, снимок, manual: false, кэш, cancellationToken);

                if (итог.Outcome == DeliveryOutcome.Sent)
                {
                    отправлено++;
                }
            }

            // Проход без отправок, как и ненастроенный, молчит: не топить лог.
            if (отправлено > 0)
            {
                log.LogInformation("keymanager_poll_delivered {Sent} {Certificates}", отправлено, снимок.Certificates.Count);
            }

            return new PollRun(true, снимок.Certificates.Count, отправлено);
        }
    }

    /// <summary>Сколько раз подряд отправка повторяется сама, если не дошло ни одно письмо.</summary>
    internal const int ПовторовСамих = 3;

    /// <summary>Сверить письма прежних отправок с очередью: ушедшие забыть, умершие — в <c>last_error</c>.</summary>
    /// <remarks>⚠ Умершее письмо части получателей само не повторяется (дубли остальным); не дошло ни одно — автоповтор.</remarks>
    private async Task СверитьПисьмаAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ждут = await db.Set<CertificateSubscription>()
                .AsNoTracking()
                .Where(п => п.PendingMailIds != null)
                .Select(п => new { п.Id, Номера = п.PendingMailIds! })
                .ToListAsync(cancellationToken);

            if (ждут.Count == 0)
            {
                return;
            }

            var судьбы = await mail.StatusAsync([.. ждут.SelectMany(п => п.Номера)], cancellationToken);

            foreach (var подписка in ждут)
            {
                var письма = подписка.Номера
                    .Select(номер => судьбы.GetValueOrDefault(номер))
                    .ToList();

                // Нет в очереди — значит, удалено уборкой, а уборка удаляет только ушедшие.
                if (письма.Any(п => п is { State: MailLetterState.Pending }))
                {
                    continue;
                }

                var умершие = письма.Where(п => п is { State: MailLetterState.Dead }).Select(п => п!).ToList();

                await ЗакрытьОтправкуAsync(подписка.Id, подписка.Номера, умершие, всеУмерли: умершие.Count == письма.Count, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ошибка)
        {
            log.LogError("keymanager_reconcile_failed {Error}", SafeError.Describe(ошибка));
        }
    }

    /// <summary>Вердикт о письмах — в подписку; ⚠ только если её номера не сменила новая отправка.</summary>
    private async Task ЗакрытьОтправкуAsync(
        Guid id, Guid[] было, IReadOnlyList<MailLetterStatus> умершие, bool всеУмерли, CancellationToken cancellationToken)
    {
        await db.InTransactionAsync(
            async token =>
            {
                var строка = await db.Set<CertificateSubscription>()
                    .FromSql($"SELECT * FROM certificate_subscriptions WHERE id = {id} FOR UPDATE SKIP LOCKED")
                    .SingleOrDefaultAsync(token);

                if (строка?.PendingMailIds is not { } номера || !номера.SequenceEqual(было))
                {
                    return;
                }

                var несли = строка.PendingSerials;
                var полная = строка.PendingFull;

                строка.PendingMailIds = null;
                строка.PendingSerials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                строка.PendingFull = false;

                if (умершие.Count == 0)
                {
                    строка.AutoRetries = 0;
                }
                else if (!всеУмерли)
                {
                    строка.AutoRetries = 0;
                    строка.LastError = Undelivered(умершие);
                    log.LogWarning("keymanager_delivery_undelivered {Subscription} {Count}", id, умершие.Count);
                }
                else if (строка.AutoRetries < ПовторовСамих)
                {
                    // Снять отметки ровно этой отправки, чтобы этот же проход отправил её заново.
                    строка.SentSerials = new Dictionary<string, string>(
                        строка.SentSerials.Where(пара => !несли.TryGetValue(пара.Key, out var нёс)
                            || !string.Equals(нёс, пара.Value, StringComparison.OrdinalIgnoreCase)),
                        StringComparer.OrdinalIgnoreCase);

                    if (полная)
                    {
                        строка.LastFullSentAt = null;
                    }

                    // Повтор не ждёт следующего времени отправки.
                    строка.LastSlotAt = null;

                    строка.AutoRetries++;
                    строка.LastError = НеДошло(умершие)
                        + $" Не дошло ни одно письмо — отправка повторится сама (повтор {строка.AutoRetries} из {ПовторовСамих}).";
                    log.LogWarning("keymanager_delivery_retry {Subscription} {Attempt}", id, строка.AutoRetries);
                }
                else
                {
                    строка.LastError = НеДошло(умершие)
                        + $" Не дошло ни одно письмо, автоповтор исчерпан ({ПовторовСамих} раза) — отправьте вручную, когда причина устранена.";
                    log.LogWarning("keymanager_delivery_undelivered {Subscription} {Count}", id, умершие.Count);
                }

                await db.SaveChangesAsync(token);
            },
            cancellationToken);

        db.ChangeTracker.Clear();
    }

    /// <summary>Текст для подписки: на какие адреса письмо не дошло и почему.</summary>
    internal static string Undelivered(IReadOnlyList<MailLetterStatus> умершие) =>
        НеДошло(умершие) + " Отправьте повторно, когда причина устранена.";

    private static string НеДошло(IReadOnlyList<MailLetterStatus> умершие) =>
        "Письмо не доставлено: "
        + string.Join("; ", умершие.Select(п => п.LastError is null ? п.ToAddress : $"{п.ToAddress} — {п.LastError}"))
        + ".";

    /// <summary>Любой неудачный опрос — в <c>last_poll_error</c> для панели и в лог.</summary>
    private async Task НеудачаОпросаAsync(Exception ошибка)
    {
        var текст = ошибка is KeyManagerException ? ошибка.Message : $"Опрос не удался: {SafeError.Describe(ошибка)}.";

        await ЗаписатьОпросAsync(null, текст);
        log.LogWarning("keymanager_poll_failed {Error}", текст);
    }

    /// <summary>Итог опроса в строку состояния; сбой записи — в лог, не наверх.</summary>
    private async Task ЗаписатьОпросAsync(int? сколько, string? ошибка)
    {
        try
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO keymanager_state (id, last_poll_at, last_poll_error, last_poll_count)
                VALUES ({KeyManagerState.SingletonId}, {clock.GetUtcNow()}, {ошибка}, {сколько})
                ON CONFLICT (id) DO UPDATE
                SET last_poll_at = EXCLUDED.last_poll_at,
                    last_poll_error = EXCLUDED.last_poll_error,
                    last_poll_count = COALESCE(EXCLUDED.last_poll_count, keymanager_state.last_poll_count)
                """,
                CancellationToken.None);
        }
        catch (Exception сбой)
        {
            log.LogError("keymanager_poll_mark_failed {Error}", SafeError.Describe(сбой));
        }
    }
}
