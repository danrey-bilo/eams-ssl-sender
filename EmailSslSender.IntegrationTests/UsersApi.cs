using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Core.Errors;

namespace EmailSslSender.IntegrationTests;

/// <summary>Учётные записи по HTTP: маршруты, права, приглашение, активация без входа.</summary>
[Collection(SenderHost.Collection)]
public sealed class UsersApi(SenderHost host)
{
    [Fact]
    public async Task Список_без_входа_отвечает_401_а_не_404()
    {
        // 404 значил бы, что маршрутизация не нашла контроллер модуля.
        host.User.SignOut();

        (await Client().GetAsync("/api/users", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Заведение_отдаёт_ссылку_приглашения()
    {
        SignIn("user.create");

        var created = await CreateAsync(Email());

        created["invitation_token"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        created["invitation_expires_at"].ShouldNotBeNull();
        created["user"]!["activated_at"].ShouldBeNull();
    }

    [Fact]
    public async Task Ответ_не_содержит_хеша_пароля()
    {
        SignIn("user.create");

        var response = await Client().PostAsJsonAsync(
            "/api/users", new { email = Email(), password = Strong }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        body.ShouldNotContain("password_hash");
        body.ShouldNotContain("argon2");
        body.ShouldNotContain(Strong);
    }

    [Fact]
    public async Task Активация_доступна_без_входа()
    {
        // ⚠ У активирующего ещё нет пароля — войти он не может.
        SignIn("user.create");

        var created = await CreateAsync(Email());
        var token = created["invitation_token"]!.GetValue<string>();

        host.User.SignOut();

        var info = await (await Client().PostAsJsonAsync("/api/activation/info", new { token }, Ct))
            .Content.ReadFromJsonAsync<JsonObject>(Ct);
        info!["email"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();

        var response = await Client().PostAsJsonAsync(
            "/api/activation", new { token, password = Strong }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["activated_at"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Чтение_ссылки_её_не_гасит()
    {
        // Случайно закрытая вкладка не должна требовать нового приглашения.
        SignIn("user.create");

        var token = (await CreateAsync(Email()))["invitation_token"]!.GetValue<string>();

        host.User.SignOut();

        (await Client().PostAsJsonAsync("/api/activation/info", new { token }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client().PostAsJsonAsync("/api/activation/info", new { token }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await Client().PostAsJsonAsync(
            "/api/activation", new { token, password = Strong }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>⚠ Адрес запроса оседает в журналах Cloudflare и прокси — ссылка только телом.</summary>
    [Fact]
    public async Task Ссылку_приглашения_в_адресе_не_принимают()
    {
        SignIn("user.create");

        var token = (await CreateAsync(Email()))["invitation_token"]!.GetValue<string>();

        host.User.SignOut();

        var поАдресу = await Client().GetAsync($"/api/activation?token={token}", Ct);

        поАдресу.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed, "ссылка приглашения снова принимается в адресе");
        (await поАдресу.Content.ReadAsStringAsync(Ct)).ShouldNotContain("@", Case.Sensitive, "по ссылке в адресе выданы сведения");

        var телом = await Client().PostAsJsonAsync("/api/activation/info", new { token }, Ct);

        телом.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await телом.Content.ReadFromJsonAsync<JsonObject>(Ct))!["policy"].ShouldNotBeNull();

        (await Client().PostAsJsonAsync("/api/activation/info", new { token = "ссылки-такой-нет-совсем" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Непригодный_пароль_отвечает_списком_нарушений()
    {
        // Все нарушения разом, а не по одному за попытку.
        SignIn("user.create");

        var token = (await CreateAsync(Email()))["invitation_token"]!.GetValue<string>();

        host.User.SignOut();

        var response = await Client().PostAsJsonAsync(
            "/api/activation", new { token, password = "коротко" }, Ct);
        var envelope = await Envelope(response);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        envelope.Code.ShouldBe("PASSWORD_POLICY_VIOLATION");

        // Машинные коды: клиент показывает свои формулировки.
        envelope.Details!["codes"]!.ToString()!.ShouldContain("min_length");

        // Пароля в ответе нет: ответы валидации уходят в логи клиента.
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("коротко");
    }

    [Fact]
    public async Task Отказ_в_активации_одинаков_на_любую_причину()
    {
        host.User.SignOut();

        var response = await Client().PostAsJsonAsync(
            "/api/activation", new { token = "ссылки-такой-нет-совсем", password = Strong }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Envelope(response)).Code.ShouldBe("ACTIVATION_TOKEN_INVALID");
    }

    /// <summary>⚠ Номер случайный: отказ — в фильтре до разбора ресурса, иначе 404/403 выдали бы, кто существует.</summary>
    [Fact]
    public async Task Действия_над_доступом_без_свежего_кода_отвергаются()
    {
        SignIn("user.manage");
        host.User.StepUpAt = DateTimeOffset.UtcNow.AddMinutes(-11);

        try
        {
            var кто = $"/api/users/{Guid.CreateVersion7()}";
            var действия = new (HttpMethod Метод, string Путь)[]
            {
                (HttpMethod.Post, "/api/users"),
                (HttpMethod.Post, $"{кто}/invite"),
                (HttpMethod.Post, $"{кто}/reset-password"),
                (HttpMethod.Post, $"{кто}/disable"),
                (HttpMethod.Post, $"{кто}/enable"),
                (HttpMethod.Post, $"{кто}/unlock"),
                (HttpMethod.Delete, кто),
                (HttpMethod.Delete, $"{кто}/two-factor"),
                (HttpMethod.Delete, $"{кто}/sessions"),
                (HttpMethod.Patch, кто),
            };

            foreach (var (метод, путь) in действия)
            {
                using var запрос = new HttpRequestMessage(метод, путь)
                {
                    Content = JsonContent.Create(new { email = Email() }),
                };

                var ответ = await Client().SendAsync(запрос, Ct);

                ответ.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{метод} {путь}");
                (await Envelope(ответ)).Code.ShouldBe(ErrorCodes.SecondFactorRequired, $"{метод} {путь}");
            }

            // Чтение кода не спрашивает.
            (await Client().GetAsync("/api/users", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            // Давняя отметка не достаётся соседям по коллекции.
            host.User.StepUpAt = DateTimeOffset.UtcNow;
        }
    }

    [Fact]
    public async Task Занятый_адрес_отвечает_409()
    {
        SignIn("user.create");

        var email = Email();
        await CreateAsync(email);

        var response = await Client().PostAsJsonAsync("/api/users", new { email }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Envelope(response)).Code.ShouldBe("USER_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Ссылка_приглашения_в_журнал_не_попадает()
    {
        // ⚠ Журнал только дописывается: пропуск в систему лежал бы там вечно.
        SignIn("user.create", "audit.read");

        var created = await CreateAsync(Email());
        var id = created["user"]!["id"]!.GetValue<Guid>();
        var token = created["invitation_token"]!.GetValue<string>();

        var page = await Client().GetAsync($"/api/audit?resource_type=user&resource_id={id}", Ct);

        (await page.Content.ReadAsStringAsync(Ct)).ShouldNotContain(token);
    }

    [Fact]
    public async Task Ошибка_разбора_приходит_нашим_конвертом()
    {
        // ⚠ [ApiController] сам отвечает ProblemDetails мимо нашего конверта.
        SignIn("user.create");

        using var broken = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await Client().PostAsync("/api/users", broken, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var envelope = await Envelope(response);

        envelope.Code.ShouldBe(ErrorCodes.Validation);
        envelope.RequestId.ShouldNotBeNullOrWhiteSpace();
        envelope.Details.ShouldNotBeNull();

        // Следов ProblemDetails нет.
        var raw = await response.Content.ReadAsStringAsync(Ct);
        raw.ShouldNotContain("traceId");
        raw.ShouldNotContain("tools.ietf.org");
    }

    [Fact]
    public async Task Присланный_пароль_в_ответ_валидации_не_попадает()
    {
        // Сообщения валидации повторяют присланное, а ответ уходит в лог клиента.
        SignIn("user.create");

        using var body = new StringContent(
            """{"email":"","password":"секрет-который-нельзя-показывать"}""",
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await Client().PostAsync("/api/users", body, Ct);

        (await response.Content.ReadAsStringAsync(Ct))
            .ShouldNotContain("секрет-который-нельзя-показывать");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    private static string Email() => $"user-{Guid.CreateVersion7():N}@example.com";

    private HttpClient Client() => host.CreateClient();

    private Guid SignIn(params string[] permissions)
    {
        var id = host.User.SignIn();

        return id;
    }

    private async Task<JsonObject> CreateAsync(string email)
    {
        var response = await Client().PostAsJsonAsync("/api/users", new { email }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private static async Task<ErrorResponse> Envelope(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web), Ct))!;
}
