using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Modules.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Вход по HTTP — настоящим конвейером, без подмены личности.
/// </summary>
/// <remarks>
/// ⚠ Здесь <b>не</b> используется подмена <c>ICurrentUser</c>: человек заводится, входит
/// паролем, получает токен и предъявляет его заголовком. Проверять вход подделкой личности
/// значило бы проверять подделку.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class AuthApi(SenderHost host)
{
    [Fact]
    public async Task Вход_выдаёт_токен_и_ставит_куку()
    {
        var email = await ActiveUserAsync();

        var response = await Client().PostAsJsonAsync(
            "/api/auth/login", new { email, password = Strong }, Ct);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body["access_token"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();

        // ⚠ Токена обновления в теле НЕТ: он ушёл кукой HttpOnly, и дублировать его рядом
        // означало бы отменить всю затею — сценарий на странице получил бы то же значение.
        body["refresh_token"].ShouldBeNull();

        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.Contains("__Host-"));

        cookie.ShouldContain("__Host-sender-refresh=");
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);

        // Префикс __Host- обязывает НЕ задавать домен: иначе браузер молча отбросит куку.
        cookie.ShouldNotContain("domain=", Case.Insensitive);
    }

    /// <summary>
    /// Не снятый заголовок от мёртвой сессии не мешает ни войти, ни обновиться.
    /// </summary>
    /// <remarks>
    /// ⚠ Клиент чистит заголовок не раньше, чем получит ответ, — а ответа он получить не мог:
    /// распознаватель личности опрашивался на КАЖДОМ запросе, включая публичные, и негодный
    /// токен обрывал запрос исключением до контроллера. То есть обновить пару со ещё не снятым
    /// истёкшим заголовком было нельзя <b>никогда</b>, а вход после выхода отвечал «вы вышли
    /// из системы». Веб-клиента ещё нет, поэтому никто не наступил. Найдено разбором
    /// 2026-08-22.
    ///
    /// Токен здесь берётся настоящий и намеренно убивается выходом: выдуманная строка нужной
    /// формы прошла бы и через сломанную редакцию, потому что распознаватель до базы бы дошёл,
    /// а строки не нашёл — то есть проверка ничего бы не значила.
    /// </remarks>
    [Fact]
    public async Task Мёртвый_заголовок_не_мешает_войти_заново()
    {
        var email = await ActiveUserAsync();
        var first = await LoginAsync(email, cookie: false, completeFactor: false);
        var access = first["access_token"]!.GetValue<string>();

        using (var out1 = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout"))
        {
            out1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

            (await Client().SendAsync(out1, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Тот же, уже мёртвый заголовок — и обычный вход.
        using var again = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password = Strong, use_cookie = false }),
        };

        again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        var response = await Client().SendAsync(again, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "мёртвый заголовок закрыл вход");

        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var refresh = body["refresh_token"]!.GetValue<string>();

        // И обновление — с тем же мёртвым заголовком на шее.
        using var refreshed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refresh_token = refresh, use_cookie = false }),
        };

        refreshed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        (await Client().SendAsync(refreshed, Ct)).StatusCode.ShouldBe(
            HttpStatusCode.OK, "мёртвый заголовок закрыл обновление токена");
    }

    [Fact]
    public async Task Десктопу_токен_обновления_возвращается_в_теле()
    {
        // Кук у него нет: токен он хранит сам, в зашифрованном локальном хранилище.
        var email = await ActiveUserAsync();

        var body = await LoginAsync(email, cookie: false);

        body["refresh_token"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Токен_доступа_открывает_свои_эндпоинты()
    {
        var email = await ActiveUserAsync();
        var tokens = await LoginAsync(email, cookie: false);

        var me = await GetAsync<JsonObject>("/api/me", tokens["access_token"]!.GetValue<string>());

        me!["email"]!.GetValue<string>().ShouldBe(email);
        me["session_id"]!.GetValue<Guid>().ShouldBe(tokens["session_id"]!.GetValue<Guid>());
    }

    /// <summary>
    /// Истёкший токен доступа при живой сессии отвечает <c>401 TOKEN_EXPIRED</c>, и пара обменивается.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ Сведение ревью 2026-09-15, решение 0080. Это договор с обоими клиентами: веб
    /// (<c>eams.web/src/shared/api/client.ts</c>) и настольный (<c>Eams.Desktop/Api/ApiClient.cs</c>)
    /// обменивают пару только на <c>TOKEN_EXPIRED</c>. Сервер отвечал <c>SESSION_EXPIRED</c>, и оба
    /// клиента уводили человека на вход по истечении каждого токена доступа. Проверка — на уровне
    /// HTTP, потому что клиенты читают конверт, а не исключение службы.
    ///
    /// Красно-зелёная 2026-09-15: с проверкой срока в прежнем виде (одна ветка на оба срока) этот
    /// факт красный, с починкой — зелёный.
    /// </remarks>
    [Fact]
    public async Task Истёкший_токен_доступа_отвечает_TOKEN_EXPIRED_и_пара_обменивается()
    {
        var email = await ActiveUserAsync();
        var tokens = await LoginAsync(email, cookie: false);
        var access = tokens["access_token"]!.GetValue<string>();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Голым SQL: сущности токенов internal в чужой сборке, открывать модуль ради теста нельзя.
            var затронуто = await db.Database.ExecuteSqlRawAsync(
                "UPDATE auth_token_secrets SET expires_at = now() - interval '1 minute' WHERE token_id = {0} AND kind = 'access'",
                new object[] { tokens["session_id"]!.GetValue<Guid>() },
                Ct);

            затронуто.ShouldBe(1, "секрет доступа этой сессии обязан найтись");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var response = await Client().SendAsync(request, Ct);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        body["code"]!.GetValue<string>().ShouldBe(ErrorCodes.TokenExpired);

        // И обмен работает: половина обновления жива, новый доступ открывает свои эндпоинты.
        var fresh = await RefreshAsync(tokens["refresh_token"]!.GetValue<string>());
        var me = await GetAsync<JsonObject>("/api/me", fresh["access_token"]!.GetValue<string>());

        me!["email"]!.GetValue<string>().ShouldBe(email);
    }

    [Fact]
    public async Task Без_токена_свои_эндпоинты_закрыты()
    {
        host.User.SignOut();

        (await Client().GetAsync("/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Неверный_пароль_и_чужой_адрес_отвечают_одинаково()
    {
        var email = await ActiveUserAsync();

        var wrong = await Client().PostAsJsonAsync(
            "/api/auth/login", new { email, password = "Неверный-Пароль-1!" }, Ct);
        var missing = await Client().PostAsJsonAsync(
            "/api/auth/login", new { email = $"нет-{Guid.CreateVersion7():N}@example.com", password = Strong }, Ct);

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var first = await Envelope(wrong);
        var second = await Envelope(missing);

        // Совпадать обязаны код, текст и подробности — то есть всё, по чему снаружи можно
        // отличить один случай от другого. Номер обращения при этом различается и обязан:
        // он свой у каждого запроса, и первая редакция этого теста сравнивала тела целиком,
        // то есть краснела на верном коде.
        first.Code.ShouldBe(second.Code);
        first.Message.ShouldBe(second.Message);
        first.Details.ShouldBeNull();
        second.Details.ShouldBeNull();
    }

    // --- ротация ------------------------------------------------------------------------

    [Fact]
    public async Task Обновление_кукой_работает_и_выдаёт_новую_куку()
    {
        var email = await ActiveUserAsync();

        var login = await Client().PostAsJsonAsync("/api/auth/login", new { email, password = Strong }, Ct);
        var cookie = Refresh(login);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { }),
        };

        request.Headers.Add("Cookie", $"__Host-sender-refresh={cookie}");

        var refreshed = await Client().SendAsync(request, Ct);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        Refresh(refreshed).ShouldNotBe(cookie, "ротация: прежний токен больше не действует");
    }

    /// <summary>
    /// Половина, взятая из куки, не выносится в тело — даже если просили <c>use_cookie: false</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью безопасности 2026-10-20. Прежде <c>use_cookie: false</c> с пустым полем брало
    /// половину из куки <c>HttpOnly</c> и возвращало новую телом: сценарий, внедрённый в страницу,
    /// одним запросом получал на руки то, до чего кука его не допускала, и уносил сессию.
    /// </remarks>
    // мутация 2026-10-20: вернул `Respond(…, body.UseCookie)` в Refresh → покраснел этот факт.
    [Fact]
    public async Task Половина_из_куки_не_выносится_в_тело()
    {
        var email = await ActiveUserAsync();

        var login = await Client().PostAsJsonAsync("/api/auth/login", new { email, password = Strong }, Ct);
        var cookie = Refresh(login);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { use_cookie = false }),
        };

        request.Headers.Add("Cookie", $"__Host-sender-refresh={cookie}");

        var refreshed = await Client().SendAsync(request, Ct);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = (await refreshed.Content.ReadFromJsonAsync<JsonObject>(Ct))!;

        body["refresh_token"].ShouldBeNull("половина из HttpOnly-куки вынесена в тело — под руки сценарию");
        Refresh(refreshed).ShouldNotBe(cookie, "новая половина обязана уйти кукой");
    }

    [Fact]
    public async Task Повторное_использование_токена_закрывает_сессию()
    {
        // ⚠ Сквозная проверка главного свойства этапа. RFC 9700: предъявление уже
        // потраченного токена означает, что он есть у двоих; кто из них вор — определить
        // нельзя, поэтому закрывается сессия целиком.
        var email = await ActiveUserAsync();
        host.Settings.Values["auth.refresh_grace_seconds"] = 0;

        try
        {
            var tokens = await LoginAsync(email, cookie: false);
            var first = tokens["refresh_token"]!.GetValue<string>();

            await RefreshAsync(first);

            var reused = await Client().PostAsJsonAsync(
                "/api/auth/refresh", new { refresh_token = first, use_cookie = false }, Ct);

            reused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            var envelope = await Envelope(reused);

            envelope.Code.ShouldBe("SESSION_REVOKED");

            // ⚠ Человеку сказано, ЧТО случилось. Голое «войдите заново» здесь было бы
            // умолчанием о происшествии.
            envelope.Message.ShouldContain("повторное использование");
        }
        finally
        {
            host.Settings.Values.Remove("auth.refresh_grace_seconds");
        }
    }

    [Fact]
    public async Task Выход_закрывает_сессию_и_снимает_куку()
    {
        var email = await ActiveUserAsync();
        var tokens = await LoginAsync(email, cookie: false);
        var access = tokens["access_token"]!.GetValue<string>();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        var response = await Client().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Кука снимается теми же признаками, что при выдаче, иначе браузер оставит старую.
        response.Headers.GetValues("Set-Cookie").ShouldContain(value => value.Contains("__Host-sender-refresh="));

        // И токен доступа больше не работает — с внятной причиной.
        using var after = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        after.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        var denied = await Client().SendAsync(after, Ct);

        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Envelope(denied)).Message.ShouldContain("вышли");
    }

    [Fact]
    public async Task Свои_сессии_видны_и_текущая_помечена()
    {
        var email = await ActiveUserAsync();
        var first = await LoginAsync(email, cookie: false);
        await LoginAsync(email, cookie: false);

        var list = await GetAsync<JsonArray>("/api/me/sessions", first["access_token"]!.GetValue<string>());

        list!.Count.ShouldBeGreaterThanOrEqualTo(2);
        list.Count(session => session!["current"]!.GetValue<bool>()).ShouldBe(1);
    }

    // --- частота ---------------------------------------------------------------------------

    /// <summary>Поток попыток входа с одного адреса упирается в потолок по адресу.</summary>
    /// <remarks>
    /// ⚠ Отказ отвечает 429, а не 503: умолчание платформы — именно 503, и клиент решил бы, что
    /// сервер лежит, и стал бы повторять. Почты разные: проверяется потолок по адресу, а не предел
    /// на пару «адрес + почта» (решение 0096).
    /// </remarks>
    [Fact]
    public async Task Слишком_частые_попытки_входа_отвергаются_нашим_конвертом()
    {
        using var strict = Строгий(("RateLimits:LoginPerAddressPerMinute", "1"));
        using var client = strict.CreateClient();

        var limited = await ДоОтказаAsync(() => client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = $"нет-{Guid.CreateVersion7():N}@example.com", password = Strong },
            Ct));

        limited.ShouldNotBeNull("потолок входа по адресу должен срабатывать");
        (await Envelope(limited!)).Code.ShouldBe("RATE_LIMITED");
    }

    /// <summary>
    /// Перебор одной почты режется, а соседи по тому же адресу входят.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Решение владельца 0096: за офисным NAT адрес у всех один, и предел «на адрес» был пределом
    /// на весь офис. Теперь адрес — потолок против потока, а перебор держит предел на пару
    /// «адрес + почта» в модуле личностей и блокировка учётной записи.
    /// </remarks>
    // мутация 2026-10-19: снял attempts.Require из SignInAsync → покраснел этот факт (вторая попытка той же почты не 429).
    [Fact]
    public async Task Попытки_одной_почты_режутся_а_соседи_по_адресу_входят()
    {
        using var strict = Строгий(("RateLimits:LoginPerMinute", "1"));
        using var client = strict.CreateClient();

        var почта = $"перебор-{Guid.CreateVersion7():N}@example.com";

        (await client.PostAsJsonAsync("/api/auth/login", new { email = почта, password = Strong }, Ct))
            .StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, "первая попытка отвергнута частотой");

        var вторая = await client.PostAsJsonAsync("/api/auth/login", new { email = почта, password = Strong }, Ct);

        вторая.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "перебор одной почты частотой не режется");
        (await Envelope(вторая)).Code.ShouldBe("RATE_LIMITED");

        (await client.PostAsJsonAsync(
                "/api/auth/login", new { email = $"сосед-{Guid.CreateVersion7():N}@example.com", password = Strong }, Ct))
            .StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, "сосед по адресу заперт чужим перебором");
    }

    /// <summary>Второй фактор веб-входа режется частотой вместе со входом.</summary>
    /// <remarks>
    /// ⚠ Найдено аудитом надёжности 2026-10-19 (решение 0096): группа держала только второй фактор
    /// расширения, а веб-путь не резался ничем.
    /// </remarks>
    // мутация 2026-10-19: убрал /api/auth/two-factor из группы входа → покраснел этот факт.
    [Fact]
    public async Task Второй_фактор_веб_входа_режется_частотой()
    {
        using var strict = Строгий(("RateLimits:LoginPerAddressPerMinute", "1"));
        using var client = strict.CreateClient();

        var limited = await ДоОтказаAsync(() => client.PostAsJsonAsync(
            "/api/auth/two-factor", new { challenge = "нет-такого", code = "000000" }, Ct));

        limited.ShouldNotBeNull("второй фактор веб-входа частотой не режется");
        (await Envelope(limited!)).Code.ShouldBe("RATE_LIMITED");
    }

    /// <summary>
    /// Переподтверждение личности и смена пароля режутся частотой — на человека.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью 2026-09-15 (S2, stepup-1 и password-1): обе двери доказывают личность ВНУТРИ
    /// открытой сессии и перебирались со скоростью сети. С решения 0096 предел — на человека, а
    /// по адресу остаётся потолок в несколько раз выше: сосед по офису своей попытки не теряет.
    /// </remarks>
    // мутация 2026-10-19: снял UserRateLimitMiddleware из конвейера → покраснели оба случая.
    [Theory]
    [InlineData("/api/me/step-up")]
    [InlineData("/api/me/password")]
    public async Task Переспрос_внутри_сессии_режется_частотой(string путь)
    {
        using var strict = Строгий(("RateLimits:LoginPerMinute", "1"));
        using var client = strict.CreateClient();

        host.User.SignIn();

        // Не больше трёх попыток: потолок по адресу здесь — десять, и отказ обязан прийти от
        // предела на человека, а не от него.
        var limited = await ДоОтказаAsync(() => client.PostAsJsonAsync(путь, new { code = "000000" }, Ct), раз: 3);

        limited.ShouldNotBeNull($"путь {путь} частотой не режется");
        (await Envelope(limited!)).Code.ShouldBe("RATE_LIMITED");

        host.User.SignIn();

        (await client.PostAsJsonAsync(путь, new { code = "000000" }, Ct))
            .StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, "сосед по адресу заперт чужим пределом");
    }

    /// <summary>Поток без токена всё равно упирается в потолок по адресу.</summary>
    /// <remarks>
    /// Ограничитель по адресу стоит ДО опознания, поэтому 429 приходит и без валидного токена —
    /// предел на человека такого потока не видит, ему не за кого считать.
    /// </remarks>
    [Fact]
    public async Task Поток_переспроса_без_токена_режется_по_адресу()
    {
        using var strict = Строгий(("RateLimits:LoginPerMinute", "1"), ("RateLimits:AddressFactor", "1"));
        using var client = strict.CreateClient();

        host.User.SignOut();

        var limited = await ДоОтказаAsync(() => client.PostAsJsonAsync("/api/me/step-up", new { code = "000000" }, Ct));

        limited.ShouldNotBeNull("поток без токена частотой не режется");
    }

    /// <summary>Свой сервер с заданными пределами — на той же базе.</summary>
    /// <remarks>
    /// Свой, потому что пределы общие на хост, и выжигать их соседним проверкам было бы нечестно:
    /// они начали бы падать в зависимости от порядка запуска. ⚠ Второй хост на той же базе —
    /// </remarks>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Строгий(params (string Ключ, string Значение)[] пределы) =>
        host.WithWebHostBuilder(web =>
        {
            foreach (var (ключ, значение) in пределы)
            {
                web.UseSetting(ключ, значение);
            }

        });

    /// <summary>Звать, пока не откажут частотой, — не больше <paramref name="раз"/> раз.</summary>
    private static async Task<HttpResponseMessage?> ДоОтказаAsync(Func<Task<HttpResponseMessage>> запрос, int раз = 15)
    {
        for (var попытка = 0; попытка < раз; попытка++)
        {
            var ответ = await запрос();

            if (ответ.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ответ;
            }
        }

        return null;
    }

    // --- временный пароль --------------------------------------------------------------------

    [Fact]
    public async Task Пока_пароль_не_сменён_остальное_закрыто()
    {
        // ⚠ ГЛАВНАЯ проверка блока: человек с временным паролем не должен ходить по API, пока
        // его не сменит, — иначе пароль, переданный по телефону, работал бы как постоянный.
        var email = await ProvisionalUserAsync();
        var access = (await LoginAsync(email, cookie: false))["access_token"]!.GetValue<string>();

        // Обычное действие закрыто, и код говорит клиенту, куда вести человека.
        var blocked = await SendAsync(HttpMethod.Get, "/api/users", access);

        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Envelope(blocked)).Code.ShouldBe("PASSWORD_CHANGE_REQUIRED");

        // А «кто я» открыто: экрану смены пароля надо знать, кто на нём стоит.
        (await SendAsync(HttpMethod.Get, "/api/me", access)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // И сама смена — ради неё ограничение и заведено.
        var changed = await SendAsync(
            HttpMethod.Post, "/api/me/password", access,
            new { current_password = Strong, new_password = Другой });

        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await host.CompleteInitialFactorAsync(Client(), access, Ct);

        // После смены пароля и подключения фактора ограничение снято: прав в установке нет,
        // и тот же запрос проходит.
        var after = await SendAsync(HttpMethod.Get, "/api/users", access);

        after.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Смена_пароля_без_текущего_не_проходит()
    {
        var email = await ProvisionalUserAsync();
        var access = (await LoginAsync(email, cookie: false))["access_token"]!.GetValue<string>();

        var response = await SendAsync(
            HttpMethod.Post, "/api/me/password", access,
            new { current_password = "Не-Тот-Пароль-1!", new_password = Другой });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Envelope(response)).Code.ShouldBe("INVALID_CREDENTIALS");
    }

    // --- второй фактор -----------------------------------------------------------------------

    [Fact]
    public async Task Пока_фактор_не_включён_суперпользователю_остальное_закрыто()
    {
        // ⚠ Умолчание системы: учётная запись, проходящая любую проверку прав, не должна
        // держаться на одном пароле. Ограничение устроено так же, как требование сменить
        // пароль, и проверяется тем же конвейером — но список исключений у него свой.
        var email = await SuperuserAsync();
        var access = (await LoginAsync(email, cookie: false, completeFactor: false))["access_token"]!.GetValue<string>();

        var blocked = await SendAsync(HttpMethod.Get, "/api/users", access);

        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Envelope(blocked)).Code.ShouldBe("TWO_FACTOR_ENROLLMENT_REQUIRED");

        // «Кто я» и включение фактора открыты — ради них ограничение и заведено.
        (await SendAsync(HttpMethod.Get, "/api/me", access)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var setup = await SendAsync(HttpMethod.Post, "/api/me/two-factor/totp/setup", access);

        setup.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secret = (await setup.Content.ReadFromJsonAsync<JsonObject>(Ct))!["secret"]!.GetValue<string>();

        // ⚠ ⚠ Пароля здесь БОЛЬШЕ НЕТ, и кодов восстановления в ответе тоже (решение владельца
        // 0061): включение усиливает защиту, а сессия уже доказала, кто это. Прежняя редакция
        // требовала десять кодов «иначе фактор — способ потерять доступ»; запасной путь теперь
        // один и другой — письмо, и проверяется он в модуле личностей.
        var confirmed = await SendAsync(
            HttpMethod.Post, "/api/me/two-factor/totp/confirm", access, new { code = CurrentCode(secret) });

        confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Ограничение снято, и тот же запрос проходит: суперпользователь проходит любую
        // проверку прав, значит единственным, что его останавливало, было требование
        // включить фактор — ровно то, что и проверяется.
        (await SendAsync(HttpMethod.Get, "/api/users", access)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Вход_с_включённым_фактором_идёт_в_два_шага()
    {
        var email = await SuperuserAsync();
        var access = (await LoginAsync(email, cookie: false, completeFactor: false))["access_token"]!.GetValue<string>();

        var setup = await SendAsync(HttpMethod.Post, "/api/me/two-factor/totp/setup", access);
        var secret = (await setup.Content.ReadFromJsonAsync<JsonObject>(Ct))!["secret"]!.GetValue<string>();

        (await SendAsync(HttpMethod.Post, "/api/me/two-factor/totp/confirm", access, new { code = CurrentCode(secret) }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Первый шаг: пароль принят, токенов нет — вместо них расписка.
        var first = await Client().PostAsJsonAsync(
            "/api/auth/login", new { email, password = Strong, use_cookie = false }, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var envelope = await Envelope(first);

        envelope.Code.ShouldBe("TWO_FACTOR_REQUIRED");
        envelope.Details.ShouldNotBeNull();

        var challenge = envelope.Details!["challenge"]!.ToString();

        // Второй шаг: расписка плюс код. Код берём следующий — свой шаг подтверждение уже
        // потратило, и это правильно: один код, один вход.
        var second = await Client().PostAsJsonAsync(
            "/api/auth/two-factor",
            new { challenge, code = CurrentCode(secret, steps: 1), use_cookie = false },
            Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var пара = (await second.Content.ReadFromJsonAsync<JsonObject>(Ct))!;

        пара["access_token"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();

        // ⚠ ⚠ ГЛАВНОЕ ЗДЕСЬ. Второй шаг обязан слушать `use_cookie` так же, как первый. Пока он
        // зашивал `true`, вход с включённым фактором отдавал половину обновления ТОЛЬКО кукой —
        // а у настольного клиента кук нет. Он получал пару без половины, сохранять ему было
        // нечего, и на следующем запуске он снова спрашивал логин, пароль и код. Нашлось живой
        // проверкой владельца 2026-09-04: «при повторном входе снова требует вход с 2ФА».
        пара["refresh_token"]?.GetValue<string>()
            .ShouldNotBeNullOrWhiteSpace("без половины обновления клиент не переживёт перезапуск");

        second.Headers.TryGetValues("Set-Cookie", out _)
            .ShouldBeFalse("просили тело — куки быть не должно");
    }

    /// <summary>Первый шаг входа с включённым фактором: расписка вместо токенов.</summary>
    private async Task<string> ChallengeAsync(string email)
    {
        var первый = await Client().PostAsJsonAsync(
            "/api/auth/login", new { email, password = Strong, use_cookie = false }, Ct);

        первый.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var конверт = await Envelope(первый);

        конверт.Code.ShouldBe("TWO_FACTOR_REQUIRED");

        var расписка = конверт.Details!["challenge"]!.ToString();

        расписка.ShouldNotBeNullOrWhiteSpace();

        return расписка;
    }

    // --- обвязка -----------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    private const string Другой = "Совсем-Другой-Пароль-77!";

    /// <summary>Человек с временным паролем: администратор задал пароль и велел его сменить.</summary>
    private async Task<string> ProvisionalUserAsync()
    {
        host.User.SignIn();

        var email = $"provisional-{Guid.CreateVersion7():N}@example.com";

        (await host.CreateActivatedUserAsync(Client(), new { email, password = Strong, must_change_password = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        host.User.SignOut();

        return email;
    }

    /// <summary>Запрос с токеном доступа. Код ответа не проверяется — его смотрит тест.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string access, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Client().SendAsync(request, Ct);
    }

    /// <summary>Суперпользователь: от него второй фактор требуется по умолчанию.</summary>
    private async Task<string> SuperuserAsync()
    {
        host.User.SignIn();

        var email = $"boss-{Guid.CreateVersion7():N}@example.com";

        (await host.CreateActivatedUserAsync(Client(), new { email, password = Strong, is_superuser = true, must_change_password = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        host.User.SignOut();

        return email;
    }

    /// <summary>
    /// Код, который показало бы приложение.
    /// </summary>
    /// <remarks>
    /// ⚠ Своя реализация, а не вызов серверной. Совпадение двух независимых реализаций —
    /// это проверка; вызов той же самой доказал бы только, что она равна себе.
    /// </remarks>
    private static string CurrentCode(string base32, int steps = 0)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;

        foreach (var symbol in base32)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(symbol, StringComparison.Ordinal);
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        var step = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30) + steps;
        var counter = new byte[8];

        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        var hash = System.Security.Cryptography.HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 0x0F;

        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Попробовать сменить пароль настоящим токеном и вернуть тело ответа.</summary>
    /// <remarks>
    /// ⚠ Возвращается тело, а не код: проверяется <b>что именно</b> назвал отказ, а не сам факт.
    /// </remarks>
    private async Task<string> Сменить(string токен, string текущий, string новый)
    {
        using var запрос = new HttpRequestMessage(HttpMethod.Post, "/api/me/password")
        {
            Content = JsonContent.Create(new { current_password = текущий, new_password = новый }),
        };

        запрос.Headers.Authorization = new AuthenticationHeaderValue("Bearer", токен);

        var ответ = await Client().SendAsync(запрос, Ct);

        return await ответ.Content.ReadAsStringAsync(Ct);
    }

    private HttpClient Client() => host.CreateClient();

    /// <summary>Завести живого человека с известным паролем и вернуться к настоящему конвейеру.</summary>
    private async Task<string> ActiveUserAsync()
    {
        host.User.SignIn();

        var email = $"user-{Guid.CreateVersion7():N}@example.com";

        // ⚠ must_change_password: false задаётся ЯВНО. Умолчание — true, и это правильное
        // умолчание: пароль, заданный администратором, временный по существу. Но здесь нужен
        // человек, готовый работать, а не проверка ограничения — она рядом и своя.
        (await host.CreateActivatedUserAsync(Client(), new { email, password = Strong, must_change_password = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // ⚠ Снимаем подмену: дальше всё идёт настоящим конвейером.
        host.User.SignOut();

        return email;
    }

    private async Task<JsonObject> LoginAsync(string email, bool cookie, bool completeFactor = true)
    {
        var response = await host.LoginFixtureAsync(Client(), email, Strong, cookie, Ct, completeFactor);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private async Task<JsonObject> RefreshAsync(string refresh)
    {
        var response = await Client().PostAsJsonAsync(
            "/api/auth/refresh", new { refresh_token = refresh, use_cookie = false }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private async Task<T?> GetAsync<T>(string path, string access)
        where T : JsonNode
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        var response = await Client().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<T>(Ct);
    }

    /// <summary>Значение куки с токеном обновления из ответа.</summary>
    private static string Refresh(HttpResponseMessage response) => response.Headers
        .GetValues("Set-Cookie")
        .First(value => value.StartsWith("__Host-sender-refresh=", StringComparison.Ordinal))
        .Split(';')[0]
        .Split('=', 2)[1];

    private static async Task<ErrorResponse> Envelope(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web), Ct))!;

    /// <summary>
    /// Требования к паролю отдаются вошедшему — и совпадают с тем, чем он отказывает.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>АДРЕСА НЕ БЫЛО ВОВСЕ (этап 37.1), а веб-экраны обещали чек-лист комментарием в
    /// разметке.</b> Человек узнавал требования единственным способом — отказом: придумывал
    /// пароль, получал список и придумывал заново.
    ///
    /// ⚠ ⚠ <b>Проверяется СОВПАДЕНИЕ списка с отказом, а не сам список.</b> Два списка про одно и
    /// то же расходятся молча: экран покажет пять требований, отказ назовёт шестое — и человек
    /// будет прав, решив, что программа врёт.
    ///
    /// Мутация 2026-09-07: <c>CurrentAsync</c> читает <c>RequireSymbol</c> вместо
    /// <c>RequireDigit</c> → покраснело совпадение.
    /// </remarks>
    [Fact]
    public async Task Требования_к_паролю_совпадают_с_отказом()
    {
        // ⚠ ⚠ Живая учётная запись и настоящий вход: подделка личности хоста не заводит строки
        // в базе, и смена пароля отвечает «пользователь не найден» — то есть проверять было бы
        // нечего.
        var почта = $"policy-{Guid.CreateVersion7():N}@example.com";
        const string Пароль = "Очень-Длинный-Пароль-42";

        host.User.SignIn();

        var создан = await host.CreateActivatedUserAsync(Client(), new { email = почта, password = Пароль, must_change_password = false }, Ct);

        создан.StatusCode.ShouldBe(HttpStatusCode.Created);

        host.User.SignOut();

        var вход = await Client().PostAsJsonAsync(
            "/api/auth/login", new { email = почта, password = Пароль, use_cookie = false }, Ct);

        вход.StatusCode.ShouldBe(HttpStatusCode.OK);

        var токен = (await вход.Content.ReadFromJsonAsync<JsonObject>(Ct))!["access_token"]!
            .GetValue<string>();

        using var спросить = new HttpRequestMessage(HttpMethod.Get, "/api/auth/password-policy");

        спросить.Headers.Authorization = new AuthenticationHeaderValue("Bearer", токен);

        var ответ = await Client().SendAsync(спросить, Ct);

        ответ.StatusCode.ShouldBe(HttpStatusCode.OK, "адрес политики заведён этапом 37.1");

        var правила = await ответ.Content.ReadFromJsonAsync<JsonObject>(Ct);

        правила.ShouldNotBeNull();

        var предел = правила["min_length"]!.GetValue<int>();

        предел.ShouldBeGreaterThan(0);

        // ⚠ ⚠ ⚠ ГРАНИЦА ПРОВЕРЯЕТСЯ С ОБЕИХ СТОРОН, и это не педантизм. Число, названное
        // политикой, — то самое, по которому судит сервер: ошибись оно на единицу, и чек-лист
        // покажет галочку у пароля, который сервер отвергнет. Мутация «вернуть предел + 1»
        // покраснеет здесь и нигде больше.
        var короткий = new string('ы', предел - 1);
        var ровно = new string('ы', предел);

        (await Сменить(токен, Пароль, короткий)).ShouldContain(
            "min_length", Case.Sensitive, "на символ короче предела — отказ обязан назвать длину");

        (await Сменить(токен, Пароль, ровно)).ShouldNotContain(
            "min_length", Case.Sensitive, "ровно предел — длина больше не нарушение");

        // ⚠ ⚠ Пароль подобран НАРОЧНО: длинный и из одних строчных букв. Он проходит длину и
        // «строчная буква» и нарушает три остальных — значит отказ обязан назвать ровно три
        // кода и промолчать про четвёртый. Пустая строка сюда не годится: её отвергает
        // проверка модели раньше политики, и до неё дело не доходит вовсе.
        var одниСтрочные = new string('ы', 40);

        var тело = await Сменить(токен, Пароль, одниСтрочные);

        foreach (var (поле, код) in new[]
        {
            ("require_uppercase", "uppercase"),
            ("require_digit", "digit"),
            ("require_symbol", "symbol"),
        })
        {
            if (правила[поле]!.GetValue<bool>())
            {
                тело.ShouldContain(
                    код,
                    Case.Sensitive,
                    $"политика обещает «{поле}», а отказ про «{код}» молчит — два разных списка");
            }
        }

        // ⚠ Обратная сторона: список нарушений — не «всё подряд». Строчная буква в пароле есть,
        // и жаловаться на неё отказ не имеет права.
        тело.ShouldNotContain(
            "lowercase",
            Case.Sensitive,
            "отказ перечисляет требования, а не нарушения — тогда он ничего не объясняет");
    }
}
