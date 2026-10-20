using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Итог прохода опроса.</summary>
/// <param name="Polled">Состоялся ли опрос: настроено и Key Manager Plus ответил.</param>
/// <param name="Certificates">Сколько сертификатов он вернул.</param>
/// <param name="Sent">Сколько подписок отправлено.</param>
internal readonly record struct PollRun(bool Polled, int Certificates, int Sent);

/// <summary>
/// Один проход: спросить Key Manager Plus, обновить снимок, отправить подписки, которым пора.
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
    SenderDbContext db,
    IKeyManagerConnector connector,
    CertificateDelivery delivery,
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

        IReadOnlyList<KmpCertificate> список;
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
                список = await клиент.ListAsync(cancellationToken);
            }
            catch (KeyManagerException ошибка)
            {
                await ЗаписатьОпросAsync(null, ошибка.Message);
                log.LogWarning("keymanager_poll_failed {Error}", ошибка.Message);
                return default;
            }

            await ЗапомнитьAsync(список, cancellationToken);
            await ЗаписатьОпросAsync(список.Count, null);

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
                var итог = await delivery.DeliverAsync(id, клиент, список, manual: false, кэш, cancellationToken);

                if (итог.Outcome == DeliveryOutcome.Sent)
                {
                    отправлено++;
                }
            }

            // ⚠ Проход без отправок молчит — по той же причине, что и ненастроенный.
            if (отправлено > 0)
            {
                log.LogInformation("keymanager_poll_delivered {Sent} {Certificates}", отправлено, список.Count);
            }

            return new PollRun(true, список.Count, отправлено);
        }
    }

    /// <summary>
    /// Обновить снимок: новые пары «Common Name + серийный» добавить, виденные — отметить.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>ON CONFLICT</c>, а не «прочитать и добавить недостающие»: два процесса, опросившие
    /// одновременно, иначе споткнулись бы о первичный ключ друг друга.
    /// </remarks>
    private async Task ЗапомнитьAsync(IReadOnlyList<KmpCertificate> список, CancellationToken cancellationToken)
    {
        var сейчас = clock.GetUtcNow();

        await db.InTransactionAsync(
            async token =>
            {
                foreach (var с in список.DistinctBy(с => (с.CommonName, с.SerialNumber)))
                {
                    await db.Database.ExecuteSqlAsync(
                        $"""
                        INSERT INTO keymanager_certificates
                            (common_name, serial_number, expires_on, first_seen_at, last_seen_at)
                        VALUES ({с.CommonName}, {с.SerialNumber}, {с.ExpiresOn}, {сейчас}, {сейчас})
                        ON CONFLICT (common_name, serial_number) DO UPDATE
                        SET expires_on = EXCLUDED.expires_on, last_seen_at = EXCLUDED.last_seen_at
                        """,
                        token);
                }
            },
            cancellationToken);
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
