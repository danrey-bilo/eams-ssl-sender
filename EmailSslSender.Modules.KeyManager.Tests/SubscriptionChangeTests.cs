using System.Text.Json;
using EmailSslSender.Modules.KeyManager.Api;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.KeyManager.Tests;

/// <summary>
/// Правка и удаление подписки: письма в очереди не переживают права получателя, а журнал
/// говорит, что изменилось, — без адресов.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Главный факт файла — убранный получатель не получает ключ, уже стоящий в очереди.</b>
/// Право проверяется при постановке письма, а уходит оно позже: почта лежит, сервер не отвечает
/// — и всё это время закрытый ключ ждёт отправки тому, у кого право уже отняли.
/// </remarks>
[Collection(KeyManagerDatabaseCollection.Name)]
public sealed class SubscriptionChangeTests(KeyManagerDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        база.Сброс();
        база.Kmp.Сертификаты.Add(new KmpCertificate("site.example.com", "aa01", new DateOnly(2027, 1, 1)));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        await db.Set<CertificateSubscription>().ExecuteDeleteAsync(CancellationToken.None);
        await db.Set<KnownCertificate>().ExecuteDeleteAsync(CancellationToken.None);
        await db.Set<KeyManagerState>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Убранный_получатель_не_получит_ключ_из_очереди_а_остальные_получат()
    {
        var id = await Создать("a@example.com", "b@example.com", "c@example.com");
        await Опросить();

        var номера = база.Почта.Номера.ToList();
        номера.Count.ShouldBe(3);

        // «C@…» — тот же адрес в другом регистре: его письмо не отменяется.
        await Изменить(id, new SubscriptionIn { Recipients = ["a@example.com", "C@example.com"] });

        база.Почта.Отменённые.ShouldBe([номера[1]]);

        var судьбы = await Судьбы(номера);
        судьбы[номера[1]].State.ShouldBe(MailLetterState.Dead);
        судьбы[номера[1]].LastError.ShouldBe("Отменено: получатель убран из подписки");
        судьбы[номера[0]].State.ShouldBe(MailLetterState.Pending);
        судьбы[номера[2]].State.ShouldBe(MailLetterState.Pending);

        var подписка = await Прочитать(id);
        подписка.PendingMailIds.ShouldBe([номера[0], номера[2]], "отменённое не ждёт сверки опросом");
        подписка.LastSentSerial.ShouldBe("aa01", "остальным отправка засчитана");

        var запись = база.Аудит.Записи.Single(з => з.Action == KeyManagerActions.SubscriptionUpdated);
        запись.LogChanges.ShouldBeTrue();

        var получатели = запись.Changes.ShouldNotBeNull()["recipients"]
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>().ShouldNotBeNull();
        получатели["from"].ShouldBe(3);
        получатели["to"].ShouldBe(2);
        получатели["added"].ShouldBe(0);
        получатели["removed"].ShouldBe(1);
        запись.Changes["cancelled_letters"].ShouldBe(1);

        JsonSerializer.Serialize(запись.Changes).ShouldNotContain("@", Case.Sensitive, "адреса — сведения о людях");
    }

    [Fact]
    public async Task Удаление_подписки_отменяет_все_её_письма_в_очереди()
    {
        var id = await Создать("a@example.com", "b@example.com");
        await Опросить();

        var номера = база.Почта.Номера.ToList();

        await using (var scope = база.Scope())
        {
            await Контроллер(scope).Delete(id, Ct);
        }

        база.Почта.Отменённые.ShouldBe(номера, ignoreOrder: true);
        (await Судьбы(номера)).Values.ShouldAllBe(п => п.State == MailLetterState.Dead && п.LastError == "Отменено: подписка удалена");

        var запись = база.Аудит.Записи.Single(з => з.Action == KeyManagerActions.SubscriptionDeleted);
        запись.LogChanges.ShouldBeTrue();
        запись.Changes.ShouldNotBeNull()["cancelled_letters"].ShouldBe(2);
        запись.Changes["recipients"].ShouldBe(2);
        JsonSerializer.Serialize(запись.Changes).ShouldNotContain("@");
    }

    [Fact]
    public async Task Смена_Common_Name_отменяет_письма_прежнего_сертификата()
    {
        var id = await Создать("a@example.com");
        await Опросить();

        var номер = база.Почта.Номера.Single();

        await Изменить(id, new SubscriptionIn { CommonName = "other.example.com" });

        база.Почта.Отменённые.ShouldBe([номер]);

        var подписка = await Прочитать(id);
        подписка.PendingMailIds.ShouldBeNull();
        подписка.LastSentSerial.ShouldBeNull();

        var изменения = база.Аудит.Записи.Single(з => з.Action == KeyManagerActions.SubscriptionUpdated)
            .Changes.ShouldNotBeNull();
        var имя = изменения["common_name"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>().ShouldNotBeNull();
        имя["from"].ShouldBe("site.example.com");
        имя["to"].ShouldBe("other.example.com");
    }

    [Fact]
    public async Task Правка_без_смены_получателей_писем_не_трогает_и_пишет_только_изменённое()
    {
        var id = await Создать("a@example.com", "b@example.com");
        await Опросить();

        await Изменить(id, new SubscriptionIn
        {
            Recipients = ["b@example.com", "a@example.com"],
            IntervalDays = 7,
            Enabled = true,
        });

        база.Почта.Отменённые.ShouldBeEmpty();
        (await Прочитать(id)).PendingMailIds!.Length.ShouldBe(2);

        var изменения = база.Аудит.Записи.Single(з => з.Action == KeyManagerActions.SubscriptionUpdated)
            .Changes.ShouldNotBeNull();
        изменения.Keys.ShouldBe(["interval_days"], "перестановка адресов и прежнее «включена» — не изменения");

        var интервал = изменения["interval_days"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>().ShouldNotBeNull();
        интервал["from"].ShouldBeNull();
        интервал["to"].ShouldBe(7);
    }

    [Fact]
    public async Task Добавленный_получатель_писем_остальных_не_отменяет()
    {
        var id = await Создать("a@example.com");
        await Опросить();

        await Изменить(id, new SubscriptionIn { Recipients = ["a@example.com", "new@example.com"] });

        база.Почта.Отменённые.ShouldBeEmpty();

        var получатели = база.Аудит.Записи.Single(з => з.Action == KeyManagerActions.SubscriptionUpdated)
            .Changes.ShouldNotBeNull()["recipients"]
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>().ShouldNotBeNull();
        получатели["added"].ShouldBe(1);
        получатели["removed"].ShouldBe(0);
    }

    // --- обвязка ---------------------------------------------------------------------------

    private static SubscriptionsController Контроллер(AsyncServiceScope scope) =>
        ActivatorUtilities.CreateInstance<SubscriptionsController>(scope.ServiceProvider, new PollWakeup());

    private async Task<Guid> Создать(params string[] адреса)
    {
        await using var scope = база.Scope();

        await Контроллер(scope).Create(
            new SubscriptionIn { CommonName = "site.example.com", Recipients = адреса }, Ct);

        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var id = await db.Set<CertificateSubscription>().AsNoTracking().Select(п => п.Id).SingleAsync(Ct);

        база.Аудит.Записи.Clear();

        return id;
    }

    private async Task Изменить(Guid id, SubscriptionIn правка)
    {
        await using var scope = база.Scope();
        await Контроллер(scope).Update(id, правка, Ct);
    }

    private async Task Опросить()
    {
        await using (var scope = база.Scope())
        {
            (await scope.ServiceProvider.GetRequiredService<CertificatePolling>().RunAsync(Ct)).Sent.ShouldBe(1);
        }

        база.Аудит.Записи.Clear();
    }

    private async Task<IReadOnlyDictionary<Guid, MailLetterStatus>> Судьбы(IReadOnlyCollection<Guid> номера) =>
        await база.Почта.StatusAsync(номера, Ct);

    private async Task<CertificateSubscription> Прочитать(Guid id)
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<SenderDbContext>()
            .Set<CertificateSubscription>().AsNoTracking().SingleAsync(п => п.Id == id, Ct);
    }
}
