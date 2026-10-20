using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Mail.Sending;

/// <summary>
/// Разбор очереди: захват порции, отправка, отметка исхода.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Захват порции АТОМАРЕН одним запросом.</b> <c>UPDATE … WHERE id IN (SELECT … FOR
/// UPDATE SKIP LOCKED) … RETURNING</c>: он сразу увеличивает число попыток и отодвигает
/// следующую. Двойной выдачи не будет именно поэтому — письмо помечено взятым ДО того, как
/// начнётся отправка, и второй насос его уже не увидит.
///
/// ⚠ <b><c>SKIP LOCKED</c> стережёт при этом НЕ двойную выдачу, и сказать это надо прямо.</b>
/// Мутация «убрать его» пережила два захода тестов (2026-08-29). Причина в устройстве: захват —
/// отдельный самофиксирующийся запрос, блокировки снимаются к началу отправки, и окно
/// составляет миллисекунды между двумя одновременными <c>UPDATE</c>. Без <c>SKIP LOCKED</c> в
/// этом окне второй процесс <b>ждёт</b> блокировку, а не берёт чужое: в READ COMMITTED
/// PostgreSQL перечитывает условие после её снятия. То есть это про пропускную способность, а
/// не про правильность, — и детерминированного теста на него нет.
///
/// ⚠ <b>Попытка увеличивается ПРИ ЗАХВАТЕ, а не после отправки.</b> Процесс, упавший посреди
/// отправки, иначе оставил бы письмо с прежним счётчиком — и оно пробовалось бы вечно.
///
/// ⚠ ⚠ <b><c>try</c> ВНУТРИ цикла, а не вокруг него.</b> Один упавший адрес не должен уносить
/// пачку. Этот самый дефект уже ловили в рассылке сообщений 2026-08-16: общий <c>except</c>
/// вокруг цикла терял все кадры разом, а выглядело это как «иногда не доходит».
/// </remarks>
/// <summary>
/// Итог одного прохода насоса: сколько взято и сколько ушло.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Две величины, а не одна, и разница несущая (этап 60.2).</b> «Ушло ноль» бывает и когда
/// очередь пуста, и когда двадцать писем подряд отказали. Насосу это разные вещи: в первом случае
/// можно разредить ход, во втором работа есть и сервер просто не отвечает — разреживать нельзя,
/// иначе письмо, которое вот-вот пройдёт, будет ждать минуту сверх того.
/// </remarks>
/// <param name="Taken">Сколько строк захвачено из очереди.</param>
/// <param name="Sent">Сколько из них ушло.</param>
internal readonly record struct MailRun(int Taken, int Sent);

internal sealed class MailDelivery(
    SenderDbContext db,
    IMailSender sender,
    ISettingsReader settings,
    ISecretEnvelope envelope,
    TimeProvider clock,
    ILogger<MailDelivery> log)
{
    /// <summary>Сколько писем берём за проход.</summary>
    internal const int Порция = 20;

    /// <summary>Контекст AEAD тела письма — тот же, что при постановке.</summary>
    private const string Контекст = "sender:mail-body";

    /// <summary>
    /// Захват порции: увеличить попытку, отодвинуть следующую и вернуть взятое.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Пауза растёт КВАДРАТОМ попытки: 1, 4, 9, 16, 25 минут.</b> Не удвоением: у пяти
    /// попыток квадрат даёт чуть больше часа, то есть починенный сервер разбирает очередь в тот
    /// же день; удвоение с той же пятой попытки уводит письмо за сутки, и человек, ждущий кода
    /// входа, не дождётся его вовсе.
    /// </remarks>
    /// <remarks>
    /// ⚠ ⚠ <b>«Сейчас» приходит ПАРАМЕТРОМ, а не берётся из <c>now()</c> базы</b> — и это не
    /// отступление от «сроки сравнивай там же, где хранишь», а его исполнение. Важен не
    /// источник времени, а его <b>единственность</b>: строка пишется одним «сейчас» и читается
    /// им же. Первая редакция писала <c>now()</c> базы, а предел частоты считала от
    /// <see cref="TimeProvider"/> — два часовых пояса на одну таблицу, и поведение зависело от
    /// того, чьи часы спешат.
    ///
    /// В бою это одни и те же часы: <c>api</c>, <c>worker</c> и <c>postgres</c> живут на одной
    /// машине одного <c>compose</c>. В тестах — управляемые, и без этого «попытка растёт при
    /// захвате» не проверить вовсе.
    /// </remarks>
    private const string ЗахватSql = """
        UPDATE mail_outbox
        SET attempts = attempts + 1,
            next_attempt_at = {2} + make_interval(mins => (attempts + 1) * (attempts + 1))
        WHERE id IN (
            SELECT id FROM mail_outbox
            WHERE sent_at IS NULL AND attempts < {0} AND next_attempt_at <= {2}
            ORDER BY next_attempt_at
            LIMIT {1}
            FOR UPDATE SKIP LOCKED
        )
        RETURNING id, to_address, subject, body, data_key, attachments, kind, created_at,
                  next_attempt_at, attempts, sent_at, last_error
        """;

    /// <summary>Разобрать очередную порцию.</summary>
    /// <remarks>
    /// ⚠ ⚠ Возвращает <b>две</b> величины, а не одну (этап 60.2). «Ушло ноль» бывает и когда
    /// очередь пуста, и когда двадцать писем подряд отказали, — а насосу это разные вещи:
    /// первое означает «можно поспать подольше», второе — «сервер не отвечает, но работа есть».
    /// </remarks>
    public async Task<MailRun> RunAsync(CancellationToken cancellationToken = default)
    {
        var политика = await MailPolicy.ReadAsync(settings, cancellationToken);

        if (!политика.Configured)
        {
            // ⚠ Молча: насос ходит раз в пятнадцать секунд, и строка «почта не настроена»
            // залила бы лог собой у всякой установки, которая почтой не пользуется.
            return default;
        }

        var порция = await db.Set<MailOutbox>()
            .FromSqlRaw(ЗахватSql, политика.MaxAttempts, Порция, clock.GetUtcNow())
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (порция.Count == 0)
        {
            // ⚠ Пустой прогон МОЛЧИТ. Пятнадцатисекундное расписание, пишущее строку в лог,
            // заливает лог собой — и в нём тонет всё, ради чего лог читают.
            return default;
        }

        var ушло = 0;

        // ⚠ ⚠ ⚠ ОДНО СОЕДИНЕНИЕ НА ПОРЦИЮ (этап 60.2). Прежде каждое письмо платило полный
        // обряд: `Connect` + TLS + `Auth` + `Send` + `Quit`. На порции из двадцати это двадцать
        // рукопожатий вместо одного, половина времени порции — и прямой повод для реле счесть
        // нас перебирающим пароли.
        //
        // ⚠ ⚠ `Open` не подключается: соединение поднимается при первой отправке, внутри
        // `try` конкретного письма. Подключись оно здесь — недоступный сервер уронил бы всю
        // порцию одним броском мимо цикла, и ни одному письму не досталось бы ни попытки в
        // счётчике, ни строки с причиной.
        await using var сессия = sender.Open(политика);

        foreach (var письмо in порция)
        {
            // ⚠ Каждое письмо в СВОЕЙ попытке: одно упавшее не уносит пачку.
            try
            {
                // Останов пришёл между письмами — следующее не начинаем: оно останется в очереди.
                cancellationToken.ThrowIfCancellationRequested();

                var ключ = new WrappedDataKey(письмо.DataKey);
                var тело = envelope.OpenText(ключ, new SealedSecret(письмо.SealedBody), Контекст);
                var вложения = MailAttachments.Open(envelope, ключ, письмо.SealedAttachments);

                await сессия.SendAsync(
                    new OutgoingMail(письмо.ToAddress, письмо.Subject, тело, вложения), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Останов процесса — не неудача письма: оно останется в очереди как есть.
                throw;
            }
            catch (Exception ошибка)
            {
                // ⚠ В строку и в лог уходит ОПИСАНИЕ, а не исключение объектом: текст ошибки
                // SMTP может содержать адрес и учётную запись (правило 14, `SafeError`).
                var описание = SafeError.Describe(ошибка);

                log.LogWarning(
                    "mail_attempt_failed {Kind} {Attempt} {Error}",
                    письмо.Kind,
                    письмо.Attempts,
                    описание);

                await ОтметитьНеудачуAsync(
                    письмо.Id, описание, письмо.Attempts >= политика.MaxAttempts, CancellationToken.None);

                continue;
            }

            // ⚠ ⚠ Отметка об успехе — ОТДЕЛЬНО от отправки и БЕЗ токена отмены (ревью
            // 2026-09-15, mail-2). Реле письмо уже приняло. С токеном останов в эти миллисекунды
            // отменял отметку, `sent_at` оставался пустым, и через attempts² минут письмо уходило
            // ВТОРОЙ раз. А сбой отметки внутри общего перехвата вёл в «неудачу», и в `last_error`
            // доставленного письма ложилась ошибка базы.
            //
            // ⚠ Окно этим СУЖЕНО, а не закрыто: отмена, пришедшая после DATA и до ответа 250, даёт
            // исключение у уже принятого письма. Доставка — «хотя бы раз», и обещать «ровно раз»
            // здесь нечем.
            //
            // мутация 2026-09-15: вернул токен отмены в отметку → покраснел
            // DeliveryTests.Останов_сразу_после_отправки_не_теряет_отметку.
            try
            {
                await ОтметитьУспехAsync(письмо.Id, CancellationToken.None);
                ушло++;
            }
            catch (Exception ошибка)
            {
                log.LogError("mail_sent_mark_failed {Kind} {Error}", письмо.Kind, SafeError.Describe(ошибка));
            }
        }

        log.LogInformation("mail_delivered {Sent} {Taken}", ушло, порция.Count);

        return new MailRun(порция.Count, ушло);
    }

    /// <summary>Отметить успех и стереть вложения: письмо ушло, хранить их больше незачем.</summary>
    private async Task ОтметитьУспехAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<MailOutbox>()
            .Where(письмо => письмо.Id == id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(письмо => письмо.SentAt, clock.GetUtcNow())
                    .SetProperty(письмо => письмо.LastError, (string?)null)
                    .SetProperty(письмо => письмо.SealedAttachments, (byte[]?)null),
                cancellationToken);

    /// <summary>
    /// Отметить неудачу.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Исчерпавшее попытки письмо НЕ УДАЛЯЕТСЯ.</b> Оно остаётся непосланным с текстом
    /// ошибки: это ответ администратору на вопрос «почему не дошло», а не мусор. В следующую
    /// выборку оно не попадёт — условие захвата смотрит на число попыток.
    ///
    /// ⚠ Сбой самой отметки глотается с логом (ревью 2026-09-15, mail-2): прежде он выходил из
    /// цикла и уносил порцию — остальные письма уже захвачены с attempts+1 и в этом проходе не
    /// уходили вовсе. Вызывающий передаёт `CancellationToken.None` по той же причине, что у успеха: попытка уже
    /// потрачена, и строка с причиной — ответ администратору.
    ///
    /// ⚠ <b>Последняя попытка стирает вложения.</b> Мёртвое письмо остаётся навсегда, а уборка его не
    /// трогает; закрытый ключ сертификата лежал бы в нём вечно.
    /// </remarks>
    private async Task ОтметитьНеудачуAsync(
        Guid id, string описание, bool последняя, CancellationToken cancellationToken)
    {
        try
        {
            await db.Set<MailOutbox>()
                .Where(письмо => письмо.Id == id)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(письмо => письмо.LastError, описание)
                        .SetProperty(
                            письмо => письмо.NextAttemptAt,
                            письмо => последняя && письмо.SealedAttachments != null
                                ? MailAttachments.Никогда
                                : письмо.NextAttemptAt)
                        .SetProperty(
                            письмо => письмо.SealedAttachments,
                            письмо => последняя ? null : письмо.SealedAttachments),
                    cancellationToken);
        }
        catch (Exception ошибка)
        {
            log.LogError("mail_failure_mark_failed {Error}", SafeError.Describe(ошибка));
        }
    }
}
