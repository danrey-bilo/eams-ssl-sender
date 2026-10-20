using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Второй фактор: включение, вход, восстановление, отключение.
/// </summary>
/// <remarks>
/// Условие завершения этапа звучит так: <b>ни один путь получения секрета не обходит
/// фактор</b>. На Python-версии выгрузка обходила — проверка стояла ровно в двух местах, а
/// третье про неё не знало. Здесь путь выдачи токенов ровно один, и фактор стоит на нём.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class TwoFactorTests(IdentityDatabase database)
{
    [Fact]
    public async Task Заведённый_но_не_подтверждённый_фактор_не_действует()
    {
        // ⚠ Два состояния, а не одно. Нажавший «включить» и не сумевший отсканировать код
        // иначе запер бы себя снаружи собственной учётной записи.
        var (id, email) = await UserAsync();

        await InFactorsAsync(id, factors => factors.SetupAsync(Ct));

        var status = await InFactorsAsync(id, factors => factors.StatusAsync(Ct));

        status.Enrolled.ShouldBeTrue();
        status.Confirmed.ShouldBeFalse();

        // И вход по-прежнему проходит одним паролем.
        await Should.NotThrowAsync(() => SignInAsync(email));
    }

    [Fact]
    public async Task Подтверждение_включает_фактор()
    {
        var (id, _) = await UserAsync();
        var setup = await InFactorsAsync(id, factors => factors.SetupAsync(Ct));

        await InFactorsAsync(id, async factors =>
        {
            await factors.ConfirmAsync(CodeFor(setup.Secret), Ct);

            return true;
        });

        (await InFactorsAsync(id, factors => factors.StatusAsync(Ct))).Confirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task Секрет_лежит_в_базе_запечатанным()
    {
        // ⚠ Первый по-настоящему обратимый секрет в системе: пароли и токены только
        // хешируются, а seed надо уметь развернуть. Значит его можно и не запечатать — и
        // никто бы этого не заметил, пока не открыл базу.
        var (id, _) = await UserAsync();
        var setup = await InFactorsAsync(id, factors => factors.SetupAsync(Ct));

        var raw = await database.ReadAsync(db => db.Set<UserTwoFactor>()
            .Where(row => row.UserId == id)
            .Select(row => row.Secret)
            .FirstAsync(Ct));

        var secret = TestTotp.Decode(setup.Secret);

        Contains(raw, secret).ShouldBeFalse("seed лежит в базе открытым");
        raw.Length.ShouldBeGreaterThan(secret.Length, "шифротекст с заголовком и тегом длиннее");
    }

    // --- вход ------------------------------------------------------------------------------

    [Fact]
    public async Task Вход_после_включения_требует_кода()
    {
        var (id, email) = await UserAsync();

        await EnableAsync(id);

        var error = await Should.ThrowAsync<AppException>(() => SignInAsync(email));

        error.Code.ShouldBe("TWO_FACTOR_REQUIRED");
        error.Details.ShouldNotBeNull();
        error.Details!["challenge"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Расписка_и_код_дают_токены()
    {
        var (id, email) = await UserAsync();
        var secret = await EnableAsync(id);

        var challenge = await ChallengeAsync(email);
        var pair = (await InSessionsAsync(sessions =>
            sessions.CompleteTwoFactorAsync(
                challenge, CodeFor(secret, steps: 1), Origin, cancellationToken: Ct))).Pair;

        pair.Access.ShouldNotBeNullOrWhiteSpace();

        // И токен работает: путь выдачи один, и фактор его не сломал.
        (await ПроверитьAsync(pair.Access)).ShouldNotBeNull();
    }

    /// <summary>
    /// Подбор кода упирается в блокировку — как подбор пароля.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>До 2026-08-22 счётчик неудач двигал только разбор пароля.</b> Неверный код
    /// писался в журнал и не считался ничем — то есть блокировки на подбор второго фактора не
    /// было вовсе. Расписка гасится, да, но берётся заново одним запросом входа, если пароль
    /// известен; а известен он ровно в том случае, ради которого второй фактор и заводят.
    /// Между чужим и учётной записью оставались шесть цифр и терпение.
    ///
    /// Порог здесь снижен настройкой до трёх: гонять десять неудачных входов ради того же
    /// утверждения — это тридцать проверок пароля argon2 на пустом месте.
    /// </remarks>
    /// <summary>
    /// Включение фактора закрывает прежние сессии.
    /// </summary>
    /// <remarks>
    /// ⚠ Второй фактор включают ровно тогда, когда заподозрили кражу. Токен обновления,
    /// украденный до этого момента, кода не спрашивает <b>никогда</b> — путь обновления про
    /// фактор не знает — и молол бы новые пары до конца абсолютного срока сессии, то есть до
    /// двенадцати часов. Человек при этом считает, что защитился. Найдено разбором 2026-08-22.
    /// </remarks>
    [Fact]
    public async Task Включение_фактора_НЕ_закрывает_прежние_сессии()
    {
        var (id, email) = await UserAsync();
        var pair = await SignInAsync(email);

        await EnableAsync(id);

        // ⚠ ⚠ Прежняя редакция требовала ОБРАТНОГО, и она была права при своём условии: пока
        // подтверждение спрашивало пароль, гашение закрывало кражу. Пароль снят (решение 0061),
        // и та же строка стала оружием: севший за незапертый компьютер включал бы фактор со
        // своего аутентификатора и одним действием выбрасывал хозяина отовсюду.
        //
        // ⚠ Защита не потеряна, а перенесена в осознанное действие: «выйти из всех сессий»
        // живёт своей кнопкой на экране сессий.
        await Should.NotThrowAsync(
            () => ПроверитьAsync(pair.Access),
            "включение фактора выбросило владельца — ровно то, чем это стало бы без пароля");

        await Should.NotThrowAsync(() => ОбменятьAsync(pair.Refresh));
    }

    /// <summary>
    /// Смена пароля гасит выданную расписку второго фактора.
    /// </summary>
    /// <remarks>
    /// ⚠ Расписку выдают ПОСЛЕ проверки пароля и до кода. Тот, кто знал прежний пароль, мог
    /// держать её на руках и завершить вход уже после смены — то есть смена пароля не
    /// закрывала бы дверь, которую сама же и приоткрыла. Сессии при смене закрываются; расписка
    /// — не сессия, и её пропустили. Найдено разбором 2026-08-22.
    /// </remarks>
    /// <summary>
    /// Администратор снимает фактор тому, кто потерял и приложение, и коды.
    /// </summary>
    /// <remarks>
    /// ⚠ До 2026-08-22 такого пути не было вовсе: человек оказывался заперт снаружи
    /// собственной учётной записи навсегда, и помочь ему было нечем, кроме прямого доступа к
    /// базе. Если такой человек — единственный суперпользователь, заперта вся система.
    ///
    /// Сессии снимаемого закрываются: фактор снимают, когда телефон потерян или украден.
    /// </remarks>
    [Fact]
    public async Task Снятие_фактора_администратором_возвращает_вход()
    {
        var (id, email) = await UserAsync();

        await EnableAsync(id);

        // До снятия вход упирается во второй фактор.
        (await Should.ThrowAsync<AppException>(() => SignInAsync(email))).Code
            .ShouldBe("TWO_FACTOR_REQUIRED");

        database.Actor.AsSuperuser();

        (await InFactorsAsync(factors => factors.ResetForAsync(id, Ct))).ShouldBeTrue();

        // Теперь вход обычный, кода не спрашивают.
        await Should.NotThrowAsync(() => SignInAsync(email));

        // Повтор — не ошибка, а подтверждение состояния.
        (await InFactorsAsync(factors => factors.ResetForAsync(id, Ct))).ShouldBeFalse();
    }

    /// <summary>Позвать службу фактора, не подменяя актора.</summary>
    private async Task<T> InFactorsAsync<T>(Func<TwoFactorService, Task<T>> action)
    {
        await using var scope = database.Scope();

        return await action(scope.ServiceProvider.GetRequiredService<TwoFactorService>());
    }

    [Fact]
    public async Task Смена_пароля_гасит_расписку_фактора()
    {
        var (id, email) = await UserAsync();

        await EnableAsync(id);

        var challenge = await ChallengeAsync(email);

        database.Actor.Become(id);

        await database.InScopeAsync<bool>(async users =>
        {
            await users.ChangePasswordAsync(Strong, "Другой-Очень-Хороший-Пароль-77!", Ct);

            return true;
        });

        var error = await Should.ThrowAsync<AppException>(() => InSessionsAsync(
            sessions => sessions.CompleteTwoFactorAsync(challenge, "000000", Origin, cancellationToken: Ct)));

        error.Code.ShouldBe("SESSION_EXPIRED", "расписка пережила смену пароля");
    }

    /// <summary>
    /// Неверный пароль при переспросе считается и пишется в журнал — как на входе.
    /// </summary>
    /// <remarks>
    /// ⚠ До 2026-08-24 здесь стояла голая проверка: ни счётчика, ни строки, ни блокировки. То
    /// есть угнанная вкладка перебирала пароль владельца через отключение фактора — бесшумно
    /// и без ограничений, — а именно этот сценарий переспросом и закрывают.
    /// </remarks>
    [Fact]
    public async Task Неверный_пароль_при_переспросе_считается_неудачей()
    {
        var (id, _) = await UserAsync();

        await EnableAsync(id);

        database.Actor.Become(id);

        var было = database.Audit.Entries.Count;

        // ⚠ Переспрос пароля живёт у ОТКЛЮЧЕНИЯ: перевыпуска кодов больше нет (решение 0061),
        // а включение пароля не спрашивает. Проверяется тот же механизм на единственном месте,
        // где он остался, — и это единственное место как раз самое важное.
        await Should.ThrowAsync<AppException>(() => InFactorsAsync(async factors =>
        {
            await factors.DisableAsync("совсем-не-тот-пароль", Ct);

            return true;
        }));

        (await FailuresAsync(id)).ShouldBe(1, "неудачный переспрос не засчитан");

        database.Audit.Entries.Count.ShouldBeGreaterThan(было, "отказ не попал в журнал");

        // А верный пароль проходит — иначе проверка годилась бы и для сломанного наглухо.
        await Should.NotThrowAsync(() => InFactorsAsync(async factors =>
        {
            await factors.DisableAsync(Strong, Ct);

            return true;
        }));
    }

    /// <summary>Запертая учётная запись не проходит и переспрос.</summary>
    /// <remarks>
    /// Иначе блокировка закрывала бы одну дверь из двух: подбор через вход упирается в порог,
    /// а подбор через отключение фактора продолжался бы как ни в чём не бывало.
    /// </remarks>
    [Fact]
    public async Task Запертая_запись_не_проходит_переспрос()
    {
        var (id, _) = await UserAsync();

        await EnableAsync(id);
        await LockAsync(id);

        database.Actor.Become(id);

        // Пароль верный, но учётная запись заперта.
        await Should.ThrowAsync<AppException>(() => InFactorsAsync(async factors =>
        {
            await factors.DisableAsync(Strong, Ct);

            return true;
        }));
    }

    /// <summary>Подтверждение фактора пароля не спрашивает, а неверный код не пропускает.</summary>
    /// <remarks>
    /// ⚠ ⚠ Решение владельца 0061. Сессия уже доказала, кто это; код доказывает, что
    /// аутентификатор в руках. Проверка парная намеренно: половина «пароль не нужен» зеленела бы
    /// и у метода, который пускает вообще всё.
    /// </remarks>
    [Fact]
    public async Task Подтверждение_фактора_пароля_не_требует_но_код_проверяет()
    {
        var (id, _) = await UserAsync();

        database.Actor.Become(id);

        var setup = await InFactorsAsync(factors => factors.SetupAsync(Ct));

        var отказ = await Should.ThrowAsync<AppException>(() => InFactorsAsync(async factors =>
        {
            await factors.ConfirmAsync("000000", Ct);

            return true;
        }));

        отказ.Code.ShouldBe("TWO_FACTOR_INVALID");

        await Should.NotThrowAsync(() => InFactorsAsync(async factors =>
        {
            await factors.ConfirmAsync(CodeFor(setup.Secret), Ct);

            return true;
        }));
    }

    /// <summary>Счётчик неудачных попыток прямо из таблицы.</summary>
    private Task<int> FailuresAsync(Guid id) =>
        database.ReadAsync(db => db.Set<EmailSslSender.Modules.Identity.Domain.User>()
            .Where(row => row.Id == id)
            .Select(row => row.FailedLoginCount)
            .FirstAsync(Ct));

    [Fact]
    public async Task Подбор_кода_приводит_к_блокировке()
    {
        var (id, email) = await UserAsync();

        await EnableAsync(id);

        database.Settings.Values["auth.lockout_threshold"] = 3;

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var challenge = await ChallengeAsync(email);

                await Should.ThrowAsync<AppException>(() => InSessionsAsync(sessions =>
                    sessions.CompleteTwoFactorAsync(challenge, "000000", Origin, cancellationToken: Ct)));
            }

            // Четвёртый вход не доходит даже до расписки: учётная запись заперта, и ВЕРНЫЙ
            // пароль больше не работает. Код отказа при этом тот же, что у неверного пароля,
            // — нарочно: иначе форма входа рассказывала бы, какие адреса существуют и какие
            // из них под блокировкой.
            var error = await Should.ThrowAsync<AppException>(() => SignInAsync(email));

            error.Code.ShouldBe("INVALID_CREDENTIALS");

            // А без блокировки тот же вызов дошёл бы до второго фактора — это проверено
            // соседним тестом, где вход отвечает TWO_FACTOR_REQUIRED.
        }
        finally
        {
            database.Settings.Values.Remove("auth.lockout_threshold");
        }
    }

    /// <summary>Запертая учётная запись не завершает вход и по уже выданной расписке.</summary>
    /// <remarks>
    /// Расписку могли взять за секунду до того, как порог сработал. Блокировка обязана
    /// закрывать все двери, а не ту, через которую вошли в прошлый раз.
    /// </remarks>
    [Fact]
    public async Task Запертая_запись_не_завершает_вход_по_старой_расписке()
    {
        var (id, email) = await UserAsync();
        var secret = await EnableAsync(id);

        var challenge = await ChallengeAsync(email);

        await LockAsync(id);

        var error = await Should.ThrowAsync<AppException>(() => InSessionsAsync(sessions =>
            sessions.CompleteTwoFactorAsync(
                challenge, CodeFor(secret, steps: 1), Origin, cancellationToken: Ct)));

        error.Code.ShouldBe("INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Опечатка_позволяет_повторить_код_в_той_же_расписке()
    {
        // ⚠ Иначе одна расписка даёт неограниченное число попыток подобрать шестизначный
        // код: миллион вариантов при неограниченных попытках — это минуты.
        var (id, email) = await UserAsync();
        var secret = await EnableAsync(id);

        var challenge = await ChallengeAsync(email);

        await Should.ThrowAsync<AppException>(() => InSessionsAsync(sessions =>
            sessions.CompleteTwoFactorAsync(challenge, "000000", Origin, cancellationToken: Ct)));

        // Тот же challenge с ПРАВИЛЬНЫМ кодом больше не годится.
        await InSessionsAsync(sessions => sessions.CompleteTwoFactorAsync(
            challenge, CodeFor(secret, steps: 1), Origin, cancellationToken: Ct));
    }

    [Fact]
    public async Task Один_код_нельзя_предъявить_дважды()
    {
        // ⚠ Подсмотренный через плечо код живёт тридцать секунд, и без памяти о принятом
        // шаге его хватает на второй вход.
        var (id, email) = await UserAsync();
        var secret = await EnableAsync(id);

        var code = CodeFor(secret, steps: 1);

        var challenge1 = await ChallengeAsync(email);

        await InSessionsAsync(sessions =>
            sessions.CompleteTwoFactorAsync(challenge1, code, Origin, cancellationToken: Ct));

        var second = await ChallengeAsync(email);

        var error = await Should.ThrowAsync<AppException>(() => InSessionsAsync(sessions =>
            sessions.CompleteTwoFactorAsync(second, code, Origin, cancellationToken: Ct)));

        error.Code.ShouldBe("TWO_FACTOR_INVALID");
    }

    /// <summary>
    /// Потерявший аутентификатор просит код письмом — той же распиской, с того же экрана.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Это ЗАМЕНА кодов восстановления</b> (решение владельца 0061), и проверяется здесь
    /// ровно то, ради чего замена делалась: человек с включённым аутентификатором, у которого
    /// телефона в руках нет, входит письмом и не зовёт администратора.
    ///
    /// ⚠ <b>Расписка переживает просьбу.</b> Погасив её, мы отобрали бы вход у того, кто как раз
    /// попросил помощи: расписки нет, код ещё не пришёл.
    /// </remarks>
    [Fact]
    public async Task Потерявший_аутентификатор_входит_кодом_из_письма()
    {
        var (id, email) = await UserAsync();

        await EnableAsync(id);

        await СПочтойAsync(async () =>
        {
            var расписка = await ChallengeAsync(email);

            await InSessionsAsync(async sessions =>
            {
                await sessions.SendEmailCodeAsync(расписка, Ct);

                return true;
            });

            var письмо = database.Почта.Последнее(email);

            письмо.ShouldNotBeNull("код письмом не ушёл — запасного пути у фактора не осталось");

            var пара = await InSessionsAsync(sessions =>
                sessions.CompleteTwoFactorAsync(
                    расписка, письмо.Values["code"], Origin, cancellationToken: Ct));

            пара.Pair.Access.ShouldNotBeNullOrWhiteSpace();
        });
    }

    /// <summary>По выдуманной расписке письмо не уходит.</summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Адрес открыт по существу</b> (пришедший сюда ещё не вошёл), и потому расписка —
    /// единственное, что его защищает. Без проверки он превратился бы в рассылку писем на любой
    /// адрес по одному запросу: и шум человеку, и подтверждение того, что учётная запись
    /// существует.
    ///
    /// Мутация 2026-09-04: <c>PeekAsync</c> заменён на «первая живая расписка второго фактора,
    /// какая найдётся» → покраснели ДВЕ проверки — эта и «потерявший аутентификатор входит кодом
    /// из письма». Вторая покраснела потому, что письмо ушло не тому человеку.
    /// </remarks>
    [Fact]
    public async Task Негодная_расписка_письма_не_шлёт()
    {
        var (id, email) = await UserAsync();

        await EnableAsync(id);

        await СПочтойAsync(async () =>
        {
            // Настоящая расписка выдана — предъявим не её.
            await ChallengeAsync(email);

            var было = database.Почта.Отправленные.Count;

            var отказ = await Should.ThrowAsync<AppException>(() => InSessionsAsync(async sessions =>
            {
                await sessions.SendEmailCodeAsync("совсем-не-расписка", Ct);

                return true;
            }));

            отказ.Code.ShouldBe("INVALID_CREDENTIALS");

            database.Почта.Отправленные.Count.ShouldBe(
                было, "письмо ушло по выдуманной расписке — адрес стал рассылкой");
        });
    }

    /// <summary>Вызов называет и способ, и то, чем ещё можно подтвердить вход.</summary>
    /// <remarks>
    /// ⚠ Без этого списка экран «нет доступа к коду?» пришлось бы рисовать наугад — и на
    /// установке без почты он предлагал бы кнопку в никуда.
    /// </remarks>
    [Fact]
    public async Task Вызов_называет_доступные_способы()
    {
        var (id, email) = await UserAsync();

        await EnableAsync(id);

        await СПочтойAsync(async () =>
        {
            var отказ = await Should.ThrowAsync<AppException>(() => SignInAsync(email));

            отказ.Code.ShouldBe("TWO_FACTOR_REQUIRED");
            отказ.Details.ShouldNotBeNull();
            отказ.Details!["method"].ShouldBe("totp");

            var доступны = отказ.Details["available"]
                .ShouldBeAssignableTo<IReadOnlyList<string>>()
                .ShouldNotBeNull();

            доступны.ShouldContain("totp");
            доступны.ShouldContain("email", "иначе запасного пути у потерявшего телефон не видно");
        });
    }

    /// <summary>
    /// Прогнать дело на установке С НАСТРОЕННОЙ почтой и вернуть всё как было.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Настроенная почта — глобальное состояние стенда, и оставить её включённой нельзя:
    /// <see cref="SecondFactorPolicy"/> начнёт требовать фактор письмом от КАЖДОГО, кто способ
    /// себе не выбирал, и соседние проверки станут падать по причине, не имеющей к ним
    /// отношения. Возврат в <c>finally</c> обязателен по той же причине.
    /// </remarks>
    private async Task СПочтойAsync(Func<Task> дело)
    {
        database.Почта.Настроена = true;
        database.Почта.Исход = MailQueueResult.Queued;

        try
        {
            await дело();
        }
        finally
        {
            database.Почта.Настроена = false;
            database.Почта.Исход = MailQueueResult.NotConfigured;
        }
    }

    // --- отключение и перевыпуск --------------------------------------------------------------

    [Fact]
    public async Task Отключение_требует_пароля()
    {
        // ⚠ Иначе угнанная вкладка снимает второй фактор — то есть он защищает ровно до
        // первой угнанной вкладки, а именно от неё и заводится.
        var (id, _) = await UserAsync();

        await EnableAsync(id);

        var error = await Should.ThrowAsync<AppException>(() =>
            InFactorsAsync(id, async factors =>
            {
                await factors.DisableAsync("Не-Тот-Пароль-1!", Ct);

                return true;
            }));

        error.Code.ShouldBe("INVALID_CREDENTIALS");
        (await InFactorsAsync(id, factors => factors.StatusAsync(Ct))).Confirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task Отключение_приложения_не_разрешает_вход_одним_паролем()
    {
        var (id, email) = await UserAsync();

        await EnableAsync(id);

        await InFactorsAsync(id, async factors =>
        {
            await factors.DisableAsync(Strong, Ct);

            return true;
        });

        (await Should.ThrowAsync<AppException>(() => SignInAsync(email))).Code.ShouldBe(ErrorCodes.TwoFactorUnavailable);
    }

    // --- политика ---------------------------------------------------------------------------

    [Fact]
    public async Task От_обычного_человека_тоже_требуется_фактор()
    {
        var (id, _) = await UserAsync();

        (await InFactorsAsync(id, factors => factors.StatusAsync(Ct))).Required.ShouldBeTrue();
    }

    [Fact]
    public async Task Настройка_включает_требование_для_всех()
    {
        var (id, _) = await UserAsync();

        database.Settings.Values["auth.totp_required"] = true;

        try
        {
            (await InFactorsAsync(id, factors => factors.StatusAsync(Ct))).Required.ShouldBeTrue();
        }
        finally
        {
            database.Settings.Values.Remove("auth.totp_required");
        }
    }

    // --- обвязка -----------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    private static readonly RequestOrigin Origin =
        new(System.Net.IPAddress.Parse("10.0.0.5"), "EAMS-Tests/1.0");

    private async Task<(Guid Id, string Email)> UserAsync()
    {
        database.Actor.AsSuperuser();

        var email = $"totp-{Guid.CreateVersion7():N}@example.com";
        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Тестовый человек", Strong, MustChangePassword: false), Ct));

        return (user.Id, email);
    }

    /// <summary>Включить и подтвердить фактор. Возвращает секрет в base32.</summary>
    private async Task<string> EnableAsync(Guid id)
    {
        var setup = await InFactorsAsync(id, factors => factors.SetupAsync(Ct));

        await InFactorsAsync(id, async factors =>
        {
            await factors.ConfirmAsync(CodeFor(setup.Secret), Ct);

            return true;
        });

        return setup.Secret;
    }

    /// <summary>
    /// Код второго фактора на почту доходит ДО БАЗЫ.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Заведён вскрытием 26.1 по настоящему дефекту, а не для полноты.</b>
    /// <c>Purposes.All</c> содержал <c>login_code</c> с этапа 17, а ограничение
    /// <c>ck_identity_tokens_purpose</c> в схеме — нет: три значения против четырёх. Вставка
    /// отвергалась базой, то есть <b>вход со вторым фактором «код на почту» падал целиком</b>
    /// — не деградировал, а падал.
    ///
    /// ⚠ Не поймал этого никто, и стоит понимать почему. <c>HasPendingModelChanges</c> сверяет
    /// модель с миграциями — а они были согласованы между собой, ошибка жила в обеих.
    /// Единственный тест, упоминавший <c>LoginCodes</c>, доставал службу из контейнера и выдачу
    /// не звал. Проверять надо было то, что уходит В БАЗУ.
    ///
    /// ⚠ Страница <c>docs/code/…/Domain/IdentityToken.md</c> предупреждала об этом дословно:
    /// «новое назначение — три правки в трёх местах, а не одна константа». Сделали одну.
    ///
    /// мутация 2026-09-02, и первая попытка мерила не то. Откат ограничения к трём значениям
    /// краснит факт <b>внешним</b> сторожем — <c>PendingModelChangesWarning</c>: модель
    /// разошлась с миграциями, и до вставки дело не доходит. Чтобы дойти до самого ограничения,
    /// мутация обязана оставить модель и миграции СОГЛАСОВАННЫМИ: подмена значения
    /// <c>Purposes.LoginCode</c> на <c>"login_code_x"</c> даёт
    /// <c>Npgsql.PostgresException 23514: violates check constraint
    /// "ck_identity_tokens_purpose"</c> — то, ради чего факт и написан.
    ///
    /// ⚠ Отсюда видно, что сторожа два и они слоями: один сверяет модель с миграциями, второй
    /// (этот) — что записанное значение вообще принимается базой. Первый второго не заменяет:
    /// опечатка в новом значении прошла бы его насквозь.
    /// </remarks>
    [Fact]
    public async Task Код_на_почту_доходит_до_базы()
    {
        var (id, _) = await UserAsync();

        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<Shared.Data.SenderDbContext>();

        var человек = await db.Set<User>().FirstAsync(строка => строка.Id == id, Ct);

        await СПочтойAsync(async () =>
            await scope.ServiceProvider.GetRequiredService<LoginCodes>().IssueAsync(человек, Ct));

        // ⚠ Сохраняет ВЫЗЫВАЮЩИЙ: `IssueAsync` только кладёт строку в трекер, а `SignInService`
        // делает `SaveChangesAsync` следом. Значит и отказ ограничения приходит здесь — на
        // сохранении, а не на выдаче; проверять надо то же самое место.
        await db.SaveChangesAsync(Ct);

        var выдано = await db.Set<IdentityToken>()
            .AsNoTracking()
            .CountAsync(
                строка => строка.UserId == id
                    && строка.Purpose == Purposes.LoginCode
                    && строка.UsedAt == null,
                Ct);

        выдано.ShouldBe(1, "код второго фактора на почту не доехал до базы");
    }

    /// <summary>
    /// Совпавший код у двоих не роняет выдачу — и оба кода живут, каждый у своего.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Заменяет факт «двух живых кодов с одним значением не бывает»</b> (ревью 2026-09-15,
    /// S2, logincode-1). Тот закреплял уникальный индекс как защиту глобального гашения — и
    /// вместе с ним закреплял дефект: индекс стоит и на погашенных строках, живущих до уборки
    /// месяц, и совпадение нового кода с любой из них роняло вход пятисоткой с вероятностью
    /// «строк в таблице / 10⁶». Теперь хеш кода привязан к человеку, и совпадения значений у
    /// разных людей база не видит вовсе.
    /// </remarks>
    [Fact]
    public async Task Совпавший_код_у_двоих_не_роняет_выдачу()
    {
        var (первый, _) = await UserAsync();
        var (второй, _) = await UserAsync();

        const string Общий = "424242";

        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<Shared.Data.SenderDbContext>();
        var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();

        await invitations.IssueCodeAsync(первый, Purposes.LoginCode, TimeSpan.FromMinutes(10), Общий, Ct);
        await db.SaveChangesAsync(Ct);

        await invitations.IssueCodeAsync(второй, Purposes.LoginCode, TimeSpan.FromMinutes(10), Общий, Ct);

        await Should.NotThrowAsync(
            () => db.SaveChangesAsync(Ct), "совпадение кода у двоих уронило выдачу — вход ответил бы пятисоткой");

        (await invitations.ConsumeCodeAsync(второй, Общий, Purposes.LoginCode, Ct)).ShouldBe(второй);
        (await invitations.ConsumeCodeAsync(первый, Общий, Purposes.LoginCode, Ct)).ShouldBe(первый);
    }

    /// <summary>Свой прежний, уже погашенный код с тем же значением выдачу не роняет.</summary>
    [Fact]
    public async Task Повтор_своего_погашенного_кода_не_роняет_выдачу()
    {
        var (кто, _) = await UserAsync();

        const string Код = "616161";

        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<Shared.Data.SenderDbContext>();
        var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();

        await invitations.IssueCodeAsync(кто, Purposes.LoginCode, TimeSpan.FromMinutes(10), Код, Ct);
        await db.SaveChangesAsync(Ct);

        (await invitations.ConsumeCodeAsync(кто, Код, Purposes.LoginCode, Ct)).ShouldBe(кто);

        await invitations.IssueCodeAsync(кто, Purposes.LoginCode, TimeSpan.FromMinutes(10), Код, Ct);

        await Should.NotThrowAsync(
            () => db.SaveChangesAsync(Ct), "мёртвая строка своего прежнего кода уронила выдачу");

        (await invitations.ConsumeCodeAsync(кто, Код, Purposes.LoginCode, Ct)).ShouldBe(кто, "новый код не принят");
    }

    /// <summary>Чужой ввод, совпавший с живым кодом другого человека, этот код не гасит.</summary>
    [Fact]
    public async Task Чужой_ввод_не_гасит_живой_код_другого()
    {
        var (первый, _) = await UserAsync();
        var (второй, _) = await UserAsync();

        const string Код = "515151";

        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<Shared.Data.SenderDbContext>();
        var invitations = scope.ServiceProvider.GetRequiredService<Invitations>();

        await invitations.IssueCodeAsync(первый, Purposes.LoginCode, TimeSpan.FromMinutes(10), Код, Ct);
        await db.SaveChangesAsync(Ct);

        (await invitations.ConsumeCodeAsync(второй, Код, Purposes.LoginCode, Ct))
            .ShouldBeNull("чужой код принят другому человеку");

        (await invitations.ConsumeCodeAsync(первый, Код, Purposes.LoginCode, Ct))
            .ShouldBe(первый, "чужой неверный ввод погасил живой код владельца");
    }

    /// <summary>Первый шаг входа: получить расписку.</summary>
    private async Task<string> ChallengeAsync(string email)
    {
        var error = await Should.ThrowAsync<AppException>(() => SignInAsync(email));

        error.Code.ShouldBe("TWO_FACTOR_REQUIRED");

        return (string)error.Details!["challenge"]!;
    }

    private async Task<TokenPair> SignInAsync(string email) =>
        (await InSessionsAsync(
            sessions => sessions.SignInAsync(email, Strong, Origin, cancellationToken: Ct))).Pair;

    private async Task<T> InSessionsAsync<T>(Func<SignInService, Task<T>> action)
    {
        await using var scope = database.Scope();

        return await action(scope.ServiceProvider.GetRequiredService<SignInService>());
    }

    /// <summary>Позвать службу фактора от имени конкретного человека.</summary>
    private async Task<T> InFactorsAsync<T>(Guid id, Func<TwoFactorService, Task<T>> action)
    {
        database.Actor.Become(id);

        await using var scope = database.Scope();

        return await action(scope.ServiceProvider.GetRequiredService<TwoFactorService>());
    }

    /// <summary>
    /// Проверить секрет и обменять его — через ПУБЛИЧНЫЕ контракты соседа.
    /// </summary>
    /// <remarks>
    /// ⚠ Строки токенов уехали в модуль токенов (этап 10.5.4) и внутрь его нам не заглянуть.
    /// Свойство от этого проверяется даже точнее: важно не «что лежит в таблице», а «чем
    /// ещё можно войти».
    /// </remarks>
    private async Task<TokenIdentity?> ПроверитьAsync(string access)
    {
        await using var scope = database.Scope();

        return await scope.ServiceProvider
            .GetRequiredService<ITokenIntrospector>()
            .IntrospectAsync(access, Ct);
    }

    private async Task<TokenPair> ОбменятьAsync(string refresh)
    {
        await using var scope = database.Scope();

        return await scope.ServiceProvider
            .GetRequiredService<ITokenIssuer>()
            .RotateAsync(refresh, Origin, Ct);
    }

    /// <summary>Запереть учётную запись, не гоняя перебор.</summary>
    private Task LockAsync(Guid id) =>
        database.ReadAsync(db => db.Set<EmailSslSender.Modules.Identity.Domain.User>()
            .Where(row => row.Id == id)
            .ExecuteUpdateAsync(
                set => set.SetProperty(row => row.LockedUntil, DateTimeOffset.UtcNow.AddMinutes(15)), Ct));

    /// <summary>
    /// Код, который показало бы приложение сейчас или через <paramref name="steps"/> шагов.
    /// </summary>
    /// <remarks>
    /// ⚠ Сдвиг нужен не для красоты. Подтверждение включения ТРАТИТ свой шаг — иначе тот же
    /// код годился бы и для входа сразу после включения, а это ровно то повторное
    /// использование, от которого защита и стоит. В жизни человек между включением и входом
    /// тратит больше тридцати секунд; в тесте приходится взять следующий код.
    /// </remarks>
    private static string CodeFor(string base32, int steps = 0) => TestTotp.Code(base32, steps);

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
