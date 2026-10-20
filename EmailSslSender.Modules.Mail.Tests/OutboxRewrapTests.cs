using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Modules.Mail.Secrets;
using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Modules.Mail.Settings;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Ротация мастер-ключа доходит до писем в очереди.
/// </summary>
/// <remarks>
/// ⚠ ⚠ Прежде <c>rewrap-keys</c> перекладывал секреты установки, ключ API и второй фактор — и не
/// трогал <c>mail_outbox.data_key</c>. Выведенная после такой ротации старая версия
/// мастер-ключа хоронила бы неотправленные письма (коды входа, ссылки сброса, сертификаты)
/// одно за другим.
/// </remarks>
[Collection(MailDatabaseCollection.Name)]
public sealed class OutboxRewrapTests(MailDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyDictionary<string, string> Пусто =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public ValueTask InitializeAsync()
    {
        база.Конверт.Вернуть();
        база.Настройки.Значения.Clear();
        база.Настройки.Значения[MailSettings.Enabled.Name] = true;
        база.Настройки.Значения[MailSettings.SmtpHost.Name] = "smtp.example.com";
        база.Настройки.Значения[MailSettings.FromAddress.Name] = "vault@example.com";
        база.Отправитель.Сброс();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        база.Конверт.Вернуть();

        await using var scope = база.Scope();
        await scope.ServiceProvider.GetRequiredService<SenderDbContext>()
            .Set<MailOutbox>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ротация_перекладывает_ключи_неотправленных_писем_порциями()
    {
        var ушло = await Поставить();
        await Разобрать();

        var ждут = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            ждут.Add(await Поставить(new MailAttachment("site.key", "application/x-pem-file", [1, 2, 3])));
        }

        var ключУшедшего = (await Письмо(ушло)).DataKey;

        база.Конверт.Повернуть();

        // Порцией по два: курсор обязан довести проход до конца, не потеряв и не повторив строку.
        var (просмотрено, переложено) = await Переложить(порция: 2);

        просмотрено.ShouldBe(3, "только неотправленные");
        переложено.ShouldBe(3);

        foreach (var адрес in ждут)
        {
            база.Конверт.NeedsRewrap(new WrappedDataKey((await Письмо(адрес)).DataKey))
                .ShouldBeFalse("обёртка письма осталась под старой версией");
        }

        (await Письмо(ушло)).DataKey.ShouldBe(ключУшедшего, "отправленное не открывается никем — его не трогают");

        // И главное: письма по-прежнему открываются и уходят — вместе с вложениями.
        await Разобрать();

        база.Отправитель.Ушедшие.Count.ShouldBe(4);
        база.Отправитель.Ушедшие.Skip(1).ShouldAllBe(п => п.Attachments!.Count == 1);

        // Повторный проход ничего не трогает: письма уже ушли, а ушедшее не перекладывается.
        (await Переложить(порция: 2)).Переложено.ShouldBe(0);
    }

    [Fact]
    public async Task Повторный_проход_ничего_не_перекладывает()
    {
        await Поставить();
        база.Конверт.Повернуть();

        (await Переложить(порция: 10)).Переложено.ShouldBe(1);

        var второй = await Переложить(порция: 10);
        второй.Просмотрено.ShouldBe(1);
        второй.Переложено.ShouldBe(0);
    }

    private async Task<(int Просмотрено, int Переложено)> Переложить(int порция)
    {
        int просмотрено = 0, переложено = 0;
        string? курсор = null;

        do
        {
            await using var scope = база.Scope();
            var цель = new MailOutboxRewrap(
                scope.ServiceProvider.GetRequiredService<SenderDbContext>(), база.Конверт);

            цель.Name.ShouldBe("mail_outbox");

            var итог = await цель.RewrapAsync(порция, курсор, Ct);
            просмотрено += итог.Seen;
            переложено += итог.Rewrapped;
            курсор = итог.Next;
        }
        while (курсор is not null);

        return (просмотрено, переложено);
    }

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

    private async Task Разобрать()
    {
        await using var scope = база.Scope();
        await scope.ServiceProvider.GetRequiredService<MailDelivery>().RunAsync(Ct);
    }

    private async Task<MailOutbox> Письмо(string адрес)
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<SenderDbContext>()
            .Set<MailOutbox>().AsNoTracking().SingleAsync(п => п.ToAddress == адрес, Ct);
    }
}
