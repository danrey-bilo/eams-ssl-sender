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

/// <summary>Вход по HTTP — настоящим конвейером, без подмены <c>ICurrentUser</c>.</summary>
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

        // ⚠ Токена обновления в теле нет: он ушёл кукой HttpOnly, недоступной сценарию.
        body["refresh_token"].ShouldBeNull();

        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.Contains("__Host-"));

        cookie.ShouldContain("__Host-sender-refresh=");
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);

        // С префиксом __Host- домен не задаётся: иначе браузер отбросит куку.
        cookie.ShouldNotContain("domain=", Case.Insensitive);
    }

    /// <summary>Ни вход, ни обновление. Токен настоящий и убит выходом: выдуманный ничего бы не проверил.</summary>
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

    /// <summary>⚠ Договор с клиентами: пару они обменивают только на <c>TOKEN_EXPIRED</c>, не <c>SESSION_EXPIRED</c>.</summary>
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

        // Половина обновления жива, новый доступ работает.
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

        // Совпадают код, текст и подробности; номер обращения у каждого запроса свой.
        first.Code.ShouldBe(second.Code);
        first.Message.ShouldBe(second.Message);
        first.Details.ShouldBeNull();
        second.Details.ShouldBeNull();
    }

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

    /// <summary>⚠ Даже при <c>use_cookie: false</c>: иначе сценарий страницы унёс бы сессию из куки <c>HttpOnly</c>.</summary>
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
        // ⚠ RFC 9700: потраченный токен есть у двоих, вор неизвестен — закрывается вся сессия.
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

            // Человеку сказано, что случилось, а не голое «войдите заново».
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

        // Токен доступа больше не работает — с внятной причиной.
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

    /// <summary>⚠ 429, а не 503 платформы: на 503 клиент решил бы, что сервер лежит. Почты разные — потолок по адресу.</summary>
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

    /// <summary>За офисным NAT адрес общий: перебор держит предел на пару «адрес + почта».</summary>
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

    /// <summary>Переподтверждение и смена пароля режутся пределом на человека; потолок по адресу выше.</summary>
    [Theory]
    [InlineData("/api/me/step-up")]
    [InlineData("/api/me/password")]
    public async Task Переспрос_внутри_сессии_режется_частотой(string путь)
    {
        using var strict = Строгий(("RateLimits:LoginPerMinute", "1"));
        using var client = strict.CreateClient();

        host.User.SignIn();

        // Три попытки: потолок по адресу — десять, отказ обязан прийти от предела на человека.
        var limited = await ДоОтказаAsync(() => client.PostAsJsonAsync(путь, new { code = "000000" }, Ct), раз: 3);

        limited.ShouldNotBeNull($"путь {путь} частотой не режется");
        (await Envelope(limited!)).Code.ShouldBe("RATE_LIMITED");

        host.User.SignIn();

        (await client.PostAsJsonAsync(путь, new { code = "000000" }, Ct))
            .StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, "сосед по адресу заперт чужим пределом");
    }

    /// <summary>Ограничитель по адресу стоит до опознания: 429 приходит и без токена.</summary>
    [Fact]
    public async Task Поток_переспроса_без_токена_режется_по_адресу()
    {
        using var strict = Строгий(("RateLimits:LoginPerMinute", "1"), ("RateLimits:AddressFactor", "1"));
        using var client = strict.CreateClient();

        host.User.SignOut();

        var limited = await ДоОтказаAsync(() => client.PostAsJsonAsync("/api/me/step-up", new { code = "000000" }, Ct));

        limited.ShouldNotBeNull("поток без токена частотой не режется");
    }

    /// <summary>Свой хост с заданными пределами на той же базе: пределы общие на хост, соседей не выжигаем.</summary>
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

    [Fact]
    public async Task Пока_пароль_не_сменён_остальное_закрыто()
    {
        // ⚠ Иначе временный пароль, переданный по телефону, работал бы как постоянный.
        var email = await ProvisionalUserAsync();
        var access = (await LoginAsync(email, cookie: false))["access_token"]!.GetValue<string>();

        // Код говорит клиенту, куда вести человека.
        var blocked = await SendAsync(HttpMethod.Get, "/api/users", access);

        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Envelope(blocked)).Code.ShouldBe("PASSWORD_CHANGE_REQUIRED");

        // А «кто я» открыто: экрану смены пароля надо знать, кто на нём стоит.
        (await SendAsync(HttpMethod.Get, "/api/me", access)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var changed = await SendAsync(
            HttpMethod.Post, "/api/me/password", access,
            new { current_password = Strong, new_password = Другой });

        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await host.CompleteInitialFactorAsync(Client(), access, Ct);

        // После смены пароля и фактора ограничение снято.
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

    [Fact]
    public async Task Пока_фактор_не_включён_суперпользователю_остальное_закрыто()
    {
        // ⚠ Учётная запись, проходящая любую проверку прав, не держится на одном пароле.
        var email = await SuperuserAsync();
        var access = (await LoginAsync(email, cookie: false, completeFactor: false))["access_token"]!.GetValue<string>();

        var blocked = await SendAsync(HttpMethod.Get, "/api/users", access);

        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Envelope(blocked)).Code.ShouldBe("TWO_FACTOR_ENROLLMENT_REQUIRED");

        // «Кто я» и включение фактора открыты.
        (await SendAsync(HttpMethod.Get, "/api/me", access)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var setup = await SendAsync(HttpMethod.Post, "/api/me/two-factor/totp/setup", access);

        setup.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secret = (await setup.Content.ReadFromJsonAsync<JsonObject>(Ct))!["secret"]!.GetValue<string>();

        // Ни пароля, ни кодов восстановления: сессия уже доказала, кто это.
        var confirmed = await SendAsync(
            HttpMethod.Post, "/api/me/two-factor/totp/confirm", access, new { code = CurrentCode(secret) });

        confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Суперпользователя держало только требование фактора — теперь запрос проходит.
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

        // Второй шаг: расписка и следующий код — текущий потратило подтверждение.
        var second = await Client().PostAsJsonAsync(
            "/api/auth/two-factor",
            new { challenge, code = CurrentCode(secret, steps: 1), use_cookie = false },
            Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var пара = (await second.Content.ReadFromJsonAsync<JsonObject>(Ct))!;

        пара["access_token"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();

        // ⚠ Второй шаг слушает `use_cookie`, как первый: у настольного клиента кук нет.
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

    /// <summary>Код приложения — своей реализацией: серверная доказала бы только, что равна себе.</summary>
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

    /// <summary>Сменить пароль и вернуть тело: проверяется, что именно назвал отказ.</summary>
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

        // must_change_password задаётся явно: умолчание — true.
        (await host.CreateActivatedUserAsync(Client(), new { email, password = Strong, must_change_password = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Дальше — настоящий конвейер.
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

    /// <summary>⚠ Проверяется совпадение политики с отказом, а не сам список: два списка расходятся молча.</summary>
    [Fact]
    public async Task Требования_к_паролю_совпадают_с_отказом()
    {
        // Живая учётная запись: подделка личности не заводит строки в базе.
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

        // ⚠ Граница с обеих сторон: ошибка на единицу покажет галочку у пароля, который сервер отвергнет.
        var короткий = new string('ы', предел - 1);
        var ровно = new string('ы', предел);

        (await Сменить(токен, Пароль, короткий)).ShouldContain(
            "min_length", Case.Sensitive, "на символ короче предела — отказ обязан назвать длину");

        (await Сменить(токен, Пароль, ровно)).ShouldNotContain(
            "min_length", Case.Sensitive, "ровно предел — длина больше не нарушение");

        // Одни строчные: проходит длину и «строчную», нарушает три остальных требования.
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

        // Строчная буква есть — жаловаться на неё отказ не вправе.
        тело.ShouldNotContain(
            "lowercase",
            Case.Sensitive,
            "отказ перечисляет требования, а не нарушения — тогда он ничего не объясняет");
    }
}
