using System.Security.Cryptography;
using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Modules.Mail.Secrets;
using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Секреты установки: конверт, закрытый набор имён и ротация.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Главный факт файла — испорченный секрет даёт ОТКАЗ, а не «секрета нет».</b> Соблазн
/// обернуть открытие в <c>catch</c> и вернуть <c>null</c> велик: «ну не открылось, значит не
/// настроено». Цена названа образцом дословно — тогда «шлём без авторизации» выглядит как
/// неверный пароль у провайдера, и искать причину будут не там.
///
/// ⚠ Тесты работают со <b>своей</b> строкой и чистят её за собой: таблица секретов одна на базу,
/// а имён в закрытом наборе сегодня одно.
/// </remarks>
[Collection(MailDatabaseCollection.Name)]
public sealed class SecretsTests(MailDatabase база) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Почта, для которой пароль вводят в этих тестах.</summary>
    private static readonly MailPolicy Почта = new(
        Enabled: true,
        Host: "smtp.example.com",
        Port: 587,
        Security: "starttls",
        Username: "robot",
        FromName: "Email SSL Sender",
        FromAddress: "vault@example.com",
        RatePerHour: 5,
        MaxAttempts: 5);

    private static readonly string Цель = SmtpSender.Цель(Почта);

    public ValueTask InitializeAsync()
    {
        база.Конверт.Вернуть();
        база.Гонка.Снять();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        база.Конверт.Вернуть();
        база.Гонка.Снять();

        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        await db.Set<SystemSecret>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Секрет_запечатывается_и_открывается()
    {
        await Задать("пароль-почтового-ящика");

        (await Прочитать()).ShouldBe("пароль-почтового-ящика");
    }

    [Fact]
    public async Task Незаданный_секрет_это_null_а_не_отказ()
    {
        // «Не настроено» — законное состояние: почта выключена по умолчанию.
        (await Прочитать()).ShouldBeNull();
    }

    [Fact]
    public async Task Повторная_запись_перекладывает_а_не_плодит_строки()
    {
        await Задать("первый");
        база.Часы.Вперёд(TimeSpan.FromHours(1));
        await Задать("второй");

        (await Прочитать()).ShouldBe("второй");
        (await Строк()).ShouldBe(1);
    }

    [Fact]
    public async Task Время_и_автор_правки_проставляются()
    {
        var кто = Guid.CreateVersion7();
        база.Личность.Id = кто;

        await Задать("пароль");

        var строка = await Строка();

        строка.UpdatedBy.ShouldBe(кто);
        строка.UpdatedAt.ShouldBe(база.Часы.GetUtcNow());

        // ⚠ И время ОБНОВЛЯЕТСЯ при перезаписи. `onupdate` у EF нет вовсе: колонка, оставленная
        // на серверном умолчании, показывала бы дату первой записи и молчала про все
        // последующие — то есть отвечала бы неправду на вопрос «когда меняли пароль».
        база.Часы.Вперёд(TimeSpan.FromDays(3));
        await Задать("другой");

        (await Строка()).UpdatedAt.ShouldBe(база.Часы.GetUtcNow());
    }

    [Fact]
    public async Task Испорченный_блоб_даёт_ОТКАЗ_а_не_молчание()
    {
        await Задать("пароль");

        // Портим запечатанное — так выглядит авария хранилища или подмена.
        await using (var scope = база.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
            var строка = await db.Set<SystemSecret>().FirstAsync(Ct);
            строка.Sealed = [.. строка.Sealed.Select(б => (byte)(б ^ 0xFF))];
            await db.SaveChangesAsync(Ct);
        }

        await Should.ThrowAsync<CryptographicException>(async () => await Прочитать());
    }

    [Fact]
    public async Task Контекст_AEAD_включает_имя_секрета()
    {
        // ⚠ ⚠ Проверяется ИМЕННО тот контекст, который складывает боевой код, а не то, что
        // конверт вообще смотрит на контекст. Первая редакция открывала заведомо чужой строкой
        // и потому пережила мутацию «убрать имя из контекста»: она доказывала свойство
        // конверта, а не этого файла.
        //
        // Без имени в контексте годная запечатка переставляется с одного секрета на другой:
        // пароль SMTP, положенный на место будущего ключа подписи, открылся бы как он.
        await Задать("пароль");

        var строка = await Строка();
        var ключ = new EmailSslSender.Shared.Crypto.WrappedDataKey(строка.DataKey);
        var запечатано = new EmailSslSender.Shared.Crypto.SealedSecret(строка.Sealed);

        // Полный контекст боевого кода — открывает (внутри — пароль вместе с адресом).
        BoundSecret.Unpack(
                база.Конверт.OpenText(ключ, запечатано, "sender:system-secret:" + SystemSecretNames.SmtpPassword))
            .ShouldNotBeNull().Secret.ShouldBe("пароль");

        // Он же без имени секрета — не открывает.
        Should.Throw<CryptographicException>(
            () => база.Конверт.OpenText(ключ, запечатано, "sender:system-secret:"));

        // И чужое имя — тоже.
        Should.Throw<CryptographicException>(
            () => база.Конверт.OpenText(ключ, запечатано, "sender:system-secret:чужое"));
    }

    [Fact]
    public async Task Имя_не_из_закрытого_набора_отвергается()
    {
        await using var scope = база.Scope();
        var секреты = scope.ServiceProvider.GetRequiredService<SystemSecrets>();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await секреты.PutAsync("что-попало", "значение", Цель, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await секреты.GetForAsync("что-попало", Цель, Ct));

        (await Строк()).ShouldBe(0);
    }

    [Fact]
    public async Task Признак_задан_не_открывает_секрет()
    {
        await using (var scope = база.Scope())
        {
            var секреты = scope.ServiceProvider.GetRequiredService<SystemSecrets>();
            (await секреты.HasAsync(SystemSecretNames.SmtpPassword, Ct)).ShouldBeFalse();
        }

        await Задать("пароль");

        await using (var scope = база.Scope())
        {
            var секреты = scope.ServiceProvider.GetRequiredService<SystemSecrets>();
            (await секреты.HasAsync(SystemSecretNames.SmtpPassword, Ct)).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Сброс_убирает_строку_и_сообщает_была_ли_она()
    {
        await Задать("пароль");

        await using (var scope = база.Scope())
        {
            var (секреты, db) = Слои(scope);
            (await секреты.DropAsync(SystemSecretNames.SmtpPassword, Ct)).ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = база.Scope())
        {
            var (секреты, _) = Слои(scope);
            (await секреты.DropAsync(SystemSecretNames.SmtpPassword, Ct)).ShouldBeFalse();
        }
    }

    // --- привязка к адресу ------------------------------------------------------------------

    [Theory]
    [InlineData("evil.example.com", 587, "starttls", "robot")]
    [InlineData("smtp.example.com", 25, "starttls", "robot")]
    [InlineData("smtp.example.com", 587, "none", "robot")]
    [InlineData("smtp.example.com", 587, "tls", "robot")]
    [InlineData("smtp.example.com", 587, "starttls", "admin")]
    public async Task Пароль_не_открывается_для_другого_сервера_порта_защиты_или_учётки(
        string сервер, int порт, string защита, string учётка)
    {
        // ⚠ Ровно сценарий перехваченной сессии: сервер переставлен на свой (или защита снята), и
        // проверочное письмо понесло бы пароль туда — открытым текстом.
        await Задать("пароль");

        var чужая = SmtpSender.Цель(Почта with { Host = сервер, Port = порт, Security = защита, Username = учётка });

        await using var scope = база.Scope();
        var (секреты, _) = Слои(scope);

        var ошибка = await Should.ThrowAsync<MailConfigurationException>(
            () => секреты.GetForAsync(SystemSecretNames.SmtpPassword, чужая, Ct));
        ошибка.Message.ShouldContain("Введите пароль SMTP заново");
        ошибка.Message.ShouldNotContain("пароль-");

        (await секреты.GetForAsync(SystemSecretNames.SmtpPassword, Цель, Ct)).ShouldBe("пароль");
    }

    [Fact]
    public void Один_сервер_в_разной_записи_одна_цель()
    {
        SmtpSender.Цель(Почта with { Host = "https://SMTP.Example.com/" }).ShouldBe(Цель);
        SmtpSender.Цель(Почта with { Username = "a@b" })
            .ShouldNotBe(SmtpSender.Цель(Почта with { Username = "a", Host = "b@smtp.example.com" }));
    }

    [Fact]
    public async Task Старый_пароль_без_привязки_открывается_только_после_привязки_командой_migrate()
    {
        // ⚠ Пароль, запечатанный до привязки, — голая строка. Во время работы он не открывается
        // (ревью безопасности 2026-10-20: «доверие с первого раза» отдавало его тому, кто первым
        // подменил сервер), а migrate привязывает его к текущим настройкам почты.
        await using (var scope = база.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
            var ключ = база.Конверт.CreateDataKey();

            db.Set<SystemSecret>().Add(new SystemSecret
            {
                Key = SystemSecretNames.SmtpPassword,
                Sealed = база.Конверт.SealText(
                    ключ, "старый-пароль", "sender:system-secret:" + SystemSecretNames.SmtpPassword).Bytes,
                DataKey = ключ.Bytes,
            });

            await db.SaveChangesAsync(Ct);
        }

        база.Логи.Строки.Clear();

        (await Should.ThrowAsync<MailConfigurationException>(() => Прочитать())).Message.ShouldContain("migrate");

        await using (var scope = база.Scope())
        {
            var (секреты, _) = Слои(scope);
            (await секреты.BindLegacyAsync(SystemSecretNames.SmtpPassword, Цель, Ct)).ShouldBeTrue();
            (await секреты.BindLegacyAsync(SystemSecretNames.SmtpPassword, Цель, Ct)).ShouldBeFalse();
        }

        (await Прочитать()).ShouldBe("старый-пароль");

        var строка = await Строка();
        var привязка = BoundSecret.Unpack(база.Конверт.OpenText(
                new WrappedDataKey(строка.DataKey),
                new SealedSecret(строка.Sealed),
                "sender:system-secret:" + SystemSecretNames.SmtpPassword))
            .ShouldNotBeNull("старый пароль перепечатан с привязкой");
        привязка.Target.ShouldBe(Цель);
        привязка.Secret.ShouldBe("старый-пароль");

        база.Логи.Строки.ShouldContain(с => с.Contains("system_secret_bound_by_migrate", StringComparison.Ordinal));
        база.Логи.Строки.ShouldAllBe(с => !с.Contains("старый-пароль", StringComparison.Ordinal));

        await using (var scope = база.Scope())
        {
            var (секреты, _) = Слои(scope);

            await Should.ThrowAsync<MailConfigurationException>(() => секреты.GetForAsync(
                SystemSecretNames.SmtpPassword, SmtpSender.Цель(Почта with { Host = "evil.example.com" }), Ct));
        }

        (await Прочитать()).ShouldBe("старый-пароль");
    }

    // --- ротация ----------------------------------------------------------------------------

    [Fact]
    public async Task Ротация_перекладывает_обёртку_и_секрет_остаётся_читаемым()
    {
        await Задать("пароль");
        var было = (await Строка()).DataKey;

        база.Конверт.Повернуть();

        var итог = await Переложить();

        итог.Seen.ShouldBe(1);
        итог.Rewrapped.ShouldBe(1);

        // ⚠ Обёртка ДРУГАЯ, а секрет тот же: мастер-ключ шифрует ключ данных, а не данные.
        (await Строка()).DataKey.ShouldNotBe(было);
        (await Прочитать()).ShouldBe("пароль");
    }

    [Fact]
    public async Task Повторная_ротация_не_трогает_ни_строки()
    {
        await Задать("пароль");
        база.Конверт.Повернуть();

        (await Переложить()).Rewrapped.ShouldBe(1);

        // ⚠ Идемпотентность — не вежливость: перезапись столбца с ключом единственное место, где
        // сбой стоит секрета. Повторный запуск после обрыва обязан пропускать уже переложенное.
        var второй = await Переложить();

        второй.Seen.ShouldBe(1);
        второй.Rewrapped.ShouldBe(0);
        (await Прочитать()).ShouldBe("пароль");
    }

    [Fact]
    public async Task Перезаданный_до_ротации_секрет_перекладывать_нечего()
    {
        // Дешёвая половина: строка уже под действующей версией, цикл пропускает её до всякого
        // обновления. ⚠ Условное обновление этим НЕ проверяется — см. следующий факт.
        await Задать("первый");
        база.Конверт.Повернуть();
        await Задать("второй");

        var итог = await Переложить();

        итог.Seen.ShouldBe(1);
        итог.Rewrapped.ShouldBe(0, "строка уже под действующей версией — трогать её нечего");
        (await Прочитать()).ShouldBe("второй");
    }

    [Fact]
    public async Task Строка_изменившаяся_между_чтением_и_записью_НЕ_затирается()
    {
        // ⚠ ⚠ Настоящая гонка, устроенная вклиниванием: между `SELECT` порции и `UPDATE` чужое
        // подключение меняет `data_key` — ровно как если бы администратор задал пароль заново.
        // Присваивание вслепую положило бы обёртку от СТАРОГО ключа поверх нового; запечатанное
        // рядом уже новое, и открыть его стало бы нечем. Это не «строка потерялась» — это молча
        // испорченный пароль SMTP, и обнаружился бы он тем, что письма перестали уходить.
        //
        // ⚠ Без этого факта мутация «убрать условие из обновления» переживает весь сьют:
        // снаружи гонку не устроить, она живёт внутри транзакции. Проверено 2026-08-29.
        await Задать("пароль");
        база.Конверт.Повернуть();

        база.Гонка.Взвести(SystemSecretNames.SmtpPassword);

        var итог = await Переложить();

        итог.Seen.ShouldBe(1);
        итог.Rewrapped.ShouldBe(
            0, "строка изменилась между чтением и записью — перекладывать её нельзя");

        // И в базе лежит то, что положила гонка, а не обёртка ротации.
        (await Строка()).DataKey.ShouldBe(new byte[] { 0x00, 0xff, 0x00, 0xff });
    }

    // --- обвязка ----------------------------------------------------------------------------

    /// <summary>
    /// Третья нога условия 17.1: пароля нет ни в одной строке лога.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Предписанная планом мутация «убрать <c>smtp_password</c> из
    /// <c>ISensitiveKeyProvider</c>» не красит НИЧЕГО, и это находка, а не недоделка.</b> Имя
    /// ловится <b>базовым</b> правилом <c>SensitiveKeys.IsSensitive</c> — «секретно, если
    /// секретно последнее слово», а последнее слово здесь <c>password</c>; строка в списке модуля
    /// на этом фоне не работает вовсе.
    ///
    /// ⚠ <b>Проверено мутацией 2026-08-29, и заодно выяснилось, где настоящий сторож.</b> Подмена
    /// <c>IsSensitive</c> на <c>false</c> красит <b>двадцать</b> фактов — весь
    /// <c>ChangesRedactorTests</c>, включая строку <c>[Theory]</c> ровно с именем
    /// <c>smtp_password</c>, плюс <c>ExtensibilityTests</c> и <c>NotifierTests</c>. А вот этот
    /// файл и <c>MailApi</c> она не красит **ни одним фактом**, и причина важнее самой мутации:
    /// пароль не попадает в <c>changes</c> НИКОГДА — модуль пишет туда признак
    /// (<c>set</c>/<c>cleared</c>), а не значение. Редактирование здесь второй рубеж, а не
    /// первый, и проверять его надо там, где он работает.
    ///
    /// ⚠ Строка в <c>MailSensitiveKeys</c> от этого не лишняя: она страхует переименование поля
    /// (скажем, в <c>smtp_credential</c>), при котором правило последнего слова перестанет
    /// срабатывать молча. Но проверку, которая её испытывает, надо строить на имени, которого
    /// базовое правило НЕ ловит, — и такое имя в наборе есть: <c>data_key</c>.
    /// </remarks>
    [Fact]
    public async Task Пароль_не_попадает_ни_в_одну_строку_лога()
    {
        const string пароль = "очень-секретный-пароль-ящика-42";

        база.Логи.Строки.Clear();

        await Задать(пароль);
        (await Прочитать()).ShouldBe(пароль);

        база.Логи.Строки.ShouldNotBeEmpty("иначе проверка сходится на пустом списке");
        база.Логи.Строки.ShouldAllBe(строка => !строка.Contains(пароль, StringComparison.Ordinal));
    }


    private async Task Задать(string значение)
    {
        await using var scope = база.Scope();
        var (секреты, db) = Слои(scope);

        await секреты.PutAsync(SystemSecretNames.SmtpPassword, значение, Цель, Ct);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<string?> Прочитать()
    {
        await using var scope = база.Scope();
        var (секреты, _) = Слои(scope);

        return await секреты.GetForAsync(SystemSecretNames.SmtpPassword, Цель, Ct);
    }

    private async Task<EmailSslSender.Shared.Crypto.RewrapBatch> Переложить()
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider
            .GetRequiredService<SystemSecretsRewrap>()
            .RewrapAsync(200, null, Ct);
    }

    private async Task<SystemSecret> Строка()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Set<SystemSecret>().AsNoTracking().FirstAsync(Ct);
    }

    private async Task<int> Строк()
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return await db.Set<SystemSecret>().CountAsync(Ct);
    }

    private static (SystemSecrets, SenderDbContext) Слои(AsyncServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<SystemSecrets>(),
         scope.ServiceProvider.GetRequiredService<SenderDbContext>());
}
