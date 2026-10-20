using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Учётные записи по HTTP: права, приглашение, активация.
/// </summary>
/// <remarks>
/// Здесь проверяется то, чего не видно изнутри модуля: что маршруты подняты, что конвейер
/// прав их закрывает и что активация работает <b>без входа</b> — иначе приглашённый заперт
/// снаружи навсегда.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class UsersApi(SenderHost host)
{
    [Fact]
    public async Task Список_без_входа_отвечает_401_а_не_404()
    {
        // 404 означал бы, что контроллер не найден маршрутизацией — самая незаметная
        // поломка модульного монолита.
        host.User.SignOut();

        (await Client().GetAsync("/api/users", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Заведение_отдаёт_ссылку_приглашения()
    {
        // ⚠ Ссылка в теле ответа — временно, до появления почты. Отправлять её пока нечем,
        // а без неё приглашённый не войдёт никогда.
        SignIn("user.create");

        var created = await CreateAsync(Email());

        created["invitation_token"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        created["invitation_expires_at"].ShouldNotBeNull();
        created["user"]!["activated_at"].ShouldBeNull();
    }

    [Fact]
    public async Task Ответ_не_содержит_хеша_пароля()
    {
        // ⚠ Схема выхода — то место, где секрет утекает тише всего: поле добавляют «на
        // время отладки», а замечают через год в чужом отчёте об ошибке.
        SignIn("user.create");

        var response = await Client().PostAsJsonAsync(
            "/api/users", new { email = Email(), password = Strong }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        body.ShouldNotContain("password_hash");
        body.ShouldNotContain("argon2");
        body.ShouldNotContain(Strong);
    }

    // --- активация ------------------------------------------------------------------------

    [Fact]
    public async Task Активация_доступна_без_входа()
    {
        // ⚠ Свойство, без которого весь путь приглашения бессмыслен: человек, который
        // активирует учётную запись, ещё не может войти — у него нет пароля.
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

    /// <summary>
    /// Ссылка приглашения проверяется телом запроса; адресом — больше никак.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревью безопасности 2026-10-20. Прежде это был <c>GET /api/activation?token=…</c>: адрес
    /// запроса оседает в журналах Cloudflare и любого прокси по пути, и действующая ссылка
    /// приглашения лежала там открытым текстом весь свой срок. Старый путь не должен отвечать
    /// сведениями — иначе забытый клиент продолжал бы класть ссылку в адрес.
    /// </remarks>
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
        // ⚠ Все нарушения разом, а не первое найденное: иначе человек узнаёт требования по
        // одному за попытку, и форма превращается в игру «угадай следующее правило».
        SignIn("user.create");

        var token = (await CreateAsync(Email()))["invitation_token"]!.GetValue<string>();

        host.User.SignOut();

        var response = await Client().PostAsJsonAsync(
            "/api/activation", new { token, password = "коротко" }, Ct);
        var envelope = await Envelope(response);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        envelope.Code.ShouldBe("PASSWORD_POLICY_VIOLATION");

        // Машинные коды рядом с русским текстом: клиент показывает свои формулировки, а не
        // подставляет чужие в переведённый шаблон.
        envelope.Details!["codes"]!.ToString()!.ShouldContain("min_length");

        // И самого пароля в ответе нет: ответы валидации уходят в логи клиента.
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

    // --- инварианты по HTTP -----------------------------------------------------------------

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
        // ⚠ Правило редактирования секретных имён: последнее слово `invitation_token`
        // секретно. Без этого пропуск в систему лежал бы в append-only таблице вечно.
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
        // ⚠ Найдено живой проверкой. Атрибут [ApiController] сам отвечает на непрошедший
        // разбор — и отвечает ProblemDetails по RFC 9457, мимо нашего конверта, мимо
        // request_id и мимо машинных кодов. Клиент, написанный под наш формат, показал бы
        // пустую ошибку вместо внятной.
        SignIn("user.create");

        using var broken = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await Client().PostAsync("/api/users", broken, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var envelope = await Envelope(response);

        envelope.Code.ShouldBe(ErrorCodes.Validation);
        envelope.RequestId.ShouldNotBeNullOrWhiteSpace();
        envelope.Details.ShouldNotBeNull();

        // И чужого формата в ответе нет ни следа.
        var raw = await response.Content.ReadAsStringAsync(Ct);
        raw.ShouldNotContain("traceId");
        raw.ShouldNotContain("tools.ietf.org");
    }

    [Fact]
    public async Task Присланный_пароль_в_ответ_валидации_не_попадает()
    {
        // Сообщения валидации содержат присланные значения, а присылают туда в том числе
        // пароли. Ответ уходит в лог клиента и в отчёты об ошибках.
        SignIn("user.create");

        using var body = new StringContent(
            """{"email":"","password":"секрет-который-нельзя-показывать"}""",
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await Client().PostAsync("/api/users", body, Ct);

        (await response.Content.ReadAsStringAsync(Ct))
            .ShouldNotContain("секрет-который-нельзя-показывать");
    }

    // --- обвязка -----------------------------------------------------------------------------

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
