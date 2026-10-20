using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Modules.Mail.Templates;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Mail.Sending;

/// <summary>
/// Постановка письма в очередь.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>НЕ ЗОВЁТ <c>SaveChangesAsync</c>, и это несущее свойство.</b> Строка добавляется в
/// текущую единицу работы и уезжает в базу с фиксацией вызывающего. Письмо «пароль сменён» не
/// должно существовать, если сама смена откатилась, — а с отдельной фиксацией существовало бы.
///
/// ⚠ <b>Очередь — ТАБЛИЦА, а не задача в Redis.</b> Redis не транзакционен с Postgres: падение
/// между «пароль сменён» и «задача поставлена» дало бы молча непосланное письмо. Таблица
/// фиксируется той же транзакцией, что и само действие.
///
/// ⚠ <b>Ни один отказ не выходит исключением.</b> «Почта не настроена» и «слишком часто» — это
/// <see cref="MailQueueResult"/>. В эталоне это два исключения, и три места ловят их по-разному;
/// отправка кода входа ловит только одно, то есть ненастроенная почта уронила бы вход.
///
/// ⚠ <b>В логе отказа есть ВИД письма и нет АДРЕСА.</b> Сам факт «на этот адрес просили сброс»
/// является сведением о человеке, а строка уезжает в общий лог приложения (правило 14).
/// </remarks>
internal sealed class MailQueue(
    SenderDbContext db,
    MailTemplates templates,
    ISettingsReader settings,
    ISecretEnvelope envelope,
    TimeProvider clock,
    MailWakeup wakeup,
    ILogger<MailQueue> log) : IMailQueue
{
    /// <summary>Контекст AEAD тела письма.</summary>
    /// <remarks>
    /// ⚠ Своя приставка, отличная от секретов установки: годная запечатка тела не должна
    /// открываться на месте пароля SMTP и наоборот.
    /// </remarks>
    private const string Контекст = "sender:mail-body";

    public async Task<bool> CanSendAsync(CancellationToken cancellationToken = default) =>
        (await MailPolicy.ReadAsync(settings, cancellationToken)).Configured;

    public async Task<MailQueueResult> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default) =>
        (await EnqueueAsync(toAddress, kind, locale, values, [], cancellationToken)).Result;

    public async Task<IReadOnlyDictionary<Guid, MailLetterStatus>> StatusAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, MailLetterStatus>();
        }

        var номера = ids.ToArray();
        var предел = (await MailPolicy.ReadAsync(settings, cancellationToken)).MaxAttempts;

        var письма = await db.Set<MailOutbox>()
            .AsNoTracking()
            .Where(письмо => номера.Contains(письмо.Id))
            .Select(письмо => new { письмо.Id, письмо.ToAddress, письмо.SentAt, письмо.Attempts, письмо.NextAttemptAt, письмо.LastError })
            .ToListAsync(cancellationToken);

        // ⚠ Мёртвое — и исчерпавшее попытки, и стёртое «навсегда»: поднятый потом предел попыток
        // такое письмо в работу не вернёт.
        return письма.ToDictionary(
            письмо => письмо.Id,
            письмо => new MailLetterStatus(
                письмо.SentAt is not null ? MailLetterState.Sent
                    : письмо.Attempts >= предел || письмо.NextAttemptAt >= MailAttachments.Никогда ? MailLetterState.Dead
                    : MailLetterState.Pending,
                письмо.ToAddress,
                письмо.LastError));
    }

    public async Task<MailTicket> EnqueueAsync(
        string toAddress,
        string kind,
        string locale,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<MailAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toAddress);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(attachments);

        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        if (!политика.Configured)
        {
            log.LogInformation("mail_not_configured {Kind}", kind);
            return new MailTicket(MailQueueResult.NotConfigured, null);
        }

        if (templates.IsThrottled(kind)
            && await ЗаПределомAsync(toAddress, kind, политика.RatePerHour, cancellationToken))
        {
            log.LogWarning("mail_rate_limited {Kind} {Limit}", kind, политика.RatePerHour);
            return new MailTicket(MailQueueResult.RateLimited, null);
        }

        var (тема, тело) = templates.Render(kind, locale, values);
        var ключ = envelope.CreateDataKey();
        var номер = Guid.CreateVersion7();

        db.Set<MailOutbox>().Add(new MailOutbox
        {
            Id = номер,
            ToAddress = toAddress,
            Subject = тема,
            SealedBody = envelope.SealText(ключ, тело, Контекст).Bytes,
            DataKey = ключ.Bytes,
            SealedAttachments = MailAttachments.Seal(envelope, ключ, attachments),
            Kind = kind,
            CreatedAt = clock.GetUtcNow(),
            NextAttemptAt = clock.GetUtcNow(),
        });

        // ⚠ ⚠ ⚠ БУДИМ НАСОС ДО `SaveChanges`, И ЭТО ОСОЗНАННЫЙ РАЗМЕН (этап 60.2). Строка
        // добавляется в единицу работы вызывающего и уезжает в базу его транзакцией — то есть
        // отсюда не видно, состоялась ли она. Ложное пробуждение стоит одного пустого прохода;
        // пропущенное — минуты человеческого ожидания у экрана с кодом входа. Разница на два
        // порядка, и потому сигнал даётся здесь, а не «где-нибудь после фиксации».
        //
        // ⚠ ⚠ Цена «одного пустого прохода» была названа неполно (ревью 2026-09-15, mail-1): насос
        // часто приходит РАНЬШЕ фиксации этой же строки, и пустой проход расходовал сигнал и
        // разрежал ход — код входа ждал до минуты. Насос теперь отличает пустой проход по сигналу
        // и повторяет захват через две секунды (`MailDeliveryService.Следующий`).
        wakeup.Signal();

        return new MailTicket(MailQueueResult.Queued, номер);
    }

    /// <summary>
    /// Исчерпан ли предел частоты на эту пару «адрес + вид».
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Считаются ПОСТАВЛЕННЫЕ, а не отправленные.</b> Счёт по <c>sent_at</c> означал бы,
    /// что неработающий SMTP снимает ограничение: очередь копится без счёта, а при починке
    /// человек получает всё разом — то есть предел частоты перестаёт работать ровно тогда,
    /// когда он нужнее всего.
    ///
    /// ⚠ <b>Пара «адрес + вид», а не один адрес.</b> Поток писем одного вида не должен съедать
    /// квоту сброса пароля: иначе перебором приглашений закрывают человеку восстановление
    /// доступа.
    ///
    /// ⚠ <b>Окно считается по времени БАЗЫ, а не процесса.</b> Два процесса с разошедшимися
    /// часами дали бы два разных предела на один адрес.
    /// </remarks>
    private async Task<bool> ЗаПределомAsync(
        string toAddress, string kind, int ratePerHour, CancellationToken cancellationToken)
    {
        var от = clock.GetUtcNow().AddHours(-1);

        var сколько = await db.Set<MailOutbox>()
            .CountAsync(
                // мутация 2026-08-29: добавил `письмо.SentAt != null` (счёт отправленных
                // вместо поставленных) → покраснели три факта QueueTests (правило 8).
                письмо => письмо.ToAddress == toAddress
                    && письмо.Kind == kind
                    && письмо.CreatedAt >= от,
                cancellationToken);

        return сколько >= ratePerHour;
    }
}
