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

/// <summary>
/// Один проход: сверить прежние письма, спросить Key Manager Plus, отправить подписки, которым пора.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>«Сертификат обновился» замечается только так.</b> Вебхуков, истории и отметки
/// «изменён» у REST API Key Manager Plus нет. Продление — это новый серийный номер у того же
/// Common Name в <c>getAllSSLCertsExpiryDate</c>, и опрос сравнивает его с отправленным в
/// подписке.
///
/// ⚠ <b>Ненастроенная интеграция — тихий пустой проход.</b> Строка в лог раз в четверть часа у
/// установки, которой Key Manager Plus не нужен, утопила бы в логе всё остальное.
///
/// ⚠ <b>Одна подписка не уносит остальные:</b> у каждой своя транзакция, и неудача остаётся в
/// её <c>last_error</c>.
/// </remarks>
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
        catch (KeyManagerException ошибка)
        {
            await ЗаписатьОпросAsync(null, ошибка.Message);
            log.LogWarning("keymanager_poll_failed {Error}", ошибка.Message);
            return default;
        }

        using (клиент)
        {
            try
            {
                снимок = await snapshots.TakeAsync(клиент, cancellationToken);
            }
            catch (KeyManagerException ошибка)
            {
                await ЗаписатьОпросAsync(null, ошибка.Message);
                log.LogWarning("keymanager_poll_failed {Error}", ошибка.Message);
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

            // ⚠ Проход без отправок молчит — по той же причине, что и ненастроенный.
            if (отправлено > 0)
            {
                log.LogInformation("keymanager_poll_delivered {Sent} {Certificates}", отправлено, снимок.Certificates.Count);
            }

            return new PollRun(true, снимок.Certificates.Count, отправлено);
        }
    }

    /// <summary>
    /// Сверить письма прежних отправок с очередью: ушедшие забыть, умершие — в <c>last_error</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Умершее письмо НЕ отправляется заново само.</b> Причина смерти бывает у одного
    /// адреса (ящика нет) — повтор раз в час слал бы остальным получателям подписки дубли без
    /// конца. Ошибка видна в подписке, а отправить повторно — кнопка, когда причина устранена.
    ///
    /// ⚠ Условное обновление по прежним номерам: если подписку тем временем отправили снова, её
    /// новые номера не затираются вердиктом о старых.
    /// </remarks>
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
                var было = подписка.Номера;
                string? ошибка = умершие.Count == 0 ? null : Undelivered(умершие);

                await db.Set<CertificateSubscription>()
                    .Where(п => п.Id == подписка.Id && п.PendingMailIds == было)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(п => п.PendingMailIds, (Guid[]?)null)
                            .SetProperty(п => п.LastError, п => ошибка ?? п.LastError),
                        cancellationToken);

                if (ошибка is not null)
                {
                    log.LogWarning("keymanager_delivery_undelivered {Subscription} {Count}", подписка.Id, умершие.Count);
                }
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

    /// <summary>Текст для подписки: на какие адреса письмо не дошло и почему.</summary>
    internal static string Undelivered(IReadOnlyList<MailLetterStatus> умершие) =>
        "Письмо не доставлено: "
        + string.Join("; ", умершие.Select(п => п.LastError is null ? п.ToAddress : $"{п.ToAddress} — {п.LastError}"))
        + ". Отправьте повторно, когда причина устранена.";

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
