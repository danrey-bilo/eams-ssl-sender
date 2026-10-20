using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Mail.Sending;

/// <summary>
/// Уборка отправленных писем.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Сносится ТОЛЬКО отправленное.</b> Непосланное с текстом ошибки остаётся навсегда:
/// это ответ администратору на вопрос «почему не дошло», и удалить его значит стереть
/// единственный след неудачи. Мусором оно не станет — писем, исчерпавших попытки, единицы;
/// если их много, это не повод чистить, а повод чинить почту.
///
/// ⚠ <b>Четырнадцать дней — срок из эталона.</b> Он отвечает на вопрос «мы точно отправляли?»
/// в те две недели, когда его задают; дальше ответ даёт журнал.
///
/// ⚠ <b>Единственное необратимое действие модуля.</b> Отсюда минутная отсрочка после старта:
/// процесс, поднявшийся с неверной настройкой и тут же снесённый, не должен успеть удалить
/// ничего.
///
/// ⚠ ⚠ <b>Проход — раз в час, а не раз в сутки</b>: он же стирает вложения писем, не ушедших за
/// <see cref="СрокВложений"/>. Закрытый ключ в очереди живёт не дольше суток с часом, даже если
/// почта выключена или сервер не отвечает. Удаление отправленного от частоты не меняется — оно
/// смотрит на свой срок.
/// </remarks>
internal sealed class SentMailCleanup(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<SentMailCleanup> log) : BackgroundService
{
    private static readonly TimeSpan Интервал = TimeSpan.FromHours(1);

    private static readonly TimeSpan ОтсрочкаСтарта = TimeSpan.FromMinutes(1);

    /// <summary>Сколько держим отправленное.</summary>
    internal static readonly TimeSpan Срок = TimeSpan.FromDays(14);

    /// <summary>
    /// Сколько вложения ждут отправки, прежде чем стереться.
    /// </summary>
    /// <remarks>
    /// ⚠ Сутки: насос с пятью попытками сдаётся меньше чем за час, и письмо, не ушедшее за сутки,
    /// ждёт уже не повтора, а починки (или включения) почты. Закрытому ключу столько ждать в базе
    /// незачем: после починки его отправят заново, по праву, действующему на тот день.
    /// </remarks>
    internal static readonly TimeSpan СрокВложений = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(ОтсрочкаСтарта, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var таймер = new PeriodicTimer(Интервал, clock);

        do
        {
            await УбратьAsync(stoppingToken);
        }
        while (await ЖдатьAsync(таймер, stoppingToken));
    }

    /// <summary>Один проход уборки. Возвращает, сколько строк снесено.</summary>
    public static async Task<int> RunAsync(
        SenderDbContext db, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var порог = clock.GetUtcNow() - Срок;

        return await db.Set<MailOutbox>()
            .Where(письмо => письмо.SentAt != null && письмо.SentAt < порог)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Стереть вложения мёртвых писем, которые насос не успел стереть сам.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Страховка, а не основной путь.</b> Насос стирает вложения на последней неудачной
    /// попытке; но процесс, упавший посреди отправки, сбой самой отметки или опущенный
    /// <c>mail.max_attempts</c> оставляют мёртвое письмо с запечатанным закрытым ключом — а
    /// мёртвые письма уборка не удаляет никогда. Стёртому письму срок ставится «никогда»: без
    /// файлов оно уходить не должно.
    /// </remarks>
    public static Task<int> ForgetDeadAttachmentsAsync(
        SenderDbContext db, int maxAttempts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.Set<MailOutbox>()
            .Where(письмо => письмо.SentAt == null && письмо.Attempts >= maxAttempts && письмо.SealedAttachments != null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(письмо => письмо.SealedAttachments, (byte[]?)null)
                    .SetProperty(письмо => письмо.NextAttemptAt, MailAttachments.Никогда),
                cancellationToken);
    }

    /// <summary>
    /// Стереть вложения писем, не ушедших за <see cref="СрокВложений"/>, и объявить их мёртвыми.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Насос их не сотрёт</b>: при выключенной или ненастроенной почте он не берёт ничего,
    /// а с поднятым <c>mail.max_attempts</c> пробует неделями. Закрытый ключ, поставленный в
    /// очередь, лежал бы в базе, пока кто-нибудь не починит почту, — и ушёл бы тогда по праву
    /// давно минувшего дня. Срок «никогда» — чтобы без файлов письмо не ушло; причина — чтобы
    /// администратор увидел, почему.
    /// </remarks>
    public static Task<int> ForgetStaleAttachmentsAsync(
        SenderDbContext db, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var порог = clock.GetUtcNow() - СрокВложений;
        const string причина =
            "Не отправлено за сутки: вложения стёрты, письмо не уйдёт. Почините почту и отправьте заново.";

        return db.Set<MailOutbox>()
            .Where(письмо => письмо.SentAt == null && письмо.SealedAttachments != null && письмо.CreatedAt < порог)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(письмо => письмо.SealedAttachments, (byte[]?)null)
                    .SetProperty(письмо => письмо.NextAttemptAt, MailAttachments.Никогда)
                    .SetProperty(
                        письмо => письмо.LastError,
                        письмо => письмо.LastError == null ? причина : причина + " Последняя ошибка: " + письмо.LastError),
                cancellationToken);
    }

    private async Task УбратьAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

            var снесено = await RunAsync(db, clock, cancellationToken);

            var политика = await MailPolicy.ReadAsync(
                scope.ServiceProvider.GetRequiredService<Shared.Contracts.Settings.ISettingsReader>(), cancellationToken);
            var стёрто = await ForgetDeadAttachmentsAsync(db, политика.MaxAttempts, cancellationToken);
            var просрочено = await ForgetStaleAttachmentsAsync(db, clock, cancellationToken);

            // ⚠ Пустой проход молчит: часовое расписание, пишущее «снесено 0», за год даёт
            // восемь тысяч строк ни о чём.
            if (снесено > 0 || стёрто > 0)
            {
                log.LogInformation("mail_cleanup {Removed} {AttachmentsForgotten}", снесено, стёрто);
            }

            if (просрочено > 0)
            {
                log.LogWarning("mail_attachments_expired {Letters}", просрочено);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Останов процесса.
        }
        catch (Exception ошибка)
        {
            log.LogError("mail_cleanup_failed {Error}", SafeError.Describe(ошибка));
        }
    }

    private static async Task<bool> ЖдатьAsync(PeriodicTimer таймер, CancellationToken stoppingToken)
    {
        try
        {
            return await таймер.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
