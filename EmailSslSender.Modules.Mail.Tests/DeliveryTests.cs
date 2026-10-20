using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Modules.Mail.Settings;
using EmailSslSender.Modules.Mail.Templates;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Разбор очереди: атомарный захват, каждое письмо в своей попытке, уборка отправленных.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Отправитель здесь ПОДСТАВНОЙ.</b> Настоящий SMTP в тесте означал бы либо сеть, либо
/// контейнер почтового сервера ради проверки цикла, который про SMTP ничего не знает. Что
/// сообщение собирается верно — проверяется отдельно и без сети.
/// </remarks>
[Collection(MailDatabaseCollection.Name)]
public sealed class DeliveryTests(MailDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        база.Настройки.Значения.Clear();
        база.Настройки.Значения[MailSettings.Enabled.Name] = true;
        база.Настройки.Значения[MailSettings.SmtpHost.Name] = "smtp.example.com";
        // ⚠ Вскрытие 44: без адреса отправителя почта НЕ считается настроенной —
        // прежде он угадывался из учётной записи SMTP, и угадывался неверно.
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
    public async Task Письмо_уходит_и_отмечается_отправленным()
    {
        var адрес = await Поставить();

        (await Разобрать()).ShouldBe(1);

        var письмо = await Письмо(адрес);

        письмо.SentAt.ShouldNotBeNull();
        письмо.Attempts.ShouldBe(1);
        письмо.LastError.ShouldBeNull();

        // Отправителю досталось расшифрованное тело — то есть конверт открывается насосом.
        база.Отправитель.Ушедшие.ShouldContain(п => п.Body.Contains("настроена верно", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Одно_упавшее_письмо_не_уносит_пачку()
    {
        // ⚠ ⚠ Ради этого `try` стоит ВНУТРИ цикла, а не вокруг него. Ровно этот дефект уже
        // ловили в рассылке сообщений 2026-08-16: общий обработчик вокруг цикла терял все
        // кадры разом, а выглядело это как «иногда не доходит».
        var первый = await Поставить();
        var второй = await Поставить();
        var третий = await Поставить();

        база.Отправитель.ПадатьНа = второй;

        (await Разобрать()).ShouldBe(2);

        (await Письмо(первый)).SentAt.ShouldNotBeNull();
        (await Письмо(третий)).SentAt.ShouldNotBeNull();

        var упавшее = await Письмо(второй);
        упавшее.SentAt.ShouldBeNull();
        упавшее.LastError.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Попытка_растёт_при_захвате_а_не_после_отправки()
    {
        // ⚠ Процесс, упавший посреди отправки, иначе оставил бы письмо с прежним счётчиком —
        // и оно пробовалось бы вечно.
        var адрес = await Поставить();
        база.Отправитель.ПадатьНа = адрес;

        await Разобрать();
        (await Письмо(адрес)).Attempts.ShouldBe(1);

        // Время двигаем вперёд: пауза повтора отодвинула следующую попытку.
        база.Часы.Вперёд(TimeSpan.FromHours(1));
        await Разобрать();

        (await Письмо(адрес)).Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task Исчерпавшее_попытки_письмо_остаётся_с_текстом_ошибки()
    {
        // ⚠ Не удаляется: строка с ошибкой — ответ администратору на вопрос «почему не дошло»,
        // а не мусор. В следующую выборку она не попадает — условие смотрит на число попыток.
        база.Настройки.Значения[MailSettings.MaxAttempts.Name] = 2;

        var адрес = await Поставить();
        база.Отправитель.ПадатьНа = адрес;

        for (var раз = 0; раз < 4; раз++)
        {
            await Разобрать();
            база.Часы.Вперёд(TimeSpan.FromHours(1));
        }

        var письмо = await Письмо(адрес);

        письмо.Attempts.ShouldBe(2, "после исчерпания попыток письмо больше не берётся");
        письмо.SentAt.ShouldBeNull();
        письмо.LastError.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Пустой_прогон_молчит()
    {
        // ⚠ Пятнадцатисекундное расписание, пишущее строку в лог, заливает лог собой — и в нём
        // тонет всё, ради чего лог читают.
        (await Разобрать()).ShouldBe(0);

        // ⚠ Пустым список быть не может — тот же провайдер ловит и запросы EF. Проверяется
        // отсутствие СВОИХ строк: они все начинаются с `mail_`.
        Своих().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ненастроенная_почта_не_разбирает_очередь_и_молчит()
    {
        var адрес = await Поставить();
        база.Настройки.Значения[MailSettings.Enabled.Name] = false;

        (await Разобрать()).ShouldBe(0);

        (await Письмо(адрес)).Attempts.ShouldBe(0, "письмо даже не бралось");
        Своих().ShouldBeEmpty();
    }

    [Fact]
    public async Task Два_прогона_подряд_не_берут_одно_письмо_дважды()
    {
        // ⚠ ⚠ Захват атомарен: `UPDATE … FOR UPDATE SKIP LOCKED … RETURNING` сразу увеличивает
        // попытку и отодвигает следующую. Без этого два процесса — `api` и `worker` — взяли бы
        // одни и те же письма: человек получил бы код входа дважды, а предел частоты не
        // сработал бы, потому что письмо в очереди одно.
        var адреса = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            адреса.Add(await Поставить());
        }

        var первый = await Разобрать();
        var второй = await Разобрать();

        первый.ShouldBe(3);
        второй.ShouldBe(0, "второй прогон не должен увидеть уже взятое");

        foreach (var адрес in адреса)
        {
            (await Письмо(адрес)).Attempts.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Пока_первый_насос_шлёт_второй_ничего_не_берёт()
    {
        // ⚠ ⚠ Проверяется, что захват ОТМЕЧАЕТ письма сразу, а не по завершении отправки.
        // Первый насос висит внутри отправки, второй в это время приходит за порцией — и
        // обязан уйти ни с чем. Иначе человек получил бы код входа дважды, а предел частоты не
        // сработал бы: письмо в очереди одно.
        //
        // ⚠ ⚠ **Что этот тест НЕ проверяет — `SKIP LOCKED`.** Мутация «убрать его» пережила и
        // прежнюю редакцию теста, и эту (2026-08-29), и причина в устройстве, а не в тесте:
        // захват идёт ОТДЕЛЬНЫМ самофиксирующимся запросом, и блокировки строк снимаются к
        // моменту, когда начинается отправка. Окно, которое стерегёт `SKIP LOCKED`, — это
        // миллисекунды между двумя одновременными `UPDATE`, и детерминированного теста на него
        // нет.
        //
        // Ждали от него не того: план обещал «двойную выдачу» без него. Её не будет — в
        // READ COMMITTED PostgreSQL перечитывает условие после снятия блокировки, и второй
        // увидит уже отодвинутые строки. `SKIP LOCKED` превращает ожидание блокировки в
        // немедленный пропуск; это про пропускную способность, а не про правильность.
        var адреса = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            адреса.Add(await Поставить());
        }

        база.Отправитель.Задержать();

        var первый = Task.Run(() => Разобрать(), Ct);

        // Ждём, пока первый действительно займёт строки: он висит внутри отправки.
        while (база.Отправитель.Задвижка is { Task.IsCompleted: false } && !первый.IsCompleted)
        {
            var занял = await Взято();

            if (занял == 3)
            {
                break;
            }

            await Task.Delay(20, Ct);
        }

        // Второй обязан вернуться СРАЗУ и с нулём, а не ждать первого.
        var второй = await Разобрать().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        второй.ShouldBe(0);

        база.Отправитель.Отпустить();
        (await первый).ShouldBe(3);
    }

    [Fact]
    public async Task Порция_ограничена()
    {
        for (var i = 0; i < MailDelivery.Порция + 5; i++)
        {
            await Поставить();
        }

        (await Разобрать()).ShouldBe(MailDelivery.Порция);
    }

    // --- уборка -----------------------------------------------------------------------------

    [Fact]
    public async Task Уборка_сносит_отправленное_старше_срока()
    {
        var адрес = await Поставить();
        await Разобрать();

        база.Часы.Вперёд(SentMailCleanup.Срок + TimeSpan.FromDays(1));

        (await Убрать()).ShouldBe(1);
        (await Письма(адрес)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Уборка_НЕ_трогает_непосланное_с_ошибкой()
    {
        // ⚠ Строка с ошибкой — единственный след неудачи. Снести её значит стереть ответ на
        // вопрос «почему не дошло» ровно у того письма, ради которого вопрос и задают.
        var адрес = await Поставить();
        база.Отправитель.ПадатьНа = адрес;
        await Разобрать();

        база.Часы.Вперёд(SentMailCleanup.Срок + TimeSpan.FromDays(30));

        (await Убрать()).ShouldBe(0);
        (await Письма(адрес)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Уборка_не_трогает_свежее_отправленное()
    {
        var адрес = await Поставить();
        await Разобрать();

        база.Часы.Вперёд(TimeSpan.FromDays(1));

        (await Убрать()).ShouldBe(0);
        (await Письма(адрес)).Count.ShouldBe(1);
    }

    // --- обвязка ----------------------------------------------------------------------------

    /// <summary>Свои строки лога: общий провайдер ловит и запросы EF.</summary>
    /// <remarks>
    /// ⚠ `StartsWith`, а не `Contains`: текст запроса EF содержит `mail_outbox`, и поиск
    /// подстроки объявлял бы своими все запросы к таблице. Свои строки — структурные сообщения,
    /// и все они начинаются с `mail_`.
    /// </remarks>
    private List<string> Своих() =>
        [.. база.Логи.Строки.Where(с => с.StartsWith("mail_", StringComparison.Ordinal))];

    private static readonly IReadOnlyDictionary<string, string> Пусто =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private async Task<string> Поставить()
    {
        var адрес = $"кому-{Guid.NewGuid():N}@example.com";

        await using var scope = база.Scope();
        var очередь = scope.ServiceProvider.GetRequiredService<IMailQueue>();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        (await очередь.EnqueueAsync(адрес, MailKinds.Test, MailLocales.Ru, Пусто, Ct))
            .ShouldBe(MailQueueResult.Queued);

        await db.SaveChangesAsync(Ct);

        return адрес;
    }

    /// <summary>
    /// Останов процесса сразу после того, как реле приняло письмо, не теряет отметку.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-09-15 (mail-2). Отметка шла с тем же токеном отмены: останов в эти
    /// миллисекунды отменял её, <c>sent_at</c> оставался пустым, и через attempts² минут то же
    /// письмо уходило второй раз. Подставной отправитель «принимает» письмо и тут же
    /// останавливает процесс — ровно окно находки.
    ///
    /// Второе письмо порции после останова не начинается: оно остаётся в очереди.
    /// </remarks>
    [Fact]
    public async Task Останов_сразу_после_отправки_не_теряет_отметку()
    {
        var первый = await Поставить();
        var второй = await Поставить();

        using var останов = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        база.Отправитель.ПослеОтправки = () => останов.Cancel();

        await using (var scope = база.Scope())
        {
            await Should.ThrowAsync<OperationCanceledException>(
                () => scope.ServiceProvider.GetRequiredService<MailDelivery>().RunAsync(останов.Token));
        }

        база.Отправитель.Ушедшие.Count.ShouldBe(1, "после останова началось следующее письмо");

        var ушедшее = база.Отправитель.Ушедшие[0].ToAddress;
        var оставшееся = ушедшее == первый ? второй : первый;

        (await Письмо(ушедшее)).SentAt.ShouldNotBeNull(
            "реле письмо приняло, а отметки нет — оно уйдёт второй раз");
        (await Письмо(оставшееся)).SentAt.ShouldBeNull();
    }

    /// <summary>
    /// Порция уходит одним соединением, а не двадцатью.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>До этапа 60.2 каждое письмо платило полный обряд:</b> <c>Connect</c> + TLS +
    /// <c>Auth</c> + <c>Send</c> + <c>Quit</c>. На порции из двадцати это двадцать рукопожатий
    /// вместо одного, половина времени порции — и прямой повод для реле счесть нас перебирающим
    /// пароли. Числом это видно только здесь: подделка считает открытия, потому что никакой
    /// другой признак «одного соединения» изнутри не наблюдаем.
    ///
    /// ⚠ Закрытие проверяется наравне с открытием: сессия, которую забыли освободить, — это
    /// сокет, живущий до сборки мусора, и реле видит его как оборвавшегося клиента.
    ///
    /// Мутация 2026-09-07: <c>Open</c> перенесён внутрь цикла → покраснел этот факт.
    /// </remarks>
    [Fact]
    public async Task Порция_идёт_одним_соединением()
    {
        for (var номер = 0; номер < 5; номер++)
        {
            await Поставить();
        }

        (await Разобрать()).ShouldBe(5);

        база.Отправитель.Открытий.ShouldBe(1, "соединение на письмо — это обряд на каждое письмо");
        база.Отправитель.Закрытий.ShouldBe(1, "сессию обязаны закрыть, а не бросить сокет");
    }

    /// <summary>
    /// Пустая очередь не открывает соединения вовсе.
    /// </summary>
    /// <remarks>
    /// ⚠ Открывать транспорт до захвата порции значило бы ходить на почтовый сервер каждые
    /// пятнадцать секунд у установки, где писем нет. Порядок в коде именно такой, и это
    /// проверяется, а не подразумевается.
    /// </remarks>
    [Fact]
    public async Task Пустая_очередь_соединения_не_открывает()
    {
        (await Разобрать()).ShouldBe(0);

        база.Отправитель.Открытий.ShouldBe(0);
    }

    private async Task<int> Разобрать()
    {
        await using var scope = база.Scope();

        // Возвращаем «сколько ушло»: существующие проверки говорят про это число, а
        // «сколько взято» появилось этапом 60.2 ради разрежения хода и спрашивается отдельно.
        return (await scope.ServiceProvider.GetRequiredService<MailDelivery>().RunAsync(Ct)).Sent;
    }

    /// <summary>Сколько писем уже взято в работу — по числу попыток.</summary>
    private async Task<int> Взято()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Set<MailOutbox>().CountAsync(п => п.Attempts > 0, Ct);
    }

    private async Task<int> Убрать()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await SentMailCleanup.RunAsync(db, база.Часы, Ct);
    }

    private async Task<MailOutbox> Письмо(string адрес) => (await Письма(адрес)).Single();

    private async Task<List<MailOutbox>> Письма(string адрес)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Set<MailOutbox>()
            .AsNoTracking()
            .Where(п => п.ToAddress == адрес)
            .ToListAsync(Ct);
    }
}
