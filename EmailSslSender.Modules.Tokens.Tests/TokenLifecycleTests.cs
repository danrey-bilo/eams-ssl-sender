using EmailSslSender.Modules.Tokens.Contracts;
using EmailSslSender.Modules.Tokens.Domain;
using EmailSslSender.Modules.Tokens.Tokens;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Tokens.Tests;

/// <summary>
/// Жизнь предъявляемого токена: выдача, обновление, кража, отзыв.
/// </summary>
/// <remarks>
/// <b>Приехало из <c>Identity/SessionTests.cs</c> (этап 10.5.4)</b> — та его часть, что про
/// сами секреты. Проверки входа — пароль, блокировка после неудач, счётчик — остались у
/// соседа: они про учётную запись, а не про токен.
///
/// ⚠ <b>Человек здесь ВЫДУМЫВАЕТСЯ, и строки в таблице людей не заводится.</b> У наших
/// токенов нет внешнего ключа на <c>users</c> (правило 1), и это делает сьют модуля
/// независимым от соседа целиком. Приятное следствие границы, а не срезанный угол.
///
/// Самая дорогая по последствиям часть модуля: дефект здесь либо выбрасывает человека из
/// системы на ровном месте, либо пускает чужого.
/// </remarks>
[Collection(TokenDatabaseCollection.Name)]
public sealed class TokenLifecycleTests(TokenDatabase database)
{
    [Fact]
    public async Task Токен_доступа_узнаёт_человека()
    {
        var id = Кто();

        var pair = await IssueAsync(id);
        var resolved = await ПроверитьAsync(pair.Access);

        resolved.ShouldNotBeNull();
        resolved!.UserId.ShouldBe(id);
        resolved.TokenId.ShouldBe(pair.TokenId);
    }

    /// <summary>
    /// Закрытая строка перестаёт пускать немедленно, а не по истечении срока.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Имя проверки исправлено 2026-08-25: она про ОТЗЫВ, а не про отключение
    /// человека.</b> Прежнее имя стало ложью в тот день, когда владелец решил, что отключение
    /// токенов не гасит вовсе: проверка ничего не сломала и осталась зелёной, продолжая
    /// утверждать то, чего система больше не делает. Ровно тот класс, ради которого имена
    /// тестов и читают.
    ///
    /// Свойство при этом настоящее и своё: закрытая строка обязана перестать пускать сразу.
    /// Ради него состояние и читается из базы на каждом запросе — будь токен подписанным и
    /// проверяемым локально, отозванный ходил бы ещё пятнадцать минут.
    /// </remarks>
    [Fact]
    public async Task Отзыв_обрывает_живую_сессию_немедленно()
    {
        var id = Кто();
        var pair = await IssueAsync(id);

        (await ПроверитьAsync(pair.Access)).ShouldNotBeNull();

        // Закрытая строка обязана назвать причину, а не отвечать «истекла».
        await ОтозватьAsync(pair.TokenId, RevokeReasons.UserInactive);

        var error = await Should.ThrowAsync<AppException>(() =>
            ПроверитьAsync(pair.Access));

        error.Code.ShouldBe("USER_INACTIVE");
    }

    // --- блокировка после неудач --------------------------------------------------------

    // --- ротация и кража -----------------------------------------------------------------

    [Fact]
    public async Task Обновление_выдаёт_новую_пару_и_гасит_прежнюю()
    {
        var id = Кто();
        var first = await IssueAsync(id);

        var second = await RefreshAsync(first.Refresh);

        second.Refresh.ShouldNotBe(first.Refresh);
        second.Access.ShouldNotBe(first.Access);
        second.TokenId.ShouldBe(first.TokenId, "сессия одна, меняются только токены");
    }

    [Fact]
    public async Task Прежний_токен_доступа_доживает_свой_срок()
    {
        // ⚠ Намеренно. Вкладка, не успевшая узнать о новом токене, не должна получать отказ
        // на ровном месте: у токена доступа и так короткий век.
        var id = Кто();
        var first = await IssueAsync(id);

        await RefreshAsync(first.Refresh);

        (await ПроверитьAsync(first.Access)).ShouldNotBeNull();
    }

    /// <summary>
    /// Истёкший токен доступа при живой сессии называется истёкшим ТОКЕНОМ, а не сессией.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>Ревью 2026-09-15, решение 0080.</b> Оба клиента — веб и настольный — обменивают
    /// пару только на <c>401 TOKEN_EXPIRED</c>, а сервер на истёкший доступ отвечал
    /// <c>SESSION_EXPIRED</c>. Клиенты по построению считали это концом сессии и уводили человека
    /// на вход по истечении каждого токена доступа (по умолчанию — пятнадцать минут), хотя
    /// половина обновления была жива. Сервер .NET не слал <c>TOKEN_EXPIRED</c> ни разу за всю
    /// историю: код был объявлен, а ветки под него не было.
    /// </remarks>
    [Fact]
    public async Task Истёкший_токен_доступа_при_живой_сессии_просит_обмена()
    {
        var id = Кто();
        var pair = await IssueAsync(id);

        await ИстечьAsync(pair.TokenId, доступ: true, сессия: false);

        var error = await Should.ThrowAsync<AppException>(() => ПроверитьAsync(pair.Access));

        error.Code.ShouldBe(ErrorCodes.TokenExpired);
        error.Status.ShouldBe(System.Net.HttpStatusCode.Unauthorized);

        // И обмен после этого работает: сессия жива, пара поворачивается, новый доступ пускает.
        var second = await RefreshAsync(pair.Refresh);

        (await ПроверитьAsync(second.Access)).ShouldNotBeNull();
    }

    /// <summary>Кончившаяся сессия по-прежнему называется кончившейся: обмен её не лечит.</summary>
    /// <remarks>
    /// ⚠ Парный факт к предыдущему. Без него «на любой истёкший срок — TOKEN_EXPIRED» прошло бы
    /// зелёным, а клиент на мёртвой сессии сначала ходил бы на обмен и лишь после отказа — на вход.
    /// </remarks>
    [Fact]
    public async Task Кончившаяся_сессия_не_просит_обмена()
    {
        var id = Кто();
        var pair = await IssueAsync(id);

        await ИстечьAsync(pair.TokenId, доступ: true, сессия: true);

        var error = await Should.ThrowAsync<AppException>(() => ПроверитьAsync(pair.Access));

        error.Code.ShouldBe(ErrorCodes.SessionExpired);
    }

    [Fact]
    public async Task Потраченный_токен_обновления_закрывает_сессию_целиком()
    {
        // ⚠ ГЛАВНАЯ проверка этапа. RFC 9700 §4.14.2: предъявление аннулированного токена
        // означает, что он есть у двоих; кто из них вор — определить нельзя, поэтому
        // закрывается действующий. Мы закрываем сессию целиком, как предписывает OAuth 2.1.
        //
        // Цена известна и принята: настоящий владелец тоже окажется снаружи.
        var id = Кто();
        database.Settings.Values["auth.refresh_grace_seconds"] = 0;

        try
        {
            var first = await IssueAsync(id);
            var second = await RefreshAsync(first.Refresh);

            var error = await Should.ThrowAsync<AppException>(() => RefreshAsync(first.Refresh));

            error.Code.ShouldBe("SESSION_REVOKED");
            error.Message.ShouldContain("повторное использование");

            // И новый токен, выданный законному владельцу, тоже перестал работать.
            await Should.ThrowAsync<AppException>(() => RefreshAsync(second.Refresh));
            await Should.ThrowAsync<AppException>(() =>
                ПроверитьAsync(second.Access));
        }
        finally
        {
            database.Settings.Values.Remove("auth.refresh_grace_seconds");
        }
    }

    [Fact]
    public async Task Кража_токена_пишется_в_журнал_отдельным_событием()
    {
        var id = Кто();
        database.Settings.Values["auth.refresh_grace_seconds"] = 0;

        try
        {
            var first = await IssueAsync(id);
            await RefreshAsync(first.Refresh);
            await Should.ThrowAsync<AppException>(() => RefreshAsync(first.Refresh));

            var written = database.Audit.About(first.TokenId);

            written.ShouldContain(entry => entry.Action == TokenActions.Reuse);
            written.First(entry => entry.Action == TokenActions.Reuse)
                .Result.ShouldBe(Shared.Contracts.Audit.AuditResult.Deny);
        }
        finally
        {
            database.Settings.Values.Remove("auth.refresh_grace_seconds");
        }
    }

    /// <summary>Короткое окно не делает кражей повтор клиента после зависшего ответа.</summary>
    /// <remarks>
    /// ⚠ ⚠ Замечание ревью к PR #31, сведение ревью 2026-09-15. Первый обмен дошёл и погасил половину,
    /// ответ завис; клиент ждёт своим сроком 4 с, пауза полсекунды — и повтор приходит через пять
    /// с лишним секунд. При окне 2 с это была «кража»: сессия закрывалась, поднималась тревога,
    /// клиент стирал зеркало.
    /// </remarks>
    [Fact]
    public async Task Короткое_окно_не_делает_повтор_клиента_кражей()
    {
        var id = Кто();
        database.Settings.Values["auth.refresh_grace_seconds"] = 2;

        try
        {
            var first = await IssueAsync(id);

            await RefreshAsync(first.Refresh);
            await ПогаситьРаньшеAsync(first.TokenId, TimeSpan.FromSeconds(5));

            var retry = await RefreshAsync(first.Refresh);

            (await ПроверитьAsync(retry.Access)).ShouldNotBeNull("повтор после зависшего ответа — не кража");
        }
        finally
        {
            database.Settings.Values.Remove("auth.refresh_grace_seconds");
        }
    }

    /// <summary>Нижняя граница окна — не бесконечность: повтор позже неё по-прежнему кража.</summary>
    [Fact]
    public async Task Повтор_позже_наименьшего_окна_остаётся_кражей()
    {
        var id = Кто();
        database.Settings.Values["auth.refresh_grace_seconds"] = 2;

        try
        {
            var first = await IssueAsync(id);

            await RefreshAsync(first.Refresh);
            await ПогаситьРаньшеAsync(first.TokenId, TimeSpan.FromSeconds(15));

            await Should.ThrowAsync<AppException>(() => RefreshAsync(first.Refresh));
        }
        finally
        {
            database.Settings.Values.Remove("auth.refresh_grace_seconds");
        }
    }

    [Fact]
    public async Task Гонка_вкладок_кражей_не_считается()
    {
        // ⚠ Обратная сторона предыдущей проверки, и без неё та была бы вредна. Две вкладки,
        // заметившие истёкший токен доступа одновременно, пойдут обновляться обе — и вторая
        // предъявит уже потраченный токен. Считай мы это кражей, человека выкидывало бы из
        // системы за то, что у него открыто два окна.
        var id = Кто();
        var first = await IssueAsync(id);

        var one = await RefreshAsync(first.Refresh);
        var two = await RefreshAsync(first.Refresh);

        one.Refresh.ShouldNotBe(two.Refresh);
        one.TokenId.ShouldBe(two.TokenId);

        // Сессия жива, и оба токена доступа работают.
        (await ПроверитьAsync(one.Access)).ShouldNotBeNull();
        (await ПроверитьAsync(two.Access)).ShouldNotBeNull();
    }

    /// <summary>
    /// Ветка, выданная гонкой внутри окна, не живёт отдельной цепочкой.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ Ревью безопасности 2026-10-20. Повтор потраченной половины внутри окна давал новую
    /// пару и не трогал прежнюю — две живые половины одной сессии. Вор, предъявивший украденную
    /// половину в те же десять секунд, что и владелец, получал НЕЗАВИСИМУЮ цепочку: каждый менял
    /// свои половины, чужую не предъявлял никто, и повтор не замечался до абсолютного срока.
    /// Теперь первый обычный обмен любой ветки гасит другую, и предъявление погашенной позже окна
    /// — кража: сессия закрывается целиком.
    ///
    /// ⚠ Первая ветка гонкой НЕ гасится — это проверено первым обменом: кука браузера после гонки
    /// вкладок — любая из двух, и вкладка с первой веткой не должна получать «кражу» на ровном месте.
    /// </remarks>
    // мутация 2026-10-20: убрал гашение прочих половин на обычном обмене → покраснел этот факт
    // (погашенная ветка после окна обменялась как живая).
    [Fact]
    public async Task Ветка_из_окна_не_живёт_отдельной_цепочкой()
    {
        var id = Кто();
        var first = await IssueAsync(id);

        var одна = await RefreshAsync(first.Refresh);
        var другая = await RefreshAsync(first.Refresh);

        // Гонка не погасила первую ветку: её обмен — обычный, и он гасит вторую.
        var дальше = await RefreshAsync(одна.Refresh);

        // Вторую ветку предъявляют позже окна — это уже не гонка, а кража.
        await ПогаситьРаньшеAsync(first.TokenId, TimeSpan.FromSeconds(30));

        var error = await Should.ThrowAsync<AppException>(() => RefreshAsync(другая.Refresh));

        error.Code.ShouldBe("SESSION_REVOKED", "погашенная ветка обменялась как живая — цепочки две");

        (await ReasonAsync(first.TokenId)).ShouldBe(RevokeReasons.Stolen);
        await Should.ThrowAsync<AppException>(() => RefreshAsync(дальше.Refresh));
    }

    [Fact]
    public async Task Одновременные_обновления_не_валят_сессию()
    {
        // Та же гонка, но настоящая: восемь параллельных запросов одним токеном. Ни один не
        // должен привести к отзыву — иначе первый же человек с двумя вкладками окажется
        // снаружи.
        var id = Кто();
        var first = await IssueAsync(id);

        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(first.Refresh);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }));

        await Task.WhenAll(attempts);

        var session = await database.ReadAsync(db => db.Set<AuthToken>()
            .FirstAsync(row => row.Id == first.TokenId, Ct));

        session.RevokedAt.ShouldBeNull("гонка вкладок не должна выглядеть кражей");
    }

    /// <summary>
    /// При выключенном окне одновременные предъявы одной половины не дают двух живых цепочек.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-09-15 (S2, refresh-1). Чтение половины идёт без блокировки, и до правки число
    /// строк условного гашения не смотрелось: все одновременные запросы, увидевшие <c>used_at</c>
    /// пустым, получали по паре. Выключенное окно обещает «повтор — всегда кража», а одновременный
    /// повтор кражей не становился никогда.
    /// </remarks>
    [Fact]
    public async Task Одновременные_обновления_при_нулевом_окне_не_расщепляют_цепочку()
    {
        var id = Кто();
        database.Settings.Values["auth.refresh_grace_seconds"] = 0;

        // ⚠ ⚠ Гонка открывается НАВЕРНЯКА, а не «иногда». Первая редакция пускала восемь запросов
        // разом и надеялась на совпадение — мутация «ноль строк снова выдаёт пару» прошла её
        // зелёной: запросы шли друг за другом, и второй видел погашенную половину уже на чтении.
        // Окно настройки читается после чтения половины и до её гашения; барьер на нём держит
        // оба запроса, пока оба не прочитали половину пустой.
        const int Запросов = 2;
        var дошли = 0;
        var оба = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        database.Settings.Перед["auth.refresh_grace_seconds"] = async () =>
        {
            if (Interlocked.Increment(ref дошли) >= Запросов)
            {
                оба.TrySetResult();
            }

            // Не сошлись за 10 с — пустить дальше: тест покраснеет на утверждении, а не зависнет.
            await Task.WhenAny(оба.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        };

        try
        {
            var first = await IssueAsync(id);
            var старт = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var попытки = Enumerable.Range(0, Запросов).Select(_ => Task.Run(async () =>
            {
                await старт.Task;

                try
                {
                    return await RefreshAsync(first.Refresh);
                }
                catch (AppException)
                {
                    return null;
                }
            })).ToList();

            старт.SetResult();

            var выданные = (await Task.WhenAll(попытки)).OfType<TokenPair>().ToList();

            выданные.Count.ShouldBeLessThanOrEqualTo(
                1, "одна половина дала несколько пар — цепочка расщепилась, и повтор между ними не виден");

            var session = await database.ReadAsync(db => db.Set<AuthToken>()
                .FirstAsync(row => row.Id == first.TokenId, Ct));

            session.RevokedReason.ShouldBe(RevokeReasons.Stolen, "повтор при выключенном окне обязан закрыть сессию");
        }
        finally
        {
            database.Settings.Values.Remove("auth.refresh_grace_seconds");
            database.Settings.Перед.Remove("auth.refresh_grace_seconds");
        }
    }

    // --- выход и отзыв ---------------------------------------------------------------------

    [Fact]
    public async Task Выход_закрывает_сессию_и_её_токены()
    {
        var id = Кто();
        var pair = await IssueAsync(id);

        await ОтозватьAsync(pair.TokenId, RevokeReasons.SignedOut);

        var error = await Should.ThrowAsync<AppException>(() =>
            ПроверитьAsync(pair.Access));

        error.Code.ShouldBe("SESSION_REVOKED");
        error.Message.ShouldContain("вышли");
    }

    [Fact]
    public async Task Причина_окончания_сессии_доезжает_до_человека()
    {
        // ⚠ Единственное, что экран входа может сказать вместо голого «войдите заново».
        // Разница между «вы вышли» и «обнаружена кража токена» — это разница между «ладно»
        // и «звоните в безопасность».
        var id = Кто();
        var pair = await IssueAsync(id);

        // ⚠ Отключает человека СОСЕД, и это проверяется его сьютом. Здесь — наша половина:
        // строка, закрытая с причиной «учётная запись отключена», обязана назвать её
        // человеку, а не отвечать «сессия кончилась».
        await ОтозватьAsync(pair.TokenId, RevokeReasons.UserInactive);

        var error = await Should.ThrowAsync<AppException>(() => RefreshAsync(pair.Refresh));

        error.Message.ShouldContain("отключена");
    }

    [Fact]
    public async Task Чужую_сессию_отозвать_нельзя()
    {
        // Владелец берётся из личности, а не из запроса: иначе это способ выгонять кого
        // угодно, зная лишь номер сессии.
        var я = Кто();
        var другой = Кто();

        var mine = await IssueAsync(я);
        var чужая = await IssueAsync(другой);

        // ⚠ Проверка владельца живёт в КОНТРОЛЛЕРЕ, а не в отзыве: отзыв про строку, а не
        // про того, кто просит. Здесь проверяется то же свойство на уровне данных — строка
        // другого человека принадлежит другому человеку, и запрос «моя ли она» это видит.
        var моя = await database.ReadAsync(db => db.Set<AuthToken>()
            .AnyAsync(row => row.Id == чужая.TokenId && row.UserId == я, Ct));

        моя.ShouldBeFalse("иначе контроллер отдал бы чужой вход всякому, кто знает его номер");
    }

    [Fact]
    public async Task Список_сессий_показывает_только_живые()
    {
        var id = Кто();

        var first = await IssueAsync(id);
        var second = await IssueAsync(id);

        await ОтозватьAsync(first.TokenId, RevokeReasons.SignedOut);

        var live = await ЖивыеAsync(id);

        live.ShouldContain(second.TokenId);
        live.ShouldNotContain(first.TokenId);
    }

    [Fact]
    public async Task Обновление_по_отозванной_сессии_называет_настоящую_причину()
    {
        // ⚠ Найдено мутацией (правило 9): порядок проверок в обновлении не был подтверждён
        // ничем. Отзыв гасит токены, выставляя им срок «сейчас», — поменяй проверки местами,
        // и вместо «обнаружена кража» человек прочитал бы «сессия кончилась». Тот же порядок
        // в ResolveAsync тест ловил, а здесь дыра держалась.
        var id = Кто();
        database.Settings.Values["auth.refresh_grace_seconds"] = 0;

        try
        {
            var first = await IssueAsync(id);

            await RefreshAsync(first.Refresh);
            await Should.ThrowAsync<AppException>(() => RefreshAsync(first.Refresh));

            // Сессия уже закрыта кражей. Второй заход обязан назвать ту же причину, а не
            // выдать безобидное «истекла».
            var error = await Should.ThrowAsync<AppException>(() => RefreshAsync(first.Refresh));

            error.Code.ShouldBe("SESSION_REVOKED");
            error.Message.ShouldContain("повторное использование");
        }
        finally
        {
            database.Settings.Values.Remove("auth.refresh_grace_seconds");
        }
    }

    [Fact]
    public async Task Токены_лежат_в_базе_только_хешем()
    {
        // ⚠ Найдено мутацией: замена SHA-256 на «сохранить как есть» проходила весь сьют
        // зелёным. Работать система при этом продолжала бы полностью — и дамп базы отдавал
        // бы действующие токены всех живых сессий.
        var id = Кто();

        var pair = await IssueAsync(id);

        var stored = await database.ReadAsync(db => db.Set<AuthTokenSecret>()
            .Select(row => row.Hash)
            .ToListAsync(Ct));

        stored.ShouldNotBeEmpty();

        foreach (var token in new[] { pair.Access, pair.Refresh })
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(token);
            var hash = System.Security.Cryptography.SHA256.HashData(raw);

            stored.ShouldNotContain(row => row.SequenceEqual(raw), "токен лежит открытым");
            stored.ShouldContain(row => row.SequenceEqual(hash));
        }
    }

    [Fact]
    public async Task Простой_не_продлевает_сессию_дальше_абсолютного_срока()
    {
        // ⚠ Найдено мутацией: снятие потолка проходило зелёным. Без него сессия, которой
        // пользуются активно, не накопит простоя никогда — то есть абсолютного срока не
        // существует, и украденный токен обновляется бесконечно.
        var id = Кто();
        database.Settings.Values["auth.session_absolute_hours"] = 1;
        database.Settings.Values["auth.session_idle_minutes"] = 600;

        try
        {
            var pair = await IssueAsync(id);

            var absolute = await database.ReadAsync(db => db.Set<AuthToken>()
                .Where(row => row.Id == pair.TokenId)
                .Select(row => row.ExpiresAt)
                .FirstAsync(Ct));

            // Допуск — не небрежность: timestamptz хранит микросекунды, а DateTimeOffset
            // считает сотнями наносекунд, и прочитанное из базы значение всегда чуть
            // короче исходного. Точное сравнение здесь краснеет на верном коде.
            pair.RefreshExpiresAt.ShouldBe(absolute, TimeSpan.FromMilliseconds(1));
            pair.RefreshExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(600));

            // И после обмена потолок остаётся тем же: продление простоем его не двигает.
            (await RefreshAsync(pair.Refresh)).RefreshExpiresAt
                .ShouldBe(absolute, TimeSpan.FromMilliseconds(1));
        }
        finally
        {
            database.Settings.Values.Remove("auth.session_absolute_hours");
            database.Settings.Values.Remove("auth.session_idle_minutes");
        }
    }

    /// <summary>
    /// Массовый отзыв закрывает все строки человека, а не только обрывает доступ.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Имя исправлено 2026-08-25 по той же причине, что у соседней проверки:</b> она
    /// звалась «отключение...», а отключение с этого дня не гасит ничего (решение владельца).
    /// Проверяется здесь механизм массового отзыва — его зовут смена пароля, сброс, закрытие
    /// сессий по требованию и снятие фактора.
    ///
    /// ⚠ <c>RevokeAllAsync</c> до 2026-08-22 не вызывался ниоткуда вовсе. Заметили чтением, а
    /// не падением, — и это причина, по которой каждое место вызова теперь названо и проверено
    /// поимённо (<c>EmailSslSender.Modules.Identity.Tests/RevocationSitesTests.cs</c>).
    /// </remarks>
    [Fact]
    public async Task Массовый_отзыв_закрывает_строки_а_не_только_обрывает_доступ()
    {
        var id = Кто();

        await IssueAsync(id);
        await IssueAsync(id);

        (await ЖивыеAsync(id)).Count.ShouldBe(2);

        await ОтозватьВсеAsync(id, RevokeReasons.UserInactive);

        (await ЖивыеAsync(id)).ShouldBeEmpty();
    }

    /// <summary>
    /// Закрытая по причине «учётная запись отключена» строка так и объясняется.
    /// </summary>
    /// <remarks>
    /// ⚠ Причину <c>user_inactive</c> сегодня не ставит ни одно действие продукта — разбор у
    /// самой константы. Проверка остаётся, потому что строки с этой причиной <b>уже лежат в
    /// базе</b> у тех, кого отключали прежней редакцией, и объяснять их система обязана.
    /// </remarks>
    [Fact]
    public async Task Отключённому_говорят_что_отключён_а_не_что_сессия_закрыта()
    {
        // Разница не косметическая: «сессия закрыта» предлагает войти заново, а входить
        // заново бесполезно. Клиент обязан различать это машинным кодом.
        var id = Кто();
        var pair = await IssueAsync(id);

        await ОтозватьВсеAsync(id, RevokeReasons.UserInactive);

        var error = await Should.ThrowAsync<AppException>(
            () => ПроверитьAsync(pair.Access));

        error.Code.ShouldBe("USER_INACTIVE");
    }

    [Fact]
    public async Task Удаление_тоже_закрывает_сессии()
    {
        var id = Кто();

        await IssueAsync(id);

        await УдалитьВсеAsync(id);

        (await ЖивыеAsync(id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Массовый_отзыв_вне_транзакции_запрещён()
    {
        // ⚠ Правило 15 проверкой, а не комментарием. Токены гасятся оператором немедленно, а
        // строка журнала уезжает на сохранении: без общей транзакции доступ пропал бы, а
        // следа не осталось. Такую ошибку нельзя заметить по работающей системе — она видна
        // только в журнале, которого нет.
        var id = Кто();

        await IssueAsync(id);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var scope = database.Scope();

            // ⚠ БЕЗ транзакции нарочно: именно это отзыв и обязан отвергнуть.
            await scope.ServiceProvider
                .GetRequiredService<ITokenRevoker>()
                .RevokeAllAsync(id, RevokeReasons.Revoked, Ct);
        });
    }

    // --- обвязка -----------------------------------------------------------------------------

    /// <summary>
    /// Выход не затирает уже проставленную причину «кража токена».
    /// </summary>
    /// <remarks>
    /// ⚠ Обнаружение кражи закрывает сессию причиной <c>token_reuse</c> — и это единственный
    /// след происшествия. Если человек в ту же секунду нажал «выйти», прежняя редакция
    /// переписывала причину на «вышел»: на экране вместо «сообщите администратору» появлялось
    /// «вы вышли из системы», а строка сессии переставала отличать кражу от обычного выхода.
    /// Причина: сессия читалась ДО транзакции, а правился прочитанный экземпляр внутри.
    /// Теперь гашение идёт условным обновлением «только если ещё не отозвана».
    /// Найдено разбором 2026-08-22.
    /// </remarks>
    [Fact]
    public async Task Выход_не_затирает_причину_кражи()
    {
        var id = Кто();
        var pair = await IssueAsync(id);

        // Без окна снисхождения: иначе повтор в ту же секунду — это гонка вкладок, а не кража.
        database.Settings.Values["auth.refresh_grace_seconds"] = 0;

        try
        {
            // Кража: старый токен обновления предъявляют второй раз.
            await RefreshAsync(pair.Refresh);

            await Should.ThrowAsync<AppException>(() => RefreshAsync(pair.Refresh));
        }
        finally
        {
            database.Settings.Values.Remove("auth.refresh_grace_seconds");
        }

        (await ReasonAsync(pair.TokenId)).ShouldBe("token_reuse");

        // А теперь человек жмёт «выйти» — по той же, уже закрытой сессии.
        await ОтозватьAsync(pair.TokenId, RevokeReasons.SignedOut);

        (await ReasonAsync(pair.TokenId)).ShouldBe(
            "token_reuse", "выход переписал причину, и след кражи исчез");
    }

    /// <summary>
    /// Чужой по форме токен в заголовке Bearer уходит следующему распознавателю.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Заголовок <c>Authorization: Bearer</c> — не собственность этого модуля.</b>
    /// Распознаватели выстроены цепочкой, и следующим этапом рядом встанет ключ API со своим
    /// способом предъявляться. Прежде мы забирали <b>любое</b> непустое значение после
    /// <c>Bearer</c> и на негодное отвечали исключением «сессия кончилась» — то есть обрывали
    /// опрос цепочки. Ключ API, предъявленный тем же заголовком (самый очевидный вариант), не
    /// заработал бы вовсе, пока не поправят закрытый модуль. Найдено разбором 2026-08-22.
    ///
    /// Пара проверок нарочно: «не наше — молчим» и «наше, но негодное — говорим». Первая без
    /// второй означала бы, что кончившаяся сессия неотличима от отсутствия входа, и человек
    /// никогда не узнал бы про кражу токена.
    /// </remarks>
    [Theory]
    [InlineData("eams_ak_9f2c1d")]
    [InlineData("короткий")]
    [InlineData("этот.токен.выглядит.как.jwt.и.нашим.не.является")]
    public async Task Чужой_по_форме_токен_отдаётся_следующему(string foreign)
    {
        await using var scope = database.Scope();

        var introspector = scope.ServiceProvider.GetRequiredService<ITokenIntrospector>();

        introspector.Recognises(foreign).ShouldBeFalse("форма не наша — цепочка обязана идти дальше");

        (await introspector.IntrospectAsync(foreign, Ct)).ShouldBeNull();
    }

    /// <summary>А вот наш по форме, но неизвестный — это отказ, а не молчание.</summary>
    [Fact]
    public async Task Наш_по_форме_но_неизвестный_токен_это_отказ()
    {
        await using var scope = database.Scope();

        var introspector = scope.ServiceProvider.GetRequiredService<ITokenIntrospector>();

        // Сорок три символа из нужного алфавита — форма наша, значения такого нет.
        var shaped = new string('A', 43);

        introspector.Recognises(shaped).ShouldBeTrue();

        var error = await Should.ThrowAsync<AppException>(() => introspector.IntrospectAsync(shaped, Ct));

        error.Code.ShouldBe("SESSION_EXPIRED");
    }

    /// <summary>Причина закрытия сессии прямо из таблицы.</summary>
    private Task<string?> ReasonAsync(Guid sessionId) =>
        database.ReadAsync(db => db.Set<AuthToken>()
            .Where(row => row.Id == sessionId)
            .Select(row => row.RevokedReason)
            .FirstOrDefaultAsync(Ct));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    private static readonly RequestOrigin Origin =
        new(System.Net.IPAddress.Parse("10.0.0.7"), "EAMS-Tests/1.0");

    /// <summary>
    /// Выдумать человека.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Строки в таблице людей при этом НЕ ЗАВОДИТСЯ, и это не срезанный угол, а
    /// следствие границы модулей.</b> У наших токенов нет внешнего ключа на <c>users</c>
    /// (правило 1): <c>user_id</c> — это <c>Guid</c>, ровно как <c>actor_id</c> в журнале.
    /// Значит выдать токен можно кому угодно, и сьют модуля не зависит от соседа вовсе.
    ///
    /// Проверка «а можно ли этому человеку войти» живёт у соседа и проверяется его сьютом;
    /// сюда она не относится, и тащить её сюда значило бы проверять чужой модуль.
    /// </remarks>
    private static Guid Кто() => Guid.CreateVersion7();

    /// <summary>Проверить секрет — тем же путём, каким его проверяет конвейер.</summary>
    /// <remarks>
    /// ⚠ Через <b>наш</b> контракт, а не через чужой распознаватель личности: тот живёт у
    /// соседа и складывает личность из двух ответов (этап 10.5.4). Здесь проверяется наша
    /// половина — жив ли секрет и чей он.
    /// </remarks>
    private async Task<TokenIdentity?> ПроверитьAsync(string presented)
    {
        await using var scope = database.Scope();

        return await scope.ServiceProvider
            .GetRequiredService<ITokenIntrospector>()
            .IntrospectAsync(presented, Ct);
    }

    /// <summary>Живые входы человека.</summary>
    private async Task<List<Guid>> ЖивыеAsync(Guid userId)
    {
        var live = await database.InTokensAsync(
            tokens => tokens.ListAsync(userId, TokenKinds.Session, Ct));

        return [.. live.Select(row => row.Id)];
    }

    /// <summary>Закрыть всё, что предъявляется, — тем же путём, каким это делают соседи.</summary>
    private Task ОтозватьВсеAsync(Guid userId, string reason) =>
        ВТранзакцииAsync((revoker, inner) =>
            revoker.RevokeAllAsync(userId, reason, inner));

    /// <summary>Удалить строки насовсем — так поступает удаление человека.</summary>
    private Task УдалитьВсеAsync(Guid userId) =>
        ВТранзакцииAsync((revoker, inner) => revoker.DeleteAllAsync(userId, inner));

    private async Task ВТранзакцииAsync(Func<ITokenRevoker, CancellationToken, Task<int>> action)
    {
        await using var scope = database.Scope();

        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var revoker = scope.ServiceProvider.GetRequiredService<ITokenRevoker>();

        await db.InTransactionAsync((inner) => action(revoker, inner), Ct);
    }

    /// <summary>Отозвать строку — тем же путём, каким её отзывают соседи.</summary>
    private async Task ОтозватьAsync(Guid tokenId, string reason)
    {
        await using var scope = database.Scope();

        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var revoker = scope.ServiceProvider.GetRequiredService<ITokenRevoker>();

        await db.InTransactionAsync((inner) => revoker.RevokeAsync(tokenId, reason, inner), Ct);
    }

    /// <summary>Сдвинуть сроки строки в прошлое.</summary>
    /// <remarks>Часы у тестовой базы настоящие, поэтому срок двигается в строке, а не во времени.</remarks>
    private async Task ИстечьAsync(Guid tokenId, bool доступ, bool сессия)
    {
        await using var scope = database.Scope();

        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var прошлое = DateTimeOffset.UtcNow.AddMinutes(-1);

        if (доступ)
        {
            await db.Set<AuthTokenSecret>()
                .Where(row => row.TokenId == tokenId && row.Kind == SecretKinds.Access)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ExpiresAt, прошлое), Ct);
        }

        if (сессия)
        {
            await db.Set<AuthToken>()
                .Where(row => row.Id == tokenId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ExpiresAt, прошлое), Ct);
        }
    }

    /// <summary>Сдвинуть отметку гашения половин обновления в прошлое — повтор «пришёл позже».</summary>
    private async Task ПогаситьРаньшеAsync(Guid tokenId, TimeSpan насколько)
    {
        await using var scope = database.Scope();

        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var когда = DateTimeOffset.UtcNow - насколько;

        await db.Set<AuthTokenSecret>()
            .Where(row => row.TokenId == tokenId && row.Kind == SecretKinds.Refresh && row.UsedAt != null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.UsedAt, когда), Ct);
    }

    /// <summary>Выдать пару — тем же путём, каким её выдаёт вход.</summary>
    private Task<TokenPair> IssueAsync(Guid userId) =>
        InTokensAsync(async (tokens, db) =>
        {
            var pair = await tokens.IssueAsync(
                new TokenIssueRequest(userId, TokenKinds.Session, Origin.UserAgent, Origin.Address), Ct);

            await db.SaveChangesAsync(Ct);

            return pair;
        });

    private Task<TokenPair> RefreshAsync(string refresh) =>
        database.InTokensAsync(tokens => tokens.RotateAsync(refresh, Origin, Ct));

    /// <summary>
    /// Действие в транзакции — выдача её требует.
    /// </summary>
    /// <remarks>
    /// ⚠ Требование не формальное: выдача ничего не сохраняет сама, потому что в бою она
    /// идёт внутри транзакции входа. Иначе вход состоялся бы отдельно от обнуления счётчика
    /// неудач и от строки журнала.
    /// </remarks>
    private async Task<T> InTokensAsync<T>(Func<TokenService, SenderDbContext, Task<T>> action)
    {
        await using var scope = database.Scope();

        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

        return await db.InTransactionAsync((_) => action(tokens, db), Ct);
    }
}
