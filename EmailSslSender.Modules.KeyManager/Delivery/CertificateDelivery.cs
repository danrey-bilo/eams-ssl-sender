using System.Text;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Domain;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
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

    /// <summary>Ни одного сертификата подписки в Key Manager Plus нет.</summary>
    CertificateMissing,

    /// <summary>Key Manager Plus не отдал файл или пароль.</summary>
    KeyManagerFailed,

    /// <summary>Почта не настроена: письма некуда класть.</summary>
    MailNotConfigured,

    /// <summary>Что-то иное: база, шифрование.</summary>
    Failed,
}

/// <summary>Итог попытки и причина отправки, если она состоялась.</summary>
/// <param name="Outcome">Чем кончилось.</param>
/// <param name="Reason">Почему отправлено.</param>
/// <param name="Error">Почему не отправлено — или что отправлено не всё (каких сертификатов нет в Key Manager Plus).</param>
internal sealed record DeliveryResult(DeliveryOutcome Outcome, DeliveryReason? Reason = null, string? Error = null);

/// <summary>Сертификат подписки, найденный в снимке.</summary>
/// <param name="Name">Common Name, как он записан в подписке: ключ отметок отправленного.</param>
/// <param name="Certificate">Текущий сертификат из Key Manager Plus.</param>
internal sealed record Сертификат(string Name, KmpCertificate Certificate);

/// <summary>Сертификат и его выгруженные файлы — одна папка архива и одна строка письма.</summary>
internal sealed record CertificateFiles(KmpCertificate Certificate, IReadOnlyList<MailAttachment> Files);

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

    /// <summary>Даты выпуска (<c>NotBefore</c>); <c>null</c> — разобрать не удалось.</summary>
    public Dictionary<(string CommonName, string Serial), DateTimeOffset?> Issued { get; } = [];
}

/// <summary>
/// Отправка по одной подписке: найти текущие сертификаты, решить, пора ли и какие, выгрузить, поставить письма.
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
    AppDbContext db,
    IMailQueue mail,
    IAuditWriter audit,
    ISettingsReader settings,
    TimeProvider clock,
    ILogger<CertificateDelivery> log)
{
    /// <summary>Типы файлов, несущие закрытый ключ.</summary>
    internal static readonly IReadOnlySet<string> СКлючом =
        new HashSet<string>([CertificateFormats.Key, CertificateFormats.Pkcs, CertificateFormats.Jks], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Режим защиты SMTP — настройка модуля почты, прочитанная по имени.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ключ объявляет модуль почты (EAMS), а не этот модуль: здесь только чтение по тому же имени и с тем
    /// же умолчанием. Переименование в модуле почты ловит интеграционный тест справочника настроек
    /// (<c>KeyManagerApi</c>), иначе проверка молча читала бы умолчание.
    /// </remarks>
    internal static readonly SettingKey<string> SmtpSecurity = new("mail.smtp_security", "starttls", "SMTP security", "", "mail");

    /// <summary>
    /// Пора ли слать, почему и что. <c>null</c> — не пора.
    /// </summary>
    /// <param name="subscription">Подписка.</param>
    /// <param name="current">Текущие сертификаты подписки, найденные в снимке: имя из подписки и сертификат.</param>
    /// <param name="now">Сейчас.</param>
    /// <remarks>
    /// ⚠ ⚠ Решение владельца 2026-10-27: <b>по расписанию — все</b> сертификаты подписки, <b>при
    /// обновлении — только обновлённые</b>. Расписание проверяется первым: подошёл его срок — уходят все,
    /// обновлённые в том числе.
    ///
    /// ⚠ Серийные номера сравниваются без учёта регистра: это hex, и Key Manager Plus не обещает
    /// писать его одинаково в разных версиях.
    /// </remarks>
    internal static (DeliveryReason Reason, IReadOnlyList<Сертификат> Send)? Due(
        CertificateSubscription subscription, IReadOnlyList<Сертификат> current, DateTimeOffset now)
    {
        var первая = subscription.LastSentAt is null;

        if (subscription.IntervalDays is { } дней
            && (subscription.LastFullSentAt is not { } когда || now >= когда.AddDays(дней)))
        {
            return (первая ? DeliveryReason.Initial : DeliveryReason.Scheduled, current);
        }

        if (!subscription.OnChange)
        {
            return null;
        }

        var обновлённые = current
            .Where(с => !subscription.SentSerials.TryGetValue(с.Name, out var ушёл)
                || !string.Equals(ушёл, с.Certificate.SerialNumber, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return обновлённые.Count == 0
            ? null
            : (первая ? DeliveryReason.Initial : DeliveryReason.Changed, обновлённые);
    }

    /// <summary>
    /// Отправить подписку, если пора (или в любом случае, если <paramref name="manual"/>).
    /// </summary>
    /// <param name="id">Подписка.</param>
    /// <param name="client">Открытый клиент Key Manager Plus.</param>
    /// <param name="snapshot">Снимок сертификатов этого прохода.</param>
    /// <param name="manual">Ручная отправка: все сертификаты, без проверки срока и признака «включена».</param>
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
                ПочтаБезШифрования => (DeliveryOutcome.MailNotConfigured,
                    "Почта настроена без шифрования (mail.smtp_security = none): закрытый ключ и его пароль по ней не "
                    + "отправляются. Выберите tls или starttls в настройках почты или уберите из подписки KEY, PKCS, "
                    + "JKS и пароль ключа."),
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

        var найдено = подписка.CommonNames
            .Select(имя => (Имя: имя, Текущий: snapshot.Current(имя)))
            .ToList();

        var текущие = найдено
            .Where(н => н.Текущий is not null)
            .Select(н => new Сертификат(н.Имя, н.Текущий!))
            .ToList();

        var нет = найдено.Where(н => н.Текущий is null).Select(н => н.Имя).ToList();

        if (текущие.Count == 0)
        {
            var текст = $"{Перечень(нет)} нет в Key Manager Plus.";

            подписка.LastAttemptAt = сейчас;
            подписка.LastError = текст;
            await db.SaveChangesAsync(token);

            return new DeliveryResult(DeliveryOutcome.CertificateMissing, Error: текст);
        }

        // ⚠ ⚠ Защита от отката (ревью надёжности 2026-10-27): выпуск старее отправленного сам не уходит.
        var откаты = new List<string>();
        var проверены = new List<Сертификат>(текущие.Count);

        foreach (var сертификат in текущие)
        {
            if (await ПротивОткатаAsync(подписка, сертификат, snapshot, client, manual, cache, token) is { } годный)
            {
                проверены.Add(годный);
            }
            else
            {
                откаты.Add(
                    $"{сертификат.Name}: в Key Manager Plus только выпуск старее отправленного (серийный "
                    + $"{сертификат.Certificate.SerialNumber}) — сам не отправляется, отправьте вручную, если это верно");
            }
        }

        текущие = проверены;

        if (текущие.Count == 0)
        {
            var текст = Неполная(нет, [], откаты, остальныеУшли: false)!;

            подписка.LastAttemptAt = сейчас;
            подписка.LastError = текст;
            await db.SaveChangesAsync(token);

            return new DeliveryResult(DeliveryOutcome.CertificateMissing, Error: текст);
        }

        (DeliveryReason Reason, IReadOnlyList<Сертификат> Send)? срок =
            manual ? (DeliveryReason.Manual, текущие) : Due(подписка, текущие, сейчас);

        if (срок is not { } пора)
        {
            return new DeliveryResult(DeliveryOutcome.NotDue);
        }

        var (причина, отправить) = пора;

        // ⚠ Почта проверяется ДО выгрузки: иначе при ненастроенной почте каждый опрос выгружал бы
        // закрытые ключи из Key Manager Plus только затем, чтобы выбросить их.
        if (!await mail.CanSendAsync(token))
        {
            throw new ОтказПочты(MailQueueResult.NotConfigured);
        }

        // ⚠ ⚠ Закрытый ключ и его пароль — только по шифрованному соединению с сервером почты (ревью
        // безопасности 2026-10-27): при `none` их прочёл бы любой на пути до сервера. Сертификат без ключа
        // — открытые данные, ему можно.
        if ((подписка.IncludePassphrase || подписка.Formats.Any(СКлючом.Contains))
            && string.Equals(await settings.GetAsync(SmtpSecurity, token), "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new ПочтаБезШифрования();
        }

        var комплекты = new List<CertificateFiles>(отправить.Count);
        var ушедшие = new List<Сертификат>(отправить.Count);
        var невыгружены = new List<(string Name, KeyManagerException Error)>();

        foreach (var сертификат in отправить)
        {
            List<MailAttachment> файлы;

            // ⚠ ⚠ Отказ выгрузки одного сертификата — как его отсутствие в Key Manager Plus: остальные уходят,
            // его серийный не отмечается, и «при обновлении» попробует его снова (ревью надёжности
            // 2026-10-27). Прежде один сертификат без закрытого ключа в Key Manager Plus навсегда
            // останавливал всю подписку, а каждый опрос заново выгружал ключи остальных.
            try
            {
                файлы = await ВыгрузитьAsync(client, сертификат.Certificate, подписка.Formats, cache, token);

                // ⚠ Пароль ключа — файлом в архиве, а не строкой в теле: тело письма живёт в очереди
                // до уборки, а вложения стираются, как только письмо ушло.
                if (подписка.IncludePassphrase
                    && await ПарольAsync(client, сертификат.Certificate, cache, token) is { } пароль)
                {
                    файлы.Add(new MailAttachment(
                        CertificateMail.PassphraseFile, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(пароль + Environment.NewLine)));
                }
            }
            catch (KeyManagerException ошибка)
            {
                невыгружены.Add((сертификат.Name, ошибка));
                continue;
            }

            комплекты.Add(new CertificateFiles(сертификат.Certificate, файлы));
            ушедшие.Add(сертификат);
        }

        // Не выгрузился ни один — скорее всего, Key Manager Plus недоступен: отказ целиком, как прежде.
        if (комплекты.Count == 0)
        {
            throw new KeyManagerException(невыгружены[0].Error.Message, невыгружены[0].Error);
        }

        // ⚠ ⚠ Одним архивом ZIP на письмо (решение владельца): Outlook блокирует вложения .crt, .cer и .der;
        // несколько сертификатов — папка на каждый (разбор у CertificateArchive).
        var архив = CertificateArchive.Pack(комплекты, сейчас);
        var письмо = CertificateMail.Letter(комплекты, подписка.Formats, архив.FileName, подписка.Locale, сейчас);

        var номера = new List<Guid>(подписка.Recipients.Length);

        foreach (var адрес in подписка.Recipients)
        {
            var итог = await mail.EnqueueAsync(
                адрес, письмо.Kind, подписка.Locale, письмо.Values, письмо.Tables, [архив], cancellationToken: token);

            if (итог.Result != MailQueueResult.Queued || итог.Id is not { } номер)
            {
                throw new ОтказПочты(итог.Result);
            }

            номера.Add(номер);
        }

        // ⚠ ⚠ Прежние неотправленные письма подписки отменяются (ревью безопасности 2026-10-20):
        // новая отправка затирала их номера, и потом их уже не могла отменить ни смена
        // получателей, ни удаление подписки — закрытый ключ уходил тому, у кого право уже отняли.
        if (подписка.PendingMailIds is { Length: > 0 } прежние)
        {
            await mail.CancelAsync(прежние, "заменено новой отправкой", token);
        }

        var ушли = new Dictionary<string, string>(подписка.SentSerials, StringComparer.OrdinalIgnoreCase);
        var выпущены = new Dictionary<string, string>(подписка.SentIssued, StringComparer.OrdinalIgnoreCase);

        foreach (var сертификат in ушедшие)
        {
            ушли[сертификат.Name] = сертификат.Certificate.SerialNumber;

            // Дата выпуска ушедшего — для защиты от отката. Не разобрать — защиты по нему нет, а не отказ.
            if (await ВыпущенAsync(client, сертификат.Certificate, cache, token) is { } когда)
            {
                выпущены[сертификат.Name] = CertificateIssue.Format(когда);
            }
            else
            {
                выпущены.Remove(сертификат.Name);
            }
        }

        // Убранные из подписки имена не помнятся: вернись имя — его сертификат снова «новый».
        foreach (var лишнее in ушли.Keys.Where(имя => !подписка.CommonNames.Contains(имя, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            ушли.Remove(лишнее);
        }

        foreach (var лишнее in выпущены.Keys.Where(имя => !подписка.CommonNames.Contains(имя, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            выпущены.Remove(лишнее);
        }

        // Полная — все найденные пробовали отправить; не выгрузившиеся считаются как отсутствующие, иначе
        // расписание пересылало бы всю подписку каждый опрос.
        var полная = отправить.Count == текущие.Count;

        подписка.SentSerials = ушли;
        подписка.SentIssued = выпущены;
        подписка.LastSentAt = сейчас;
        подписка.LastFullSentAt = полная ? сейчас : подписка.LastFullSentAt;
        подписка.LastAttemptAt = сейчас;
        подписка.LastError = Неполная(нет, невыгружены, откаты);
        подписка.PendingMailIds = [.. номера];

        // Что несут эти письма — чтобы снять отметки, если не дойдёт ни одно (автоповтор у опроса).
        подписка.PendingSerials = new Dictionary<string, string>(
            ушедшие.ToDictionary(с => с.Name, с => с.Certificate.SerialNumber), StringComparer.OrdinalIgnoreCase);
        подписка.PendingFull = полная;

        // ⚠ В журнал — подписка, причина и какие сертификаты, без адресов и без содержимого: адрес
        // получателя — сведение о человеке, а строка уходит в общий лог (правило 14).
        audit.Write(new AuditEntry(
            KeyManagerActions.CertificateSent,
            ActorType: manual ? null : AuditActorType.System,
            ResourceType: KeyManagerActions.SubscriptionResource,
            ResourceId: подписка.Id,
            Reason: причина.ToString().ToLowerInvariant(),
            Changes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["certificates"] = ушедшие
                    .Select(с => $"{с.Certificate.CommonName} {с.Certificate.SerialNumber}")
                    .ToArray(),
                ["recipients"] = подписка.Recipients.Length,
            }));

        await db.SaveChangesAsync(token);

        return new DeliveryResult(DeliveryOutcome.Sent, причина, подписка.LastError);
    }

    /// <summary>Что из подписки не ушло и почему; <c>null</c> — ушло всё.</summary>
    private static string? Неполная(
        IReadOnlyList<string> нет,
        IReadOnlyList<(string Name, KeyManagerException Error)> невыгружены,
        IReadOnlyList<string> откаты,
        bool остальныеУшли = true)
    {
        var беды = new List<string>(невыгружены.Count + откаты.Count + 1);

        if (нет.Count > 0)
        {
            беды.Add($"{Перечень(нет)} нет в Key Manager Plus");
        }

        беды.AddRange(невыгружены.Select(н => $"{н.Name} не выгрузился: {н.Error.Message.TrimEnd('.')}"));
        беды.AddRange(откаты);

        return беды.Count == 0 ? null
            : string.Join("; ", беды) + (остальныеУшли ? " — отправлены остальные." : ".");
    }

    /// <summary>
    /// Текущий сертификат после защиты от отката; <c>null</c> — сам не отправляется.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ «Текущий — замеченный позже всех» верно, пока Key Manager Plus не вносит старые сертификаты, а он
    /// вносит: сканирование находит прежний сертификат на ещё не обновлённом сервере, повторный импорт.
    /// Такой выпуск отличается серийным номером от отправленного и ушёл бы «обновлением» (ревью надёжности
    /// 2026-10-27). Сравниваются даты выпуска (<see cref="CertificateIssue"/>): выпущенный раньше
    /// отправленного — откат. Отправленный ещё есть в Key Manager Plus — текущим остаётся он; нет —
    /// опрос не шлёт ничего и пишет причину, ручная отправка шлёт (это решение администратора).
    /// Даты отправленного нет (подписка до 2.2) или выпуск не разобрать — защиты нет, как прежде.
    /// </remarks>
    private async Task<Сертификат?> ПротивОткатаAsync(
        CertificateSubscription подписка,
        Сертификат кандидат,
        CertificateSnapshot snapshot,
        IKeyManagerClient client,
        bool manual,
        DownloadCache cache,
        CancellationToken token)
    {
        if (!подписка.SentSerials.TryGetValue(кандидат.Name, out var ушёл)
            || string.Equals(ушёл, кандидат.Certificate.SerialNumber, StringComparison.OrdinalIgnoreCase)
            || CertificateIssue.Parse(подписка.SentIssued.GetValueOrDefault(кандидат.Name)) is not { } выпущенОтправленный
            || await ВыпущенAsync(client, кандидат.Certificate, cache, token) is not { } выпущенКандидат
            || выпущенКандидат >= выпущенОтправленный)
        {
            return кандидат;
        }

        var отправленный = snapshot.Certificates.FirstOrDefault(с =>
            string.Equals(с.CommonName, кандидат.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(с.SerialNumber, ушёл, StringComparison.OrdinalIgnoreCase));

        log.LogWarning(
            "keymanager_certificate_rollback {Subscription} {CommonName} {Serial} {SentSerial} {Kept}",
            подписка.Id, кандидат.Name, кандидат.Certificate.SerialNumber, ушёл, отправленный is not null);

        return отправленный is not null ? new Сертификат(кандидат.Name, отправленный)
            : manual ? кандидат
            : null;
    }

    /// <summary>Дата выпуска сертификата — из его файла PEM; отказ Key Manager Plus — <c>null</c>, не ошибка.</summary>
    private static async Task<DateTimeOffset?> ВыпущенAsync(
        IKeyManagerClient client, KmpCertificate сертификат, DownloadCache cache, CancellationToken token)
    {
        var ключ = (сертификат.CommonName, сертификат.SerialNumber);

        if (cache.Issued.TryGetValue(ключ, out var когда))
        {
            return когда;
        }

        try
        {
            var файл = await ФайлAsync(client, сертификат, CertificateFormats.Pem, cache, token);
            когда = CertificateIssue.NotBefore(файл.Content, сертификат.SerialNumber);
        }
        catch (KeyManagerException)
        {
            когда = null;
        }

        cache.Issued[ключ] = когда;

        return когда;
    }

    private static async Task<KmpFile> ФайлAsync(
        IKeyManagerClient client, KmpCertificate сертификат, string формат, DownloadCache cache, CancellationToken token)
    {
        var ключ = (сертификат.CommonName, сертификат.SerialNumber, формат);

        if (!cache.Files.TryGetValue(ключ, out var файл))
        {
            файл = await client.DownloadAsync(сертификат.CommonName, сертификат.SerialNumber, формат, token);
            cache.Files[ключ] = файл;
        }

        return файл;
    }

    /// <summary>Имена через запятую: «Сертификата a» или «Сертификатов a, b».</summary>
    private static string Перечень(IReadOnlyList<string> имена) =>
        (имена.Count == 1 ? "Сертификата " : "Сертификатов ") + string.Join(", ", имена);

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
            var файл = await ФайлAsync(client, текущий, формат, cache, token);

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

    /// <summary>Почта без шифрования, а письмо несёт закрытый ключ или его пароль.</summary>
    private sealed class ПочтаБезШифрования() : Exception("mail.smtp_security = none");

    /// <summary>Очередь писем не приняла письмо — откатить всю отправку подписки.</summary>
    private sealed class ОтказПочты(MailQueueResult result) : Exception($"Очередь писем ответила {result}")
    {
        public MailQueueResult Result { get; } = result;
    }
}
