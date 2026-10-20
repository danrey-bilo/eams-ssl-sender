using System.Globalization;
using System.Text;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Чем кончилась попытка отправить подписку.</summary>
internal enum DeliveryOutcome
{
    /// <summary>Письма в очереди, отправка отмечена.</summary>
    Sent,

    /// <summary>Срок не подошёл: серийный номер прежний, расписание не наступило.</summary>
    NotDue,

    /// <summary>Подписка отключена — опрос её не шлёт.</summary>
    Disabled,

    /// <summary>Подписку прямо сейчас отправляет другой процесс или запрос.</summary>
    Busy,

    /// <summary>Подписки больше нет.</summary>
    Gone,

    /// <summary>Сертификата с таким Common Name в Key Manager Plus нет.</summary>
    CertificateMissing,

    /// <summary>Key Manager Plus не отдал файл или пароль.</summary>
    KeyManagerFailed,

    /// <summary>Почта не настроена: письма некуда класть.</summary>
    MailNotConfigured,

    /// <summary>Что-то иное: база, шифрование.</summary>
    Failed,
}

/// <summary>Итог попытки и причина отправки, если она состоялась.</summary>
internal sealed record DeliveryResult(DeliveryOutcome Outcome, DeliveryReason? Reason = null, string? Error = null);

/// <summary>
/// Файлы и пароли, уже выгруженные в этом проходе.
/// </summary>
/// <remarks>
/// Два получателя одного сертификата на двух подписках — один запрос к Key Manager Plus, а не
/// два. Живёт один проход опроса и умирает с ним: ключ в памяти дольше прохода не нужен.
/// </remarks>
internal sealed class DownloadCache
{
    public Dictionary<(string CommonName, string Serial, string Format), KmpFile> Files { get; } = [];

    public Dictionary<(string CommonName, string Serial), string?> Passphrases { get; } = [];
}

/// <summary>
/// Отправка сертификата по одной подписке: выбрать текущий, решить, пора ли, выгрузить, поставить письма.
/// </summary>
/// <remarks>
/// ⚠ ⚠ ⚠ <b>Одна подписка — одна транзакция, и всё в ней или ничего.</b> Строка подписки берётся
/// <c>FOR UPDATE SKIP LOCKED</c>, срок проверяется уже под блокировкой, письма кладутся в очередь
/// той же единицей работы, что и отметка «отправлено». Два процесса (или опрос и ручная отправка)
/// не пошлют один сертификат дважды: второй увидит строку занятой и пройдёт мимо. Упавшая
/// выгрузка или ненастроенная почта откатывают всё — ни одного письма без отметки и ни одной
/// отметки без писем.
///
/// ⚠ <b>Выгрузка идёт внутри транзакции</b>, то есть строка подписки заблокирована на время
/// запросов к Key Manager Plus. Это секунды, и заблокирована ровно одна строка; зато нет окна,
/// в котором второй процесс решил бы, что пора, по ещё не отмеченной отправке.
///
/// ⚠ <b>Ошибка пишется отдельной записью ПОСЛЕ отката</b> — иначе она откатилась бы вместе со
/// всем остальным, и администратор не узнал бы, почему сертификат не приходит.
/// </remarks>
internal sealed class CertificateDelivery(
    SenderDbContext db,
    IMailQueue mail,
    IAuditWriter audit,
    TimeProvider clock,
    ILogger<CertificateDelivery> log)
{
    /// <summary>
    /// Пора ли слать и почему. <c>null</c> — не пора.
    /// </summary>
    /// <remarks>
    /// ⚠ Серийные номера сравниваются без учёта регистра: это hex, и Key Manager Plus не обещает
    /// писать его одинаково в разных версиях.
    /// </remarks>
    internal static DeliveryReason? Due(CertificateSubscription subscription, KmpCertificate current, DateTimeOffset now)
    {
        var первая = subscription.LastSentSerial is null;

        if (subscription.OnChange
            && !string.Equals(subscription.LastSentSerial, current.SerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            return первая ? DeliveryReason.Initial : DeliveryReason.Changed;
        }

        if (subscription.IntervalDays is { } дней
            && (subscription.LastSentAt is not { } когда || now >= когда.AddDays(дней)))
        {
            return первая ? DeliveryReason.Initial : DeliveryReason.Scheduled;
        }

        return null;
    }

    /// <summary>
    /// Отправить подписку, если пора (или в любом случае, если <paramref name="manual"/>).
    /// </summary>
    /// <param name="id">Подписка.</param>
    /// <param name="client">Открытый клиент Key Manager Plus.</param>
    /// <param name="snapshot">Снимок сертификатов этого прохода.</param>
    /// <param name="manual">Ручная отправка: без проверки срока и признака «включена».</param>
    /// <param name="cache">Выгруженное в этом проходе.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task<DeliveryResult> DeliverAsync(
        Guid id,
        IKeyManagerClient client,
        CertificateSnapshot snapshot,
        bool manual,
        DownloadCache cache,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.InTransactionAsync(
                token => ОтправитьAsync(id, client, snapshot, manual, cache, token), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ошибка)
        {
            // Набранное в откатившейся транзакции — письма, правки строки — не должно уехать
            // ни с какой следующей фиксацией этого контекста.
            db.ChangeTracker.Clear();

            var (итог, текст) = ошибка switch
            {
                KeyManagerException отказ => (DeliveryOutcome.KeyManagerFailed, отказ.Message),
                ОтказПочты { Result: MailQueueResult.NotConfigured } =>
                    (DeliveryOutcome.MailNotConfigured, "Почта не настроена: письма с сертификатом некуда отправить."),
                ОтказПочты отказ => (DeliveryOutcome.Failed, $"Почта отказала в постановке письма: {отказ.Result}."),
                _ => (DeliveryOutcome.Failed, $"Отправка не удалась: {SafeError.Describe(ошибка)}."),
            };

            log.LogWarning("keymanager_delivery_failed {Subscription} {Outcome} {Error}", id, итог, текст);

            await ОтметитьНеудачуAsync(id, текст);

            return new DeliveryResult(итог, Error: текст);
        }
    }

    private async Task<DeliveryResult> ОтправитьAsync(
        Guid id,
        IKeyManagerClient client,
        CertificateSnapshot snapshot,
        bool manual,
        DownloadCache cache,
        CancellationToken token)
    {
        var подписка = await db.Set<CertificateSubscription>()
            .FromSql($"SELECT * FROM certificate_subscriptions WHERE id = {id} FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(token);

        if (подписка is null)
        {
            // ⚠ SKIP LOCKED не отличает «занята» от «нет»: спрашиваем отдельно, без блокировки.
            return new DeliveryResult(
                await db.Set<CertificateSubscription>().AnyAsync(п => п.Id == id, token)
                    ? DeliveryOutcome.Busy
                    : DeliveryOutcome.Gone);
        }

        if (!manual && !подписка.Enabled)
        {
            return new DeliveryResult(DeliveryOutcome.Disabled);
        }

        // ⚠ ⚠ Снимок старше последней отправки — устарел: её сделал другой процесс или ручная
        // отправка по более свежему списку. Слать по старому значило бы разослать прежний
        // сертификат под видом обновления и откатить отметку на него.
        if (!manual && подписка.LastSentAt > snapshot.FetchedAt)
        {
            return new DeliveryResult(DeliveryOutcome.NotDue);
        }

        var сейчас = clock.GetUtcNow();
        var текущий = snapshot.Current(подписка.CommonName);

        if (текущий is null)
        {
            var текст = $"Сертификата {подписка.CommonName} нет в Key Manager Plus.";

            подписка.LastAttemptAt = сейчас;
            подписка.LastError = текст;
            await db.SaveChangesAsync(token);

            return new DeliveryResult(DeliveryOutcome.CertificateMissing, Error: текст);
        }

        var причина = manual ? DeliveryReason.Manual : Due(подписка, текущий, сейчас);

        if (причина is null)
        {
            return new DeliveryResult(DeliveryOutcome.NotDue);
        }

        // ⚠ Почта проверяется ДО выгрузки: иначе при ненастроенной почте каждый опрос выгружал бы
        // закрытые ключи из Key Manager Plus только затем, чтобы выбросить их.
        if (!await mail.CanSendAsync(token))
        {
            throw new ОтказПочты(MailQueueResult.NotConfigured);
        }

        var вложения = await ВыгрузитьAsync(client, текущий, подписка.Formats, cache, token);

        var пароль = подписка.IncludePassphrase
            ? await ПарольAsync(client, текущий, cache, token)
            : null;

        // ⚠ Пароль ключа — отдельным вложением, а не строкой в теле: тело письма живёт в очереди
        // до уборки, а вложения стираются, как только письмо ушло.
        if (пароль is not null)
        {
            вложения.Add(new MailAttachment(
                CertificateMail.PassphraseFile, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(пароль + Environment.NewLine)));
        }

        var номера = new List<Guid>(подписка.Recipients.Length);

        foreach (var адрес in подписка.Recipients)
        {
            var значения = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["common_name"] = текущий.CommonName,
                ["serial_number"] = текущий.SerialNumber,
                ["expires_on"] = текущий.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    ?? CertificateMail.UnknownDate,
                ["reason"] = CertificateMail.Reason(причина.Value, подписка.Locale),
                ["files"] = string.Join(", ", вложения.Select(ф => ф.FileName)),
                ["passphrase"] = CertificateMail.Passphrase(пароль is not null, подписка.Locale),
            };

            var итог = await mail.EnqueueAsync(
                адрес, CertificateMail.Kind, подписка.Locale, значения, вложения, token);

            if (итог.Result != MailQueueResult.Queued || итог.Id is not { } номер)
            {
                throw new ОтказПочты(итог.Result);
            }

            номера.Add(номер);
        }

        подписка.LastSentSerial = текущий.SerialNumber;
        подписка.LastSentAt = сейчас;
        подписка.LastAttemptAt = сейчас;
        подписка.LastError = null;
        подписка.PendingMailIds = [.. номера];

        // ⚠ В журнал — подписка и причина, без адресов и без содержимого: адрес получателя —
        // сведение о человеке, а строка уходит в общий лог (правило 14).
        audit.Write(new AuditEntry(
            KeyManagerActions.CertificateSent,
            ActorType: manual ? null : AuditActorType.System,
            ResourceType: KeyManagerActions.SubscriptionResource,
            ResourceId: подписка.Id,
            Reason: причина.Value.ToString().ToLowerInvariant()));

        await db.SaveChangesAsync(token);

        return new DeliveryResult(DeliveryOutcome.Sent, причина);
    }

    private static async Task<List<MailAttachment>> ВыгрузитьAsync(
        IKeyManagerClient client,
        KmpCertificate текущий,
        IReadOnlyList<string> форматы,
        DownloadCache cache,
        CancellationToken token)
    {
        var файлы = new List<MailAttachment>(форматы.Count);

        foreach (var формат in форматы)
        {
            var ключ = (текущий.CommonName, текущий.SerialNumber, формат);

            if (!cache.Files.TryGetValue(ключ, out var файл))
            {
                файл = await client.DownloadAsync(текущий.CommonName, текущий.SerialNumber, формат, token);
                cache.Files[ключ] = файл;
            }

            файлы.Add(new MailAttachment(файл.FileName, файл.ContentType, файл.Content));
        }

        return файлы;
    }

    private static async Task<string?> ПарольAsync(
        IKeyManagerClient client, KmpCertificate текущий, DownloadCache cache, CancellationToken token)
    {
        var ключ = (текущий.CommonName, текущий.SerialNumber);

        if (!cache.Passphrases.TryGetValue(ключ, out var пароль))
        {
            пароль = await client.PassphraseAsync(текущий.CommonName, текущий.SerialNumber, token);
            cache.Passphrases[ключ] = пароль;
        }

        return пароль;
    }

    /// <summary>Записать причину неудачи; сбой самой записи — в лог, не наверх.</summary>
    private async Task ОтметитьНеудачуAsync(Guid id, string текст)
    {
        try
        {
            await db.Set<CertificateSubscription>()
                .Where(п => п.Id == id)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(п => п.LastError, текст)
                        .SetProperty(п => п.LastAttemptAt, clock.GetUtcNow()),
                    CancellationToken.None);
        }
        catch (Exception ошибка)
        {
            log.LogError("keymanager_failure_mark_failed {Error}", SafeError.Describe(ошибка));
        }
    }

    /// <summary>Очередь писем не приняла письмо — откатить всю отправку подписки.</summary>
    private sealed class ОтказПочты(MailQueueResult result) : Exception($"Очередь писем ответила {result}")
    {
        public MailQueueResult Result { get; } = result;
    }
}
