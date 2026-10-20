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
/// </remarks>
internal sealed class SentMailCleanup(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<SentMailCleanup> log) : BackgroundService
{
    private static readonly TimeSpan Интервал = TimeSpan.FromHours(24);

    private static readonly TimeSpan ОтсрочкаСтарта = TimeSpan.FromMinutes(1);

    /// <summary>Сколько держим отправленное.</summary>
    internal static readonly TimeSpan Срок = TimeSpan.FromDays(14);

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

            // ⚠ Пустой проход молчит: суточное расписание, пишущее «снесено 0», за год даёт
            // триста шестьдесят пять строк ни о чём.
            if (снесено > 0 || стёрто > 0)
            {
                log.LogInformation("mail_cleanup {Removed} {AttachmentsForgotten}", снесено, стёрто);
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
