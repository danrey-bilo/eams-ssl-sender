using System.Globalization;
using System.IO.Compression;
using System.Text;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Domain;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Mail;
using Modules.Core.Data;
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
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
        письмо.Values["serial_numbers"].ShouldBe("aa01");
        письмо.Values["names"].ShouldBe("site.example.com");
        письмо.Values["archive"].ShouldBe("site.example.com.zip");

        // ⚠ Сертификаты — таблицей, как в уведомлении Key Manager Plus, без серийного номера и отпечатка.
        var таблица = письмо.Tables.ShouldHaveSingleItem();
        таблица.Key.ShouldBe(CertificateMail.TableName);
        таблица.Value.Columns.ShouldBe(["Сертификат", "Форматы", "Действует до", "Осталось дней"]);
        таблица.Value.Rows.ShouldHaveSingleItem().ShouldBe(["site.example.com", "PEM, KEY", "01.01.2027", Дней(Год)]);

        // ⚠ Одно вложение — архив: Outlook блокирует .crt, .cer и .der отдельными файлами.
        var архив = письмо.Attachments.ShouldHaveSingleItem();
        архив.FileName.ShouldBe("site.example.com.zip");
        архив.ContentType.ShouldBe("application/zip");

        var файлы = Распаковать(архив);
        файлы.Select(ф => ф.Name).ShouldBe(["site.example.com.pem", "site.example.com.key"]);
        Encoding.ASCII.GetString(файлы[1].Content).ShouldBe("KEY:aa01");

        var подписка = await Прочитать(id);
        подписка.SentSerials.Values.SingleOrDefault().ShouldBe("aa01");
        подписка.LastSentAt.ShouldBe(база.Часы.GetUtcNow());
        подписка.LastError.ShouldBeNull();

        база.Аудит.Записи.ShouldContain(з =>
            з.Action == KeyManagerActions.CertificateSent && з.ResourceId == id
            && з.ActorType == AuditActorType.System && з.Reason == "initial");
    }

    /// <summary>Описание подписки — заметка администраторов: в письмо оно не попадает ни строкой, ни таблицей.</summary>
    [Fact]
    public async Task Описание_подписки_в_письмо_не_уходит()
    {
        База("site.example.com", "aa01", Год);
        await Подписка("site.example.com", ["a@example.com"], description: "Заметка: стенд CRM для отдела продаж");

        (await Опросить()).Sent.ShouldBe(1);

        var письмо = база.Почта.Письма.ShouldHaveSingleItem();
        письмо.Values.Values.ShouldNotContain(значение => значение.Contains("Заметка", StringComparison.Ordinal));
        письмо.Tables.Values.SelectMany(таблица => таблица.Rows).SelectMany(строка => строка)
            .ShouldNotContain(ячейка => ячейка.Contains("Заметка", StringComparison.Ordinal));
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
        база.Почта.Письма[0].Values["serial_numbers"].ShouldBe("bb02");
        база.Аудит.Записи.Last(з => з.Action == KeyManagerActions.CertificateSent).Reason.ShouldBe("changed");
        (await Прочитать(id)).SentSerials.Values.SingleOrDefault().ShouldBe("bb02");
    }

    [Fact]
    public void Среди_замеченных_одновременно_текущий_с_поздним_сроком()
    {
        var снимок = CertificateSnapshot.Of(
        [
            new("Site.Example.com", "новый", Год.AddYears(1)),
            new("site.example.com", "старый", Год),
            new("site.example.com", "без-даты", null),
            new("other.example.com", "чужой", Год.AddYears(5)),
        ], база.Часы.GetUtcNow());

        снимок.Current("site.example.com")!.SerialNumber.ShouldBe("новый");
        снимок.Current("нет.example.com").ShouldBeNull();
    }

    [Fact]
    public async Task Перевыпуск_с_тем_же_сроком_уходит_как_обновление()
    {
        // ⚠ ⚠ Перевыпуск у удостоверяющего центра сохраняет срок, а старый сертификат ещё лежит в
        // Key Manager Plus. «Самый долгий» здесь не различает их — текущий тот, что замечен позже.
        База("site.example.com", "aa01", Год);
        await Подписка("site.example.com", ["a@example.com"]);
        await Опросить();
        база.Почта.Сброс();

        база.Часы.Вперёд(TimeSpan.FromHours(1));
        База("site.example.com", "bb02", Год);

        (await Опросить()).Sent.ShouldBe(1);
        база.Почта.Письма.Single().Values["serial_numbers"].ShouldBe("bb02");
    }

    [Fact]
    public async Task Новый_сертификат_с_меньшим_сроком_тоже_текущий()
    {
        // Новые сертификаты живут короче старых: срок не должен перевешивать «замечен позже».
        База("site.example.com", "aa01", Год.AddYears(1));
        await Подписка("site.example.com", ["a@example.com"]);
        await Опросить();
        база.Почта.Сброс();

        база.Часы.Вперёд(TimeSpan.FromHours(1));
        База("site.example.com", "bb02", Год);

        (await Опросить()).Sent.ShouldBe(1);
        база.Почта.Письма.Single().Values["serial_numbers"].ShouldBe("bb02");
    }

    [Fact]
    public async Task Снимок_старше_последней_отправки_не_шлёт_прежний_сертификат()
    {
        // Проход взял список с aa01; пока он шёл, ручная отправка разослала уже bb02. Прийти к
        // подписке со старым списком и разослать aa01 «как обновление» — ровно то, чего быть не должно.
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);
        var устаревший = CertificateSnapshot.Of([.. база.Kmp.Сертификаты], база.Часы.GetUtcNow());

        база.Часы.Вперёд(TimeSpan.FromMinutes(1));
        база.Kmp.Сертификаты.Clear();
        База("site.example.com", "bb02", Год.AddYears(1));
        (await Вручную(id)).Outcome.ShouldBe(DeliveryOutcome.Sent);
        база.Почта.Сброс();

        await using (var scope = база.Scope())
        {
            var итог = await scope.ServiceProvider.GetRequiredService<CertificateDelivery>()
                .DeliverAsync(id, база.Kmp, устаревший, manual: false, new DownloadCache(), Ct);
            итог.Outcome.ShouldBe(DeliveryOutcome.NotDue);
        }

        база.Почта.Письма.ShouldBeEmpty();
        (await Прочитать(id)).SentSerials.Values.SingleOrDefault().ShouldBe("bb02");
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

        база.Аудит.Записи.Last(з => з.Action == KeyManagerActions.CertificateSent).Reason.ShouldBe("scheduled");
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
        подписка.SentSerials.Values.SingleOrDefault().ShouldBeNull();
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
        подписка.SentSerials.Values.SingleOrDefault().ShouldBeNull();
        подписка.LastError.ShouldNotBeNull().ShouldContain("KEY");

        // Починили — следующий опрос шлёт и стирает ошибку.
        база.Kmp.ПадатьНаФормате = null;
        (await Опросить()).Sent.ShouldBe(1);
        (await Прочитать(id)).LastError.ShouldBeNull();
    }

    /// <summary>
    /// Один сертификат подписки не выгружается (в Key Manager Plus нет его ключа) — остальные уходят, а
    /// его «при обновлении» пробует снова (ревью надёжности 2026-10-27).
    /// </summary>
    [Fact]
    public async Task Невыгружаемый_сертификат_не_останавливает_остальные()
    {
        База("a.example.com", "aa01", Год);
        База("b.example.com", "bb01", Год);
        var id = await Подписка("a.example.com", ["x@example.com"], also: ["b.example.com"]);
        база.Kmp.ПадатьНаИмени = "b.example.com";

        (await Опросить()).Sent.ShouldBe(1);

        var письмо = база.Почта.Письма.ShouldHaveSingleItem();
        письмо.Values["names"].ShouldBe("a.example.com");

        var подписка = await Прочитать(id);
        подписка.SentSerials.Keys.ToList().ShouldBe(["a.example.com"]);
        подписка.LastError.ShouldNotBeNull().ShouldStartWith("b.example.com не выгрузился:");
        подписка.LastError.ShouldEndWith("— отправлены остальные.");

        база.Почта.Сброс();
        база.Kmp.ПадатьНаИмени = null;
        база.Часы.Вперёд(TimeSpan.FromMinutes(15));

        (await Опросить()).Sent.ShouldBe(1);
        база.Почта.Письма.ShouldHaveSingleItem().Values["names"].ShouldBe("b.example.com", "повтор — только не ушедший");
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
        подписка.SentSerials.Values.SingleOrDefault().ShouldBeNull("письма не легли — отправки не было");
        подписка.LastError.ShouldNotBeNull().ShouldContain("Почта не настроена");
        база.Kmp.Выгрузок.ShouldBe(0, "закрытый ключ не выгружается, если письмо всё равно некуда класть");
    }

    [Fact]
    public async Task Ушедшие_письма_сверяются_и_забываются()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com", "b@example.com"]);
        await Опросить();

        (await Прочитать(id)).PendingMailIds.ShouldNotBeNull().Length.ShouldBe(2);

        foreach (var номер in база.Почта.Номера)
        {
            база.Почта.Судьбы[номер] = new MailLetterStatus(MailLetterState.Sent, "x", null);
        }

        await Опросить();

        var подписка = await Прочитать(id);
        подписка.PendingMailIds.ShouldBeNull();
        подписка.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Умершее_письмо_становится_ошибкой_подписки_и_не_шлётся_само()
    {
        // ⚠ Повтор сам по себе слал бы второму получателю дубли раз в час, если первый ящик не существует.
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com", "b@example.com"]);
        await Опросить();

        база.Почта.Судьбы[база.Почта.Номера[0]] =
            new MailLetterStatus(MailLetterState.Dead, "a@example.com", "5.1.1 User unknown");
        база.Почта.Судьбы[база.Почта.Номера[1]] = new MailLetterStatus(MailLetterState.Sent, "b@example.com", null);
        var поставлено = база.Почта.Письма.Count;

        (await Опросить()).Sent.ShouldBe(0);

        var подписка = await Прочитать(id);
        подписка.PendingMailIds.ShouldBeNull();
        подписка.LastError.ShouldNotBeNull().ShouldContain("a@example.com — 5.1.1 User unknown");
        подписка.SentSerials.Values.SingleOrDefault().ShouldBe("aa01", "серийный прежний — сам по себе опрос не шлёт");
        база.Почта.Письма.Count.ShouldBe(поставлено);
    }

    [Fact]
    public async Task Письма_в_пути_не_сверяются_раньше_времени()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);
        await Опросить();
        await Опросить();

        (await Прочитать(id)).PendingMailIds.ShouldNotBeNull("письмо ещё ждёт отправки");
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
    public async Task Список_только_из_последнего_опроса_а_пропавшие_за_месяц_забываются()
    {
        // Владелец: панель показывает только то, что есть в Key Manager Plus сейчас, — без
        // примерных и пропавших сертификатов.
        База("site.example.com", "aa01", Год);
        База("old.example.com", "0b0c", Год);
        await Опросить();

        база.Kmp.Сертификаты.RemoveAll(с => с.CommonName == "old.example.com");
        база.Часы.Вперёд(TimeSpan.FromHours(1));
        await Опросить();

        (await Текущие()).ShouldBe(["site.example.com"]);
        (await ВсеВСнимке()).ShouldContain("old.example.com", "месяца ещё не прошло — пара помнится");

        база.Часы.Вперёд(CertificateSnapshots.Хранить + TimeSpan.FromDays(1));
        await Опросить();

        (await ВсеВСнимке()).ShouldBe(["site.example.com"]);
    }

    private async Task<string[]> Текущие()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return [.. await CertificateSnapshots.Current(db).Select(с => с.CommonName).Distinct().ToListAsync(Ct)];
    }

    private async Task<string[]> ВсеВСнимке()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return [.. await db.Set<KnownCertificate>().Select(с => с.CommonName).Distinct().ToListAsync(Ct)];
    }

    [Fact]
    public async Task Новая_отправка_отменяет_прежние_неотправленные_письма()
    {
        // Ревью безопасности 2026-10-20: прежде номера первой порции затирались, и потом её уже
        // не отменяли ни удаление подписки, ни смена получателей.
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"]);

        await Опросить();
        var первые = (await Прочитать(id)).PendingMailIds.ShouldNotBeNull();

        (await Вручную(id)).Outcome.ShouldBe(DeliveryOutcome.Sent);

        база.Почта.Отменённые.ShouldBe(первые);
        (await Прочитать(id)).PendingMailIds.ShouldNotBeNull().ShouldNotContain(первые[0]);
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

        // ⚠ Пароль — вложением: тело письма живёт в очереди до уборки, вложения стираются сразу.
        var a = база.Почта.Письма.Single(п => п.To == "a@example.com");
        a.Kind.ShouldBe(CertificateMail.Kind);
        a.Values.Values.ShouldNotContain(з => з.Contains("пароль-a", StringComparison.Ordinal));
        Encoding.UTF8.GetString(Распаковать(a.Attachments.ShouldHaveSingleItem())
                .Single(ф => ф.Name == CertificateMail.PassphraseFile).Content)
            .Trim().ShouldBe("пароль-a");

        var b = база.Почта.Письма.Single(п => п.To == "b@example.com");
        Распаковать(b.Attachments.ShouldHaveSingleItem())
            .ShouldNotContain(ф => ф.Name == CertificateMail.PassphraseFile);
    }

    [Theory]
    [InlineData(MailLocales.Ru, "08.01.2027")]
    [InlineData(MailLocales.Uk, "08.01.2027")]
    [InlineData(MailLocales.En, "2027-01-08")]
    public void Срок_в_письме_в_привычном_языку_виде(string язык, string ожидается)
    {
        CertificateMail.ExpiresOn(new DateOnly(2027, 1, 8), язык).ShouldBe(ожидается);
        CertificateMail.ExpiresOn(null, язык).ShouldBe("—");
    }

    /// <summary>
    /// Таблица — строкой-заполнителем во всех трёх языках: модуль почты вырезает переводы строк из
    /// значений, а таблицу без заполнителя в шаблоне отвергает при постановке.
    /// </summary>
    [Fact]
    public void Шаблоны_одного_и_нескольких_сертификатов_на_каждом_языке_с_таблицей()
    {
        var письма = new CertificateMail();
        string[] виды = [CertificateMail.Kind, CertificateMail.SeveralKind];

        письма.Templates.Count.ShouldBe(виды.Length * MailLocales.All.Count);
        письма.UnthrottledKinds.ShouldBe(виды, ignoreOrder: true);

        foreach (var вид in виды)
        {
            foreach (var язык in MailLocales.All)
            {
                var шаблон = письма.Templates.Single(т => т.Kind == вид && т.Locale == язык);

                шаблон.Body.Split('\n').Count(строка => строка == "{table:" + CertificateMail.TableName + "}").ShouldBe(1);
                CertificateMail.Columns(язык).Count.ShouldBe(4);
            }
        }

        CertificateMail.Kind.ShouldBe(CertificateMail.Kind);
        CertificateMail.KindFor(CertificateSubscription.MaxCertificates).ShouldBe(CertificateMail.SeveralKind);
        Should.Throw<ArgumentOutOfRangeException>(() => CertificateMail.KindFor(CertificateSubscription.MaxCertificates + 1));
    }

    [Fact]
    public async Task Подписка_на_несколько_сертификатов_шлёт_все_одним_архивом_с_папками()
    {
        База("a.example.com", "aa01", Год);
        База("*.b.example.com", "bb01", Год.AddDays(-10));
        База("c.example.com", "cc01", Год);
        var id = await Подписка("a.example.com", ["x@example.com"], also: ["*.b.example.com"], includePassphrase: true);
        база.Kmp.Пароли["aa01"] = "пароль-a";

        (await Опросить()).Sent.ShouldBe(1);

        var письмо = база.Почта.Письма.ShouldHaveSingleItem();
        письмо.Kind.ShouldBe(CertificateMail.SeveralKind);
        письмо.Values["names"].ShouldBe("a.example.com, *.b.example.com");
        письмо.Tables[CertificateMail.TableName].Rows.ShouldBe(
        [
            ["a.example.com", "PEM, KEY", "01.01.2027", Дней(Год)],
            ["*.b.example.com", "PEM, KEY", "22.12.2026", Дней(Год.AddDays(-10))],
        ]);

        var архив = письмо.Attachments.ShouldHaveSingleItem();
        архив.FileName.ShouldBe(CertificateArchive.Several);
        Распаковать(архив).Select(ф => ф.Name).ShouldBe(
        [
            "a.example.com/a.example.com.pem", "a.example.com/a.example.com.key", "a.example.com/passphrase.txt",
            "wildcard.b.example.com/wildcard.b.example.com.pem", "wildcard.b.example.com/wildcard.b.example.com.key",
        ]);

        var подписка = await Прочитать(id);
        подписка.SentSerials.ShouldBe(new Dictionary<string, string> { ["a.example.com"] = "aa01", ["*.b.example.com"] = "bb01" });
        подписка.LastFullSentAt.ShouldBe(база.Часы.GetUtcNow());
    }

    [Fact]
    public async Task При_обновлении_уходит_только_обновлённый_сертификат()
    {
        База("a.example.com", "aa01", Год);
        База("b.example.com", "bb01", Год);
        var id = await Подписка("a.example.com", ["x@example.com"], also: ["b.example.com"]);
        await Опросить();
        var полная = (await Прочитать(id)).LastFullSentAt;
        база.Почта.Сброс();

        база.Часы.Вперёд(TimeSpan.FromHours(1));
        База("b.example.com", "bb02", Год.AddYears(1));

        (await Опросить()).Sent.ShouldBe(1);

        var письмо = база.Почта.Письма.ShouldHaveSingleItem();
        письмо.Kind.ShouldBe(CertificateMail.Kind);
        письмо.Values["serial_numbers"].ShouldBe("bb02");
        Распаковать(письмо.Attachments.Single()).Select(ф => ф.Name).ShouldBe(["b.example.com.pem", "b.example.com.key"]);

        var подписка = await Прочитать(id);
        подписка.SentSerials["a.example.com"].ShouldBe("aa01");
        подписка.SentSerials["b.example.com"].ShouldBe("bb02");
        подписка.LastFullSentAt.ShouldBe(полная, "частичная отправка не сдвигает расписание");
    }

    [Fact]
    public async Task По_расписанию_и_вручную_уходят_все_сертификаты()
    {
        База("a.example.com", "aa01", Год);
        База("b.example.com", "bb01", Год);
        var id = await Подписка("a.example.com", ["x@example.com"], onChange: false, intervalDays: 2, also: ["b.example.com"]);
        await Опросить();
        база.Почта.Сброс();

        база.Часы.Вперёд(TimeSpan.FromDays(1));
        (await Опросить()).Sent.ShouldBe(0, "срок не подошёл");

        база.Часы.Вперёд(TimeSpan.FromDays(1));
        (await Опросить()).Sent.ShouldBe(1);
        база.Почта.Письма.Single().Kind.ShouldBe(CertificateMail.SeveralKind);
        база.Почта.Сброс();

        (await Вручную(id)).Outcome.ShouldBe(DeliveryOutcome.Sent);
        база.Почта.Письма.Single().Kind.ShouldBe(CertificateMail.SeveralKind, "вручную — все сертификаты");
    }

    [Fact]
    public async Task Отсутствующий_в_KMP_сертификат_не_мешает_остальным_и_виден_ошибкой()
    {
        База("a.example.com", "aa01", Год);
        var id = await Подписка("a.example.com", ["x@example.com"], also: ["нет.example.com"]);

        (await Опросить()).Sent.ShouldBe(1);

        база.Почта.Письма.Single().Values["names"].ShouldBe("a.example.com");

        var подписка = await Прочитать(id);
        подписка.LastError.ShouldBe("Сертификата нет.example.com нет в Key Manager Plus — отправлены остальные.");
        подписка.LastFullSentAt.ShouldBe(база.Часы.GetUtcNow(), "отправлены все найденные");
    }

    [Fact]
    public void Тема_перечисляет_три_имени_и_остаток()
    {
        CertificateMail.Names(["a", "b", "c", "d", "e"], MailLocales.Ru).ShouldBe("a, b, c и ещё 2");
        CertificateMail.Names(["a", "b", "c", "d"], MailLocales.En).ShouldBe("a, b, c and 1 more");
        CertificateMail.Names(["a", "b"], MailLocales.Uk).ShouldBe("a, b");
    }

    [Theory]
    [InlineData(MailLocales.Ru, "истёк")]
    [InlineData(MailLocales.En, "expired")]
    public void Истёкший_сертификат_словом_а_не_минусом(string язык, string ожидается)
    {
        var сегодня = new DateOnly(2026, 6, 1);

        CertificateMail.DaysLeft(new DateOnly(2026, 5, 31), сегодня, язык).ShouldBe(ожидается);
        CertificateMail.DaysLeft(сегодня, сегодня, язык).ShouldBe("0");
        CertificateMail.DaysLeft(null, сегодня, язык).ShouldBe("—");
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
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Set<KeyManagerState>().AsNoTracking().SingleAsync(Ct)).LastPollError
            .ShouldNotBeNull().ShouldContain("недоступен");

        (await Прочитать(id)).LastError.ShouldBeNull("подписка не виновата, что сервер лёг");
    }

    /// <summary>
    /// Не только отказ Key Manager Plus: любая ошибка открытия клиента видна в состоянии, а не только в
    /// логе (ревью надёжности 2026-10-27).
    /// </summary>
    [Fact]
    public async Task Ошибка_открытия_клиента_записывается_в_состояние()
    {
        база.Kmp.ОткрытиеПадает = new System.Security.Cryptography.CryptographicException("ключ API не открылся");

        (await Опросить()).Polled.ShouldBeFalse();

        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Set<KeyManagerState>().AsNoTracking().SingleAsync(Ct)).LastPollError
            .ShouldNotBeNull().ShouldStartWith("Опрос не удался");
    }

    /// <summary>
    /// Перерыв в опросе дольше месяца (интеграция выключена, сервис стоял) не стирает «впервые
    /// увиден» у пар, которые в Key Manager Plus есть, — по нему выбирается текущий сертификат.
    /// </summary>
    [Fact]
    public async Task Перерыв_в_опросе_дольше_месяца_не_забывает_когда_пару_увидели_впервые()
    {
        База("site.example.com", "aa01", Год);
        await Опросить();
        var впервые = await Впервые("site.example.com", "aa01");

        база.Часы.Вперёд(CertificateSnapshots.Хранить + TimeSpan.FromDays(1));
        await Опросить();

        (await Впервые("site.example.com", "aa01")).ShouldBe(впервые, "пара была в Key Manager Plus весь перерыв");
    }

    /// <summary>
    /// Key Manager Plus снова внёс старый сертификат (сканирование нашло его на необновлённом сервере): он
    /// замечен позже всех, но выпущен раньше отправленного — сам не уходит (ревью надёжности 2026-10-27).
    /// </summary>
    [Fact]
    public async Task Старый_выпуск_замеченный_позже_не_уходит_обновлением()
    {
        База("site.example.com", "aa02", Год);
        база.Kmp.Выпуски["aa02"] = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var id = await Подписка("site.example.com", ["a@example.com"]);
        (await Опросить()).Sent.ShouldBe(1);
        (await Прочитать(id)).SentIssued.ShouldContainKey("site.example.com");
        база.Почта.Сброс();

        // Старый выпуск появился — отправленный ещё в Key Manager Plus: текущим остаётся он.
        база.Часы.Вперёд(TimeSpan.FromHours(1));
        База("site.example.com", "aa01", Год.AddYears(1));
        база.Kmp.Выпуски["aa01"] = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

        (await Опросить()).Sent.ShouldBe(0, "старый выпуск не обновление, даже если срок у него дольше");
        база.Почта.Письма.ShouldBeEmpty();
        (await Прочитать(id)).SentSerials["site.example.com"].ShouldBe("aa02");

        // Отправленного в Key Manager Plus больше нет — опрос не шлёт и говорит почему, ручная отправка шлёт.
        база.Kmp.Сертификаты.RemoveAll(с => с.SerialNumber == "aa02");
        база.Часы.Вперёд(TimeSpan.FromHours(1));

        (await Опросить()).Sent.ShouldBe(0);
        (await Прочитать(id)).LastError.ShouldNotBeNull().ShouldContain("старее отправленного");

        (await Вручную(id)).Outcome.ShouldBe(DeliveryOutcome.Sent, "вручную — решение администратора");
        база.Почта.Письма.ShouldHaveSingleItem().Values["serial_numbers"].ShouldBe("aa01");
    }

    /// <summary>
    /// Продлённый досрочно под новые предельные сроки истекает РАНЬШЕ прежнего, но выпущен позже — это
    /// обновление, и оно уходит.
    /// </summary>
    [Fact]
    public async Task Новый_выпуск_с_более_коротким_сроком_уходит_обновлением()
    {
        База("site.example.com", "aa01", Год.AddYears(1));
        база.Kmp.Выпуски["aa01"] = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = await Подписка("site.example.com", ["a@example.com"]);
        await Опросить();
        база.Почта.Сброс();

        база.Часы.Вперёд(TimeSpan.FromHours(1));
        База("site.example.com", "bb02", Год);
        база.Kmp.Выпуски["bb02"] = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        (await Опросить()).Sent.ShouldBe(1);
        база.Почта.Письма.Single().Values["serial_numbers"].ShouldBe("bb02");
        (await Прочитать(id)).SentSerials["site.example.com"].ShouldBe("bb02");
    }

    /// <summary>
    /// Не дошло ни одно письмо отправки (почта лежала дольше всех попыток) — отправка повторяется сама, но не
    /// больше трёх раз подряд (ревью надёжности 2026-10-27).
    /// </summary>
    [Fact]
    public async Task Если_не_дошло_ни_одно_письмо_отправка_повторяется_сама_трижды()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com", "b@example.com"]);
        await Опросить();

        for (var повтор = 1; повтор <= CertificatePolling.ПовторовСамих; повтор++)
        {
            Умерли(база.Почта.Номера);
            var было = база.Почта.Письма.Count;
            база.Часы.Вперёд(TimeSpan.FromMinutes(45));

            (await Опросить()).Sent.ShouldBe(1, $"повтор {повтор}");
            (база.Почта.Письма.Count - было).ShouldBe(2, "заново — обоим");

            // Повтор лёг в очередь этим же проходом — ошибки нет, а счётчик помнит, сколько раз повторяли.
            (await Прочитать(id)).AutoRetries.ShouldBe(повтор);
        }

        Умерли(база.Почта.Номера);
        база.Часы.Вперёд(TimeSpan.FromMinutes(45));

        (await Опросить()).Sent.ShouldBe(0, "автоповтор исчерпан");
        (await Прочитать(id)).LastError.ShouldNotBeNull().ShouldContain("автоповтор исчерпан");
    }

    [Fact]
    public async Task Если_одному_дошло_повтора_нет_а_счётчик_сброшен()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com", "b@example.com"]);
        await Опросить();

        var номера = база.Почта.Номера.ToList();
        база.Почта.Судьбы[номера[0]] = new MailLetterStatus(MailLetterState.Sent, "a@example.com", null);
        база.Почта.Судьбы[номера[1]] = new MailLetterStatus(MailLetterState.Dead, "b@example.com", "ящика нет");

        (await Опросить()).Sent.ShouldBe(0, "дубли тем, кому дошло, не шлются");

        var подписка = await Прочитать(id);
        подписка.AutoRetries.ShouldBe(0);
        подписка.PendingMailIds.ShouldBeNull();
        подписка.LastError.ShouldNotBeNull().ShouldContain("b@example.com — ящика нет");
    }

    /// <summary>
    /// Закрытый ключ и его пароль не уходят по почте без шифрования (ревью безопасности 2026-10-27); сертификат
    /// без ключа — уходит.
    /// </summary>
    [Fact]
    public async Task Ключ_не_уходит_по_почте_без_шифрования()
    {
        База("a.example.com", "aa01", Год);
        База("b.example.com", "bb01", Год);
        var сКлючом = await Подписка("a.example.com", ["x@example.com"]);
        var безКлюча = await Подписка("b.example.com", ["y@example.com"], formats: ["PEM", "CRT"]);
        база.Настройки.Значения[CertificateDelivery.SmtpSecurity.Name] = "none";

        try
        {
            (await Опросить()).Sent.ShouldBe(1);

            база.Почта.Письма.ShouldHaveSingleItem().To.ShouldBe("y@example.com");
            (await Прочитать(сКлючом)).LastError.ShouldNotBeNull().ShouldContain("без шифрования");
            (await Прочитать(безКлюча)).LastError.ShouldBeNull();
        }
        finally
        {
            база.Настройки.Значения.Remove(CertificateDelivery.SmtpSecurity.Name);
        }
    }

    // --- период «с — по» и время отправки (решение владельца 2026-10-28) ------------------

    [Fact]
    public async Task До_первого_дня_периода_опрос_не_шлёт()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"], activeFrom: Сегодня.AddDays(1), timeZone: "UTC");

        (await Опросить()).Sent.ShouldBe(0, "первый день не наступил");
        (await Прочитать(id)).LastAttemptAt.ShouldBeNull("вне периода опрос подписку не трогает");

        Завтра(0, 5);
        (await Опросить()).Sent.ShouldBe(1, "первый день — первая отправка");
    }

    [Fact]
    public async Task После_последнего_дня_опрос_молчит_а_вручную_можно()
    {
        База("site.example.com", "aa01", Год);
        var id = await Подписка("site.example.com", ["a@example.com"], activeFrom: Сегодня, activeUntil: Сегодня, timeZone: "UTC");

        (await Опросить()).Sent.ShouldBe(1, "последний день — ещё внутри периода");

        Завтра(0, 5);
        База("site.example.com", "bb02", Год.AddYears(1));
        (await Опросить()).Sent.ShouldBe(0, "период кончился — обновление не уходит");

        (await Вручную(id)).Outcome.ShouldBe(DeliveryOutcome.Sent, "ручная отправка период не спрашивает");
    }

    [Fact]
    public async Task Время_отправки_держит_письма_до_своего_часа()
    {
        База("site.example.com", "aa01", Год);
        Завтра(8, 0);
        var id = await Подписка("site.example.com", ["a@example.com"], sendTime: new TimeOnly(9, 0), timeZone: "UTC");

        (await Опросить()).Sent.ShouldBe(0, "до 09:00 не шлёт");

        На(база.Часы.GetUtcNow().AddMinutes(65));
        (await Опросить()).Sent.ShouldBe(1, "09:05 — первая отправка");
        (await Опросить()).Sent.ShouldBe(0, "время отправки разобрано");

        На(база.Часы.GetUtcNow().AddHours(1));
        База("site.example.com", "bb02", Год.AddYears(1));
        (await Опросить()).Sent.ShouldBe(0, "замеченное после 09:00 ждёт следующего дня");

        Завтра(8, 55);
        (await Опросить()).Sent.ShouldBe(0, "назавтра до 09:00 — тоже ждёт");

        На(база.Часы.GetUtcNow().AddMinutes(10));
        (await Опросить()).Sent.ShouldBe(1, "назавтра в 09:00 — уходит");

        база.Почта.Письма.Count.ShouldBe(2);
        (await Прочитать(id)).SentSerials["site.example.com"].ShouldBe("bb02");
    }

    [Fact]
    public async Task Пропущенное_время_отправки_догоняется_один_раз()
    {
        База("site.example.com", "aa01", Год);
        Завтра(8, 0);
        await Подписка("site.example.com", ["a@example.com"], sendTime: new TimeOnly(9, 0), timeZone: "UTC");

        // Сервис стоял: ни вчерашнее, ни сегодняшнее 09:00 опрос не видел.
        Завтра(15, 0);
        (await Опросить()).Sent.ShouldBe(1, "пропущенное 09:00 догоняется");
        (await Опросить()).Sent.ShouldBe(0, "и только один раз");
    }

    [Fact]
    public async Task Раз_в_N_дней_со_временем_отправки_считается_днями()
    {
        База("site.example.com", "aa01", Год);
        Завтра(8, 0);
        await Подписка("site.example.com", ["a@example.com"], onChange: false, intervalDays: 1, sendTime: new TimeOnly(9, 0), timeZone: "UTC");

        На(база.Часы.GetUtcNow().AddMinutes(70));
        (await Опросить()).Sent.ShouldBe(1, "09:10 — первая отправка");

        // 09:01 — раньше вчерашних 09:10, но день уже следующий.
        Завтра(9, 1);
        (await Опросить()).Sent.ShouldBe(1, "раз в день — назавтра в своё время, а не через сутки после прошлой");
        база.Аудит.Записи.Last(з => з.Action == KeyManagerActions.CertificateSent).Reason.ShouldBe("scheduled");
    }

    [Fact]
    public async Task Время_отправки_считается_в_часовом_поясе_подписки()
    {
        var пояс = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kiev");
        var девять = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(Сегодня.AddDays(2).ToDateTime(new TimeOnly(9, 0)), пояс), TimeSpan.Zero);

        // Киев — UTC+2 зимой и UTC+3 летом: 09:00 там — не 09:00 UTC.
        девять.Hour.ShouldBeOneOf(6, 7);

        База("site.example.com", "aa01", Год);
        На(девять.AddHours(-3));
        await Подписка("site.example.com", ["a@example.com"], sendTime: new TimeOnly(9, 0), timeZone: "Europe/Kiev");

        На(девять.AddMinutes(-10));
        (await Опросить()).Sent.ShouldBe(0, "в Киеве ещё 08:50");

        На(девять.AddMinutes(5));
        (await Опросить()).Sent.ShouldBe(1, "в Киеве 09:05");
    }

    [Fact]
    public async Task Автоповтор_не_ждёт_следующего_времени_отправки()
    {
        База("site.example.com", "aa01", Год);
        Завтра(8, 0);
        await Подписка("site.example.com", ["a@example.com"], sendTime: new TimeOnly(9, 0), timeZone: "UTC");

        На(база.Часы.GetUtcNow().AddMinutes(65));
        (await Опросить()).Sent.ShouldBe(1);

        Умерли(база.Почта.Номера);
        На(база.Часы.GetUtcNow().AddHours(3));
        (await Опросить()).Sent.ShouldBe(1, "письма той же отправки уходят сразу, а не завтра в 09:00");
    }

    // --- обвязка ---------------------------------------------------------------------------

    /// <summary>Часы — вперёд до заданного мгновения.</summary>
    private void На(DateTimeOffset когда) => база.Часы.Вперёд(когда - база.Часы.GetUtcNow());

    /// <summary>Часы — на завтра по UTC, в заданное время.</summary>
    private void Завтра(int час, int минута) =>
        На(new DateTimeOffset(база.Часы.GetUtcNow().UtcDateTime.Date.AddDays(1).AddHours(час).AddMinutes(минута), TimeSpan.Zero));

    private DateOnly Сегодня => DateOnly.FromDateTime(база.Часы.GetUtcNow().UtcDateTime);

    private void Умерли(IEnumerable<Guid> номера)
    {
        foreach (var номер in номера.ToList())
        {
            база.Почта.Судьбы[номер] = new MailLetterStatus(MailLetterState.Dead, "x@example.com", "сервер почты недоступен");
        }
    }

    private async Task<DateTimeOffset> Впервые(string commonName, string serial)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Set<KnownCertificate>().AsNoTracking()
            .Where(с => с.CommonName == commonName && с.SerialNumber == serial)
            .Select(с => с.FirstSeenAt)
            .SingleAsync(Ct);
    }

    /// <summary>Сколько дней до даты по часам обвязки: часы общие для коллекции и двигаются тестами.</summary>
    private string Дней(DateOnly дата) =>
        (дата.DayNumber - DateOnly.FromDateTime(база.Часы.GetUtcNow().UtcDateTime).DayNumber).ToString(CultureInfo.InvariantCulture);

    private void База(string commonName, string serial, DateOnly? expires) =>
        база.Kmp.Сертификаты.Add(new KmpCertificate(commonName, serial, expires));

    private async Task<Guid> Подписка(
        string commonName,
        string[] recipients,
        bool onChange = true,
        int? intervalDays = null,
        bool enabled = true,
        bool includePassphrase = false,
        string[]? formats = null,
        string[]? also = null,
        string? description = null,
        DateOnly? activeFrom = null,
        DateOnly? activeUntil = null,
        TimeOnly? sendTime = null,
        string? timeZone = null)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var подписка = new CertificateSubscription
        {
            Id = Guid.CreateVersion7(),
            Description = description,
            CommonNames = [commonName, .. also ?? []],
            Recipients = recipients,
            Formats = formats ?? [.. CertificateFormats.Default],
            OnChange = onChange,
            IntervalDays = intervalDays,
            Enabled = enabled,
            IncludePassphrase = includePassphrase,
            Locale = MailLocales.Ru,
            CreatedAt = база.Часы.GetUtcNow(),
            UpdatedAt = база.Часы.GetUtcNow(),
            ActiveFrom = activeFrom,
            ActiveUntil = activeUntil,
            SendTime = sendTime,
            TimeZone = timeZone,

            // Как у API: новое расписание начинается с ближайшего времени отправки.
            LastSlotAt = activeFrom is null && activeUntil is null && sendTime is null ? null : база.Часы.GetUtcNow(),
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
        var снимок = await scope.ServiceProvider.GetRequiredService<CertificateSnapshots>().TakeAsync(база.Kmp, Ct);

        return await scope.ServiceProvider.GetRequiredService<CertificateDelivery>().DeliverAsync(
            id, база.Kmp, снимок, manual: true, new DownloadCache(), Ct);
    }

    private async Task<CertificateSubscription> Прочитать(Guid id)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Set<CertificateSubscription>().AsNoTracking().SingleAsync(п => п.Id == id, Ct);
    }

    /// <summary>Содержимое архива-вложения: имя и байты каждого файла, по порядку.</summary>
    private static List<(string Name, byte[] Content)> Распаковать(MailAttachment архив)
    {
        using var архивZip = new ZipArchive(new MemoryStream(архив.Content), ZipArchiveMode.Read);

        return [.. архивZip.Entries.Select(запись =>
        {
            using var поток = запись.Open();
            using var копия = new MemoryStream();
            поток.CopyTo(копия);

            return (запись.FullName, копия.ToArray());
        })];
    }
}
