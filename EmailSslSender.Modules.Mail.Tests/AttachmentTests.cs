using System.Text;
using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Modules.Mail.Settings;
using EmailSslSender.Modules.Mail.Templates;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Вложения: запечатаны в очереди, доходят до отправителя, стираются после отправки.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Главный факт файла — вложения не переживают письмо.</b> В них ездит закрытый ключ
/// сертификата; строка письма живёт неделями, а мёртвая — вечно, и ключу там не место.
/// </remarks>
[Collection(MailDatabaseCollection.Name)]
public sealed class AttachmentTests(MailDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Байты, которых не должно быть в базе открытым текстом.</summary>
    private static readonly byte[] Ключ = Encoding.ASCII.GetBytes(
        "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQ\n-----END PRIVATE KEY-----\n");

    private static readonly IReadOnlyDictionary<string, string> Пусто =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public ValueTask InitializeAsync()
    {
        база.Настройки.Значения.Clear();
        база.Настройки.Значения[MailSettings.Enabled.Name] = true;
        база.Настройки.Значения[MailSettings.SmtpHost.Name] = "smtp.example.com";
        база.Настройки.Значения[MailSettings.FromAddress.Name] = "vault@example.com";
        база.Отправитель.Сброс();
        база.Логи.Строки.Clear();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        await db.Set<MailOutbox>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Вложения_лежат_запечатанными()
    {
        var адрес = await Поставить(new MailAttachment("site.key", "application/x-pem-file", Ключ));

        var письмо = await Письмо(адрес);

        письмо.SealedAttachments.ShouldNotBeNull();
        Содержит(письмо.SealedAttachments, Ключ).ShouldBeFalse("ключ не должен лежать в базе открытым");
        Содержит(письмо.SealedAttachments, Encoding.UTF8.GetBytes("site.key"))
            .ShouldBeFalse("имя файла тоже запечатано");
    }

    [Fact]
    public async Task Письмо_без_вложений_колонку_не_заполняет()
    {
        var адрес = await Поставить();

        (await Письмо(адрес)).SealedAttachments.ShouldBeNull();
    }

    [Fact]
    public async Task Вложения_доходят_до_отправителя_и_стираются_после_отправки()
    {
        var адрес = await Поставить(
            new MailAttachment("site.pem", "application/x-pem-file", [1, 2, 3]),
            new MailAttachment("site.key", "application/x-pem-file", Ключ));

        (await Разобрать()).ShouldBe(1);

        var ушло = база.Отправитель.Ушедшие.Single();
        ушло.Attachments.ShouldNotBeNull();
        ушло.Attachments.Select(ф => ф.FileName).ShouldBe(["site.pem", "site.key"]);
        ушло.Attachments[1].Content.ShouldBe(Ключ);

        var письмо = await Письмо(адрес);
        письмо.SentAt.ShouldNotBeNull();
        письмо.SealedAttachments.ShouldBeNull("ушедшему письму вложения больше не нужны");
        письмо.SealedBody.ShouldNotBeEmpty("тело остаётся до уборки — по нему разбирают жалобы");
    }

    [Fact]
    public async Task Последняя_неудачная_попытка_стирает_вложения_а_промежуточная_нет()
    {
        база.Настройки.Значения[MailSettings.MaxAttempts.Name] = 2;

        var адрес = await Поставить(new MailAttachment("site.key", "application/x-pem-file", Ключ));
        база.Отправитель.ПадатьНа = адрес;

        await Разобрать();
        (await Письмо(адрес)).SealedAttachments.ShouldNotBeNull("будет ещё попытка — вложения нужны ей");

        база.Часы.Вперёд(TimeSpan.FromHours(1));
        await Разобрать();

        var письмо = await Письмо(адрес);
        письмо.Attempts.ShouldBe(2);
        письмо.SentAt.ShouldBeNull();
        письмо.LastError.ShouldNotBeNullOrWhiteSpace();
        письмо.SealedAttachments.ShouldBeNull("мёртвое письмо живёт вечно, ключу в нём не место");

        // Поднятый предел не возвращает письмо в работу: без файлов оно уйти не должно.
        база.Настройки.Значения[MailSettings.MaxAttempts.Name] = 10;
        база.Часы.Вперёд(TimeSpan.FromDays(1));
        await Разобрать();

        (await Письмо(адрес)).Attempts.ShouldBe(2);
        база.Отправитель.Ушедшие.ShouldBeEmpty();
    }

    [Fact]
    public async Task Уборка_стирает_вложения_мёртвых_писем_которые_насос_не_стёр()
    {
        // Процесс упал посреди отправки или предел опустили: письмо мёртвое, а ключ в нём остался.
        var адрес = await Поставить(new MailAttachment("site.key", "application/x-pem-file", Ключ));

        await using (var scope = база.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
            await db.Set<MailOutbox>().Where(п => п.ToAddress == адрес)
                .ExecuteUpdateAsync(set => set.SetProperty(п => п.Attempts, 5), Ct);

            (await SentMailCleanup.ForgetDeadAttachmentsAsync(db, maxAttempts: 5, Ct)).ShouldBe(1);
        }

        var письмо = await Письмо(адрес);
        письмо.SealedAttachments.ShouldBeNull();
        письмо.NextAttemptAt.ShouldBe(MailAttachments.Никогда);
    }

    // --- отмена и срок ----------------------------------------------------------------------

    [Fact]
    public async Task Отмена_стирает_вложения_и_хоронит_ждущее_письмо()
    {
        var (адрес, номер) = await ПоставитьСНомером(new MailAttachment("site.key", "application/x-pem-file", Ключ));

        await using (var scope = база.Scope())
        {
            (await scope.ServiceProvider.GetRequiredService<IMailQueue>()
                    .CancelAsync([номер], "подписка удалена", Ct))
                .ShouldBe(1);
        }

        var письмо = await Письмо(адрес);
        письмо.SealedAttachments.ShouldBeNull("закрытому ключу отменённого письма в базе не место");
        письмо.NextAttemptAt.ShouldBe(MailAttachments.Никогда);
        письмо.LastError.ShouldBe("Отменено: подписка удалена");

        await using (var scope = база.Scope())
        {
            var состояние = await scope.ServiceProvider.GetRequiredService<IMailQueue>().StatusAsync([номер], Ct);
            состояние[номер].State.ShouldBe(MailLetterState.Dead);
        }

        // Ни сразу, ни через сутки с поднятым пределом попыток насос его не возьмёт.
        база.Настройки.Значения[MailSettings.MaxAttempts.Name] = 50;
        база.Часы.Вперёд(TimeSpan.FromDays(1));
        await Разобрать();

        база.Отправитель.Ушедшие.ShouldBeEmpty();
        (await Письмо(адрес)).Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Отмена_не_трогает_ушедшее_и_мёртвое()
    {
        var (ушло, номерУшло) = await ПоставитьСНомером(new MailAttachment("site.key", "application/x-pem-file", Ключ));
        await Разобрать();

        база.Настройки.Значения[MailSettings.MaxAttempts.Name] = 1;
        var (умерло, номерУмерло) = await ПоставитьСНомером(new MailAttachment("site.key", "application/x-pem-file", Ключ));
        база.Отправитель.ПадатьНа = умерло;
        await Разобрать();

        var причина = (await Письмо(умерло)).LastError;
        причина.ShouldNotBeNullOrWhiteSpace();

        await using (var scope = база.Scope())
        {
            (await scope.ServiceProvider.GetRequiredService<IMailQueue>()
                    .CancelAsync([номерУшло, номерУмерло, Guid.NewGuid()], "подписка удалена", Ct))
                .ShouldBe(0);
        }

        var отправленное = await Письмо(ушло);
        отправленное.SentAt.ShouldNotBeNull();
        отправленное.LastError.ShouldBeNull();

        (await Письмо(умерло)).LastError.ShouldBe(причина, "у мёртвого своя причина, её не затирают");
    }

    [Fact]
    public async Task Отмена_откатывается_вместе_с_транзакцией_вызывающего()
    {
        var (адрес, номер) = await ПоставитьСНомером(new MailAttachment("site.key", "application/x-pem-file", Ключ));

        await using (var scope = база.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

            await using var транзакция = await db.Database.BeginTransactionAsync(Ct);
            (await scope.ServiceProvider.GetRequiredService<IMailQueue>().CancelAsync([номер], "проверка", Ct))
                .ShouldBe(1);
            await транзакция.RollbackAsync(Ct);
        }

        var письмо = await Письмо(адрес);
        письмо.SealedAttachments.ShouldNotBeNull("откат действия возвращает письмо в очередь");
        письмо.NextAttemptAt.ShouldBeLessThan(MailAttachments.Никогда);
        письмо.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Вложения_не_ушедшего_за_сутки_письма_стираются_и_письмо_умирает()
    {
        // ⚠ Почту выключили после постановки — насос не берёт ничего, и закрытый ключ лежал бы в
        // очереди, пока её не включат.
        var (застряло, номер) = await ПоставитьСНомером(new MailAttachment("site.key", "application/x-pem-file", Ключ));
        var безВложений = await Поставить();
        база.Настройки.Значения[MailSettings.Enabled.Name] = false;

        база.Часы.Вперёд(TimeSpan.FromHours(23));
        (await Просрочить()).ShouldBe(0, "сутки ещё не прошли");

        база.Часы.Вперёд(TimeSpan.FromHours(2));
        (await Просрочить()).ShouldBe(1);

        var письмо = await Письмо(застряло);
        письмо.SealedAttachments.ShouldBeNull();
        письмо.NextAttemptAt.ShouldBe(MailAttachments.Никогда);
        письмо.LastError.ShouldNotBeNull().ShouldContain("за сутки");

        var простое = await Письмо(безВложений);
        простое.NextAttemptAt.ShouldBeLessThan(MailAttachments.Никогда, "письмо без вложений сроком не задето");
        простое.LastError.ShouldBeNull();

        await using (var scope = база.Scope())
        {
            (await scope.ServiceProvider.GetRequiredService<IMailQueue>().StatusAsync([номер], Ct))[номер]
                .State.ShouldBe(MailLetterState.Dead);
        }

        // Включили почту — ушло только письмо без вложений.
        база.Настройки.Значения[MailSettings.Enabled.Name] = true;
        await Разобрать();

        база.Отправитель.Ушедшие.Select(п => п.ToAddress).ShouldBe([безВложений]);
    }

    [Fact]
    public async Task Срок_вложений_дописывает_прежнюю_причину()
    {
        var (адрес, _) = await ПоставитьСНомером(new MailAttachment("site.key", "application/x-pem-file", Ключ));
        база.Отправитель.ПадатьНа = адрес;
        база.Настройки.Значения[MailSettings.MaxAttempts.Name] = 50;
        await Разобрать();

        база.Часы.Вперёд(TimeSpan.FromDays(2));
        (await Просрочить()).ShouldBe(1);

        var причина = (await Письмо(адрес)).LastError.ShouldNotBeNull();
        причина.ShouldContain("за сутки");
        причина.ShouldContain("Последняя ошибка");
    }

    [Fact]
    public async Task Состояние_писем_по_номерам()
    {
        база.Настройки.Значения[MailSettings.MaxAttempts.Name] = 1;

        Guid ушло, умерло, ждёт;
        await using (var scope = база.Scope())
        {
            var очередь = scope.ServiceProvider.GetRequiredService<IMailQueue>();
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

            ушло = (await очередь.EnqueueAsync("ok@example.com", ПисьмаБезПредела.Вид, MailLocales.Ru, Пусто, [], Ct)).Id!.Value;
            умерло = (await очередь.EnqueueAsync("bad@example.com", ПисьмаБезПредела.Вид, MailLocales.Ru, Пусто, [], Ct)).Id!.Value;
            await db.SaveChangesAsync(Ct);
        }

        база.Отправитель.ПадатьНа = "bad@example.com";
        await Разобрать();

        await using (var scope = база.Scope())
        {
            var очередь = scope.ServiceProvider.GetRequiredService<IMailQueue>();
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
            ждёт = (await очередь.EnqueueAsync("later@example.com", ПисьмаБезПредела.Вид, MailLocales.Ru, Пусто, [], Ct)).Id!.Value;
            await db.SaveChangesAsync(Ct);

            var состояние = await очередь.StatusAsync([ушло, умерло, ждёт, Guid.NewGuid()], Ct);

            состояние.Count.ShouldBe(3, "неизвестный номер — удалённое уборкой, его в ответе нет");
            состояние[ушло].State.ShouldBe(MailLetterState.Sent);
            состояние[умерло].State.ShouldBe(MailLetterState.Dead);
            состояние[умерло].ToAddress.ShouldBe("bad@example.com");
            состояние[умерло].LastError.ShouldNotBeNullOrWhiteSpace();
            состояние[ждёт].State.ShouldBe(MailLetterState.Pending);
        }
    }

    [Fact]
    public async Task Путь_в_имени_файла_отрезается()
    {
        await Поставить(new MailAttachment("../../etc/passwd", "text/plain", [1]));
        await Поставить(new MailAttachment(@"C:\Windows\evil.bat", "нечто", [1]));

        await Разобрать();

        var имена = база.Отправитель.Ушедшие.SelectMany(п => п.Attachments!).ToList();
        имена.Select(ф => ф.FileName).Order(StringComparer.Ordinal).ShouldBe(["evil.bat", "passwd"]);
        имена.Single(ф => ф.FileName == "evil.bat").ContentType.ShouldBe("application/octet-stream");
    }

    [Fact]
    public async Task Вид_без_предела_не_упирается_в_предел_а_обычный_упирается()
    {
        база.Настройки.Значения[MailSettings.RatePerHour.Name] = 1;
        var адрес = $"кому-{Guid.NewGuid():N}@example.com";

        await using var scope = база.Scope();
        var очередь = scope.ServiceProvider.GetRequiredService<IMailQueue>();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        for (var раз = 0; раз < 3; раз++)
        {
            (await очередь.EnqueueAsync(адрес, ПисьмаБезПредела.Вид, MailLocales.Ru, Пусто, [], Ct))
                .Result.ShouldBe(MailQueueResult.Queued);
            await db.SaveChangesAsync(Ct);
        }

        (await очередь.EnqueueAsync(адрес, MailKinds.Test, MailLocales.Ru, Пусто, Ct)).ShouldBe(MailQueueResult.Queued);
        await db.SaveChangesAsync(Ct);
        (await очередь.EnqueueAsync(адрес, MailKinds.Test, MailLocales.Ru, Пусто, Ct)).ShouldBe(MailQueueResult.RateLimited);
    }

    [Fact]
    public void Снятие_предела_с_необъявленного_вида_роняет_справочник()
    {
        var ошибка = Should.Throw<InvalidOperationException>(
            () => new MailTemplates([new MailOwnTemplates(), new ОпечаткаВВиде()]));

        ошибка.Message.ShouldContain("нет-такого-вида");
    }

    [Fact]
    public void Письмо_с_вложениями_собирается_смесью_частей()
    {
        var сообщение = SmtpSession.СобратьДляПроверки(
            Политика(),
            new OutgoingMail(
                "кому@example.com",
                "тема",
                "тело",
                [new MailAttachment("site.pfx", "application/x-pkcs12", [9, 8, 7])]));

        var смесь = сообщение.Body.ShouldBeOfType<Multipart>();
        смесь.ContentType.MimeType.ShouldBe("multipart/mixed");
        смесь[0].ShouldBeOfType<TextPart>().Text.ShouldBe("тело");

        var файл = смесь[1].ShouldBeOfType<MimePart>();
        файл.FileName.ShouldBe("site.pfx");
        файл.ContentType.MimeType.ShouldBe("application/x-pkcs12");
        файл.ContentDisposition.ShouldNotBeNull().Disposition.ShouldBe(ContentDisposition.Attachment);

        using var байты = new MemoryStream();
        файл.Content.ShouldNotBeNull().DecodeTo(байты, Ct);
        байты.ToArray().ShouldBe(new byte[] { 9, 8, 7 });
    }

    [Fact]
    public void Письмо_без_вложений_остаётся_простым_текстом()
    {
        var сообщение = SmtpSession.СобратьДляПроверки(
            Политика(), new OutgoingMail("кому@example.com", "тема", "тело"));

        сообщение.Body.ShouldBeOfType<TextPart>();
    }

    [Fact]
    public void Строки_тела_уходят_с_CRLF_а_не_голым_LF()
    {
        // Голый LF внутри base64 почтовые клиенты не считают переводом строки и склеивают письмо.
        var сообщение = SmtpSession.СобратьДляПроверки(
            Политика(), new OutgoingMail("кому@example.com", "тема", "Сертификат: a\nДействует до: b\r\nФайлы: c\n"));

        using var байты = new MemoryStream();
        сообщение.Body.ShouldBeOfType<TextPart>().Content.ShouldNotBeNull().DecodeTo(байты, Ct);

        System.Text.Encoding.UTF8.GetString(байты.ToArray())
            .ShouldBe("Сертификат: a\r\nДействует до: b\r\nФайлы: c\r\n");
    }

    private static MailPolicy Политика() => new(
        Enabled: true,
        Host: "smtp.example.com",
        Port: 25,
        Security: "none",
        Username: null,
        FromName: "Email SSL Sender",
        FromAddress: "vault@example.com",
        RatePerHour: 5,
        MaxAttempts: 5);

    private static bool Содержит(byte[] где, byte[] что) => где.AsSpan().IndexOf(что) >= 0;

    private async Task<string> Поставить(params MailAttachment[] вложения)
    {
        var адрес = $"кому-{Guid.NewGuid():N}@example.com";

        await using var scope = база.Scope();
        var очередь = scope.ServiceProvider.GetRequiredService<IMailQueue>();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        (await очередь.EnqueueAsync(адрес, ПисьмаБезПредела.Вид, MailLocales.Ru, Пусто, вложения, Ct))
            .Result.ShouldBe(MailQueueResult.Queued);

        await db.SaveChangesAsync(Ct);

        return адрес;
    }

    private async Task<(string Адрес, Guid Номер)> ПоставитьСНомером(params MailAttachment[] вложения)
    {
        var адрес = $"кому-{Guid.NewGuid():N}@example.com";

        await using var scope = база.Scope();
        var очередь = scope.ServiceProvider.GetRequiredService<IMailQueue>();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        var итог = await очередь.EnqueueAsync(адрес, ПисьмаБезПредела.Вид, MailLocales.Ru, Пусто, вложения, Ct);
        итог.Result.ShouldBe(MailQueueResult.Queued);

        await db.SaveChangesAsync(Ct);

        return (адрес, итог.Id!.Value);
    }

    private async Task<int> Просрочить()
    {
        await using var scope = база.Scope();

        return await SentMailCleanup.ForgetStaleAttachmentsAsync(
            scope.ServiceProvider.GetRequiredService<SenderDbContext>(), база.Часы, Ct);
    }

    private async Task<int> Разобрать()
    {
        await using var scope = база.Scope();

        return (await scope.ServiceProvider.GetRequiredService<MailDelivery>().RunAsync(Ct)).Sent;
    }

    private async Task<MailOutbox> Письмо(string адрес)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Set<MailOutbox>().AsNoTracking().SingleAsync(п => п.ToAddress == адрес, Ct);
    }

    /// <summary>Снимает предел с вида, которого никто не объявлял.</summary>
    private sealed class ОпечаткаВВиде : IMailTemplateProvider
    {
        public IReadOnlyList<MailTemplate> Templates => [];

        public IReadOnlyCollection<string> UnthrottledKinds => ["нет-такого-вида"];
    }
}

/// <summary>
/// Вид письма с вложениями, снявший с себя предел частоты, — как доставка сертификатов.
/// </summary>
internal sealed class ПисьмаБезПредела : IMailTemplateProvider
{
    public const string Вид = "test_unthrottled";

    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(Вид, MailLocales.Ru, "Файлы", "Файлы во вложении."),
        new(Вид, MailLocales.Uk, "Файли", "Файли у вкладенні."),
        new(Вид, MailLocales.En, "Files", "Files attached."),
    ];

    public IReadOnlyCollection<string> UnthrottledKinds => [Вид];
}
