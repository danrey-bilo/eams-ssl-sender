using System.Text;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EmailSslSender.Modules.KeyManager.Tests;

/// <summary>
/// Опрос и отправка: когда сертификат уходит, кому, в каких файлах — и когда не уходит.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Главный факт файла — новый серийный номер уходит ровно один раз.</b> Продление в Key
/// Manager Plus видно только так, и ошибка здесь бывает двух видов: сертификат не ушёл вовсе или
/// уходит на каждом опросе. Оба проверяются.
/// </remarks>
[Collection(KeyManagerDatabaseCollection.Name)]
public sealed class DeliveryTests(KeyManagerDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateOnly Год = new(2027, 1, 1);

    public ValueTask InitializeAsync()
    {
        база.Сброс();
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
    public async Task Новая_подписка_получает_текущий_сертификат_на_первом_опросе()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com", "b@example.com"]);

        var итог = await Опросить();

        итог.Polled.ShouldBeTrue();
        итог.Sent.ShouldBe(1);

        база.Почта.Письма.Select(п => п.To).ShouldBe(["a@example.com", "b@example.com"]);

        var письмо = база.Почта.Письма[0];
        письмо.Kind.ShouldBe(CertificateMail.Kind);
        письмо.Values["serial_number"].ShouldBe("aa01");
        письмо.Values["expires_on"].ShouldBe("2027-01-01");
        письмо.Values["reason"].ShouldBe(CertificateMail.Reason(DeliveryReason.Initial, MailLocales.Ru));
        письмо.Attachments.Select(ф => ф.FileName).ShouldBe(["site.example.com.pem", "site.example.com.key"]);
        Encoding.ASCII.GetString(письмо.Attachments[1].Content).ShouldBe("KEY:aa01");

        var подписка = await Прочитать(id);
        подписка.LastSentSerial.ShouldBe("aa01");
        подписка.LastSentAt.ShouldBe(база.Часы.GetUtcNow());
        подписка.LastError.ShouldBeNull();

        база.Аудит.Записи.ShouldContain(з =>
            з.Action == KeyManagerActions.CertificateSent && з.ResourceId == id
            && з.ActorType == AuditActorType.System && з.Reason == "initial");
    }

    [Fact]
    public async Task Без_изменений_следующий_опрос_молчит()
    {
        База("site.example.com", "aa01", Год);
        await Подписка("site.example.com", ["a@example.com"]);

        await Опросить();
        база.Почта.Сброс();

        база.Часы.Вперёд(TimeSpan.FromDays(30));
        (await Опросить()).Sent.ShouldBe(0);

        база.Почта.Письма.ShouldBeEmpty("серийный номер прежний — слать нечего");
    }

    [Fact]
    public async Task Новый_серийный_номер_уходит_как_обновление_и_только_один_раз()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);
        await Опросить();
        база.Почта.Сброс();

        // Продление: Key Manager Plus держит и старый, и новый.
        База("site.example.com", "bb02", Год.AddYears(1));

        (await Опросить()).Sent.ShouldBe(1);
        (await Опросить()).Sent.ShouldBe(0, "второй опрос того же продления не шлёт");

        база.Почта.Письма.Count.ShouldBe(1);
        база.Почта.Письма[0].Values["serial_number"].ShouldBe("bb02");
        база.Почта.Письма[0].Values["reason"].ShouldBe(CertificateMail.Reason(DeliveryReason.Changed, MailLocales.Ru));
        (await Прочитать(id)).LastSentSerial.ShouldBe("bb02");
    }

    [Fact]
    public void Из_двух_сертификатов_одного_имени_текущий_с_поздним_сроком()
    {
        IReadOnlyList<KmpCertificate> список =
        [
            new("Site.Example.com", "новый", Год.AddYears(1)),
            new("site.example.com", "старый", Год),
            new("site.example.com", "без-даты", null),
            new("other.example.com", "чужой", Год.AddYears(5)),
        ];

        CertificateDelivery.Current(список, "site.example.com")!.SerialNumber.ShouldBe("новый");
        CertificateDelivery.Current(список, "нет.example.com").ShouldBeNull();
    }

    [Fact]
    public async Task Регистр_серийного_номера_продлением_не_считается()
    {
        База("site.example.com", "AA01", Год);
        await Подписка("site.example.com", ["a@example.com"]);
        await Опросить();
        база.Почта.Сброс();

        база.Kmp.Сертификаты.Clear();
        База("site.example.com", "aa01", Год);

        (await Опросить()).Sent.ShouldBe(0);
    }

    [Fact]
    public async Task Расписание_шлёт_повторно_по_сроку()
    {
        База("site.example.com", "aa01", Год);
        await Подписка("site.example.com", ["a@example.com"], onChange: false, intervalDays: 7);

        (await Опросить()).Sent.ShouldBe(1, "первая отправка — сразу");

        база.Часы.Вперёд(TimeSpan.FromDays(6));
        (await Опросить()).Sent.ShouldBe(0, "неделя не прошла");

        база.Часы.Вперёд(TimeSpan.FromDays(1));
        (await Опросить()).Sent.ShouldBe(1, "неделя прошла");

        база.Почта.Письма[^1].Values["reason"]
            .ShouldBe(CertificateMail.Reason(DeliveryReason.Scheduled, MailLocales.Ru));
    }

    [Fact]
    public async Task Без_on_change_новый_серийный_ждёт_расписания()
    {
        База("site.example.com", "aa01", Год);
        await Подписка("site.example.com", ["a@example.com"], onChange: false, intervalDays: 30);
        await Опросить();
        база.Почта.Сброс();

        База("site.example.com", "bb02", Год.AddYears(1));
        (await Опросить()).Sent.ShouldBe(0);
    }

    [Fact]
    public async Task Сертификата_нет_и_это_видно_в_подписке()
    {
        База("other.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);

        (await Опросить()).Sent.ShouldBe(0);

        var подписка = await Прочитать(id);
        подписка.LastSentSerial.ShouldBeNull();
        подписка.LastError.ShouldNotBeNull().ShouldContain("site.example.com");
        база.Почта.Письма.ShouldBeEmpty();
    }

    [Fact]
    public async Task Отказ_выгрузки_не_ставит_ни_одного_письма_и_не_отмечает_отправку()
    {
        // ⚠ PEM выгрузился, KEY — нет: письмо без ключа было бы письмом, которое не установить, а
        // отметка «отправлено» закрыла бы повтор навсегда.
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);
        база.Kmp.ПадатьНаФормате = CertificateFormats.Key;

        (await Опросить()).Sent.ShouldBe(0);

        база.Почта.Письма.ShouldBeEmpty();
        var подписка = await Прочитать(id);
        подписка.LastSentSerial.ShouldBeNull();
        подписка.LastError.ShouldNotBeNull().ShouldContain("KEY");

        // Починили — следующий опрос шлёт и стирает ошибку.
        база.Kmp.ПадатьНаФормате = null;
        (await Опросить()).Sent.ShouldBe(1);
        (await Прочитать(id)).LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Ненастроенная_почта_не_отмечает_отправку()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);
        база.Почта.Ответ = MailQueueResult.NotConfigured;

        (await Опросить()).Sent.ShouldBe(0);

        var подписка = await Прочитать(id);
        подписка.LastSentSerial.ShouldBeNull("письма не легли — отправки не было");
        подписка.LastError.ShouldNotBeNull().ShouldContain("Почта не настроена");
    }

    [Fact]
    public async Task Отключённая_подписка_опросом_не_шлётся_а_вручную_шлётся()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"], enabled: false);

        (await Опросить()).Sent.ShouldBe(0);
        база.Почта.Письма.ShouldBeEmpty();

        var итог = await Вручную(id);

        итог.Outcome.ShouldBe(DeliveryOutcome.Sent);
        итог.Reason.ShouldBe(DeliveryReason.Manual);
        база.Почта.Письма.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ручная_отправка_шлёт_и_без_изменений()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);
        await Опросить();

        (await Вручную(id)).Outcome.ShouldBe(DeliveryOutcome.Sent);

        база.Почта.Письма.Count.ShouldBe(2);
        база.Аудит.Записи.Last().ActorType.ShouldBeNull("ручную отправку подписывает тот, кто пришёл");
    }

    [Fact]
    public async Task Пароль_ключа_пишется_только_по_просьбе()
    {
        База("a.example.com", "aa01", Год);
        База("b.example.com", "bb02", Год);
        база.Kmp.Пароли["aa01"] = "пароль-a";
        база.Kmp.Пароли["bb02"] = "пароль-b";

        await Подписка("a.example.com", ["a@example.com"], includePassphrase: true);
        await Подписка("b.example.com", ["b@example.com"]);

        await Опросить();

        база.Почта.Письма.Single(п => п.To == "a@example.com").Values["passphrase"].ShouldContain("пароль-a");
        база.Почта.Письма.Single(п => п.To == "b@example.com").Values["passphrase"].ShouldBeEmpty();
    }

    [Fact]
    public async Task Один_сертификат_на_две_подписки_выгружается_один_раз()
    {
        База("site.example.com", "aa01", Год);
        await Подписка("site.example.com", ["a@example.com"]);
        await Подписка("site.example.com", ["b@example.com"]);

        (await Опросить()).Sent.ShouldBe(2);

        база.Kmp.Выгрузок.ShouldBe(2, "PEM и KEY — по разу на проход, а не на подписку");
    }

    [Fact]
    public async Task Занятая_подписка_пропускается_а_не_ждёт()
    {
        // ⚠ ⚠ Ради этого строка берётся FOR UPDATE SKIP LOCKED: второй процесс (или ручная
        // отправка), пришедший к занятой подписке, проходит мимо, а не шлёт её второй раз.
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);

        await using var соединение = new NpgsqlConnection(база.Connection);
        await соединение.OpenAsync(Ct);
        await using var транзакция = await соединение.BeginTransactionAsync(Ct);
        await using (var блокировка = new NpgsqlCommand(
            "SELECT 1 FROM certificate_subscriptions WHERE id = @id FOR UPDATE", соединение, транзакция))
        {
            блокировка.Parameters.AddWithValue("id", id);
            await блокировка.ExecuteScalarAsync(Ct);
        }

        (await Вручную(id)).Outcome.ShouldBe(DeliveryOutcome.Busy);
        база.Почта.Письма.ShouldBeEmpty();

        await транзакция.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Два_одновременных_прохода_шлют_подписку_один_раз()
    {
        База("site.example.com", "aa01", Год);
        await Подписка("site.example.com", ["a@example.com"]);

        // Первый проход застревает на выгрузке, держа строку подписки; второй приходит в это время.
        база.Kmp.Задержать();
        var первый = Опросить();
        await база.Kmp.ВыгрузкаНачалась.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var второй = await Опросить();
        база.Kmp.Отпустить();
        var итогПервого = await первый;

        (итогПервого.Sent + второй.Sent).ShouldBe(1);
        база.Почта.Письма.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ненастроенная_интеграция_молчит_и_в_Key_Manager_не_ходит()
    {
        база.Kmp.Настройки = база.Kmp.Настройки with { Enabled = false };
        await Подписка("site.example.com", ["a@example.com"]);

        (await Опросить()).Polled.ShouldBeFalse();

        база.Kmp.Списков.ShouldBe(0);
        база.Логи.Строки.ShouldNotContain(с => с.StartsWith("keymanager_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Опрос_запоминает_снимок_и_состояние()
    {
        База("site.example.com", "aa01", Год);
        База("site.example.com", "bb02", null);

        await Опросить();
        var первый = база.Часы.GetUtcNow();
        база.Часы.Вперёд(TimeSpan.FromHours(1));
        await Опросить();

        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        var снимок = await db.Set<KnownCertificate>().AsNoTracking().OrderBy(с => с.SerialNumber).ToListAsync(Ct);
        снимок.Select(с => с.SerialNumber).ShouldBe(["aa01", "bb02"]);
        снимок[0].ExpiresOn.ShouldBe(Год);
        снимок[1].ExpiresOn.ShouldBeNull();
        снимок[0].FirstSeenAt.ShouldBe(первый, "впервые увиден первым опросом");
        снимок[0].LastSeenAt.ShouldBe(база.Часы.GetUtcNow());

        var состояние = await db.Set<KeyManagerState>().AsNoTracking().SingleAsync(Ct);
        состояние.LastPollCount.ShouldBe(2);
        состояние.LastPollError.ShouldBeNull();
    }

    [Fact]
    public async Task Отказ_списка_записывается_в_состояние_и_не_трогает_подписки()
    {
        var id = await Подписка("site.example.com", ["a@example.com"]);
        база.Kmp.СписокПадает = true;

        (await Опросить()).Polled.ShouldBeFalse();

        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        (await db.Set<KeyManagerState>().AsNoTracking().SingleAsync(Ct)).LastPollError
            .ShouldNotBeNull().ShouldContain("недоступен");

        (await Прочитать(id)).LastError.ShouldBeNull("подписка не виновата, что сервер лёг");
    }

    // --- обвязка ---------------------------------------------------------------------------

    private void База(string commonName, string serial, DateOnly? expires) =>
        база.Kmp.Сертификаты.Add(new KmpCertificate(commonName, serial, expires));

    private async Task<Guid> Подписка(
        string commonName,
        string[] recipients,
        bool onChange = true,
        int? intervalDays = null,
        bool enabled = true,
        bool includePassphrase = false)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        var подписка = new CertificateSubscription
        {
            Id = Guid.CreateVersion7(),
            CommonName = commonName,
            Recipients = recipients,
            Formats = [.. CertificateFormats.Default],
            OnChange = onChange,
            IntervalDays = intervalDays,
            Enabled = enabled,
            IncludePassphrase = includePassphrase,
            Locale = MailLocales.Ru,
            CreatedAt = база.Часы.GetUtcNow(),
            UpdatedAt = база.Часы.GetUtcNow(),
        };

        db.Add(подписка);
        await db.SaveChangesAsync(Ct);

        return подписка.Id;
    }

    private async Task<PollRun> Опросить()
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<CertificatePolling>().RunAsync(Ct);
    }

    private async Task<DeliveryResult> Вручную(Guid id)
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<CertificateDelivery>().DeliverAsync(
            id, база.Kmp, await база.Kmp.ListAsync(Ct), manual: true, new DownloadCache(), Ct);
    }

    private async Task<CertificateSubscription> Прочитать(Guid id)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Set<CertificateSubscription>().AsNoTracking().SingleAsync(п => п.Id == id, Ct);
    }
}
