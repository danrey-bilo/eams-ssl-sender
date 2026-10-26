using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Hosting;
using Modules.Core.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Конверт ошибок: любая беда выходит наружу одинаково и без подробностей внутри.
/// </summary>
/// <remarks>
/// Два уровня, и это не дублирование. Настоящий хост проверяет, что конвейер собран и
/// порядок слоёв верен; мини-конвейер — что обработчик делает с исключением, потому что в
/// настоящем приложении пока нет эндпоинта, который умеет падать, а заводить его ради
/// теста значит оставить его в бою.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class ErrorEnvelope(SenderHost host)
{
    // --- настоящий хост -------------------------------------------------------------

    [Fact]
    public async Task Здоровье_отвечает_без_базы()
    {
        // Проверка живости не должна зависеть от базы: иначе оркестратор станет
        // перезапускать здоровое приложение всякий раз, когда недоступна база.
        var response = await host.CreateClient().GetAsync("/api/health", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Номер_обращения_возвращается_заголовком()
    {
        var response = await host.CreateClient().GetAsync("/api/health", Ct);

        response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Свой_номер_обращения_принимается_а_не_подменяется()
    {
        // За туннелем и обратным прокси номер могут присвоить раньше нас. Завести свой
        // означало бы разорвать цепочку ровно там, где она нужнее всего.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add(RequestIdMiddleware.HeaderName, "outside-123");

        var response = await host.CreateClient().SendAsync(request, Ct);

        response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single().ShouldBe("outside-123");
    }

    /// <summary>Номер с управляющим знаком заменяется своим, а не роняет запрос.</summary>
    /// <remarks>
    /// ⚠ ⚠ <b>До этапа 59.6 это была пятисотка на ЛЮБОМ эндпоинте, включая <c>/api/health</c>.</b>
    /// Номер с входа принимается как есть и ставится в заголовок ответа, а Kestrel отвергает
    /// управляющий знак в значении заголовка исключением — то есть положить установку на лопатки
    /// можно было одним запросом с кривым заголовком, без учётной записи и без прав.
    ///
    /// ⚠ <b>Заменяется своим, а не отвергается запросом.</b> Отказать в обслуживании из-за
    /// заголовка значило бы поменять одну пятисотку на другую; цепочку с прокси при этом рвёт не
    /// сервер, а тот, кто прислал такое значение.
    ///
    /// ⚠ Соседний факт выше проверяет обратное — что <b>годный</b> чужой номер доезжает
    /// нетронутым, и проверяет кириллицей. Оба нужны: первая редакция этой правки просеивала
    /// состав списком разрешённых знаков и ломала ровно его.
    /// </remarks>
    [Fact]
    public async Task Номер_обращения_с_управляющим_знаком_не_роняет_запрос()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");

        // ⚠ Через `TryAddWithoutValidation`: обычный `Add` такое значение не пропустит сам, а
        // проверяется здесь поведение СЕРВЕРА, а не клиентской библиотеки.
        request.Headers.TryAddWithoutValidation(RequestIdMiddleware.HeaderName, "плохой\u0001номер");

        var response = await host.CreateClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, "управляющий знак в номере обращения уронил запрос");

        response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single()
            .ShouldNotContain("\u0001", Case.Sensitive, "управляющий знак уехал в ответ");
    }

    // --- мини-конвейер: поведение обработчика ---------------------------------------

    [Fact]
    public async Task Известная_ошибка_выходит_своим_кодом_и_статусом()
    {
        using var server = await PipelineAsync(_ => throw new AppException(
            ErrorCodes.SecondFactorRequired, "Подтвердите кодом.", HttpStatusCode.Forbidden));

        var response = await server.GetTestClient().GetAsync("/", Ct);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        body!.Code.ShouldBe(ErrorCodes.SecondFactorRequired);
        body.RequestId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Подробности_известной_ошибки_доезжают_до_клиента()
    {
        // `details` — машинное поле: по нему клиент показывает, чего именно не хватило.
        // Секретов там не бывает по определению, оно уезжает целиком.
        using var server = await PipelineAsync(_ => throw new AppException(
            ErrorCodes.Validation, "Не прошло проверку", HttpStatusCode.BadRequest,
            new Dictionary<string, object?> { ["field"] = "email" }));

        var body = await (await server.GetTestClient().GetAsync("/", Ct))
            .Content.ReadFromJsonAsync<ErrorResponse>(Ct);

        body!.Details.ShouldNotBeNull();
        body.Details!["field"]!.ToString().ShouldBe("email");
    }

    [Fact]
    public async Task Непредвиденное_исключение_не_выносит_наружу_ни_слова_из_себя()
    {
        // Самое важное утверждение файла. Текст исключения содержит то, что знает код:
        // имена таблиц, куски запросов, а у сбоя подключения — строку подключения с
        // паролем целиком. Наружу уходит номер обращения и ничего больше.
        const string Secret = "Host=postgres;Password=совершенно-секретно";
        using var server = await PipelineAsync(_ => throw new InvalidOperationException(Secret));

        var response = await server.GetTestClient().GetAsync("/", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(raw, Web);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        body!.Code.ShouldBe(ErrorCodes.Internal);
        body.Details.ShouldBeNull();
        raw.ShouldNotContain("совершенно-секретно");
        raw.ShouldNotContain("postgres");
        raw.ShouldNotContain(nameof(InvalidOperationException));
    }

    /// <summary>Тело больше предела — 413 своим кодом, а не пятисотка.</summary>
    /// <remarks>
    /// ⚠ Найдено аудитом надёжности 2026-10-19 (решение 0095): отказ платформы
    /// (<c>BadHttpRequestException</c>) уходил <c>INTERNAL_ERROR</c>, и клиент предлагал
    /// сообщить администратору о файле, который просто великоват.
    /// </remarks>
    // мутация 2026-10-19: убрал перехват BadHttpRequestException → покраснел этот факт (500).
    [Fact]
    public async Task Отказ_платформы_выходит_своим_статусом()
    {
        using var server = await PipelineAsync(_ => throw new BadHttpRequestException("тело велико", 413));

        var response = await server.GetTestClient().GetAsync("/", Ct);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        body!.Code.ShouldBe(ErrorCodes.PayloadTooLarge);
    }

    /// <summary>Отказ без тела под <c>/api</c> — конвертом; вне <c>/api</c> — не трогается.</summary>
    // мутация 2026-10-19: убрал вызов ЗавернутьПустойОтказAsync → покраснели этот факт и адреса хоста ниже.
    [Fact]
    public async Task Пустой_отказ_под_api_заворачивается_в_конверт()
    {
        using var server = await PipelineAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return Task.CompletedTask;
        });

        var подApi = await server.GetTestClient().GetAsync("/api/нет-такого", Ct);
        var body = await подApi.Content.ReadFromJsonAsync<ErrorResponse>(Ct);

        подApi.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body!.Code.ShouldBe(ErrorCodes.NotFound);
        body.RequestId.ShouldNotBeNullOrWhiteSpace();

        var внеApi = await server.GetTestClient().GetAsync("/ws/нет-такого", Ct);

        (await внеApi.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty("ответ вне /api переписан конвертом");
    }

    /// <summary>Неизвестный адрес и чужой метод настоящего хоста — конвертом.</summary>
    [Fact]
    public async Task Неизвестный_адрес_и_чужой_метод_отвечают_конвертом()
    {
        var нет = await host.CreateClient().GetAsync("/api/такого-адреса-нет", Ct);

        нет.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await нет.Content.ReadFromJsonAsync<ErrorResponse>(Ct))!.Code.ShouldBe(ErrorCodes.NotFound);

        var чужой = await host.CreateClient().DeleteAsync("/api/health", Ct);

        чужой.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await чужой.Content.ReadFromJsonAsync<ErrorResponse>(Ct))!.Code.ShouldBe(ErrorCodes.MethodNotAllowed);
    }

    [Fact]
    public async Task Номер_обращения_в_ответе_и_в_конверте_совпадают()
    {
        // Иначе сотрудник поддержки получит от человека один номер, а в логе будет другой.
        using var server = await PipelineAsync(_ => throw AppException.NotFound());

        var response = await server.GetTestClient().GetAsync("/", Ct);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(Ct);

        body!.RequestId.ShouldBe(response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public void Конверт_сериализуется_в_snake_case()
    {
        // Контракт совпадает с Python-версией, чтобы уже написанный веб-интерфейс переехал
        // без правок. `requestId` вместо `request_id` сломал бы разбор молча — поле просто
        // оказалось бы пустым.
        var json = JsonSerializer.Serialize(
            new ErrorResponse(ErrorCodes.NotFound, "Не найдено", null, "abc"), Web);

        json.ShouldContain("\"request_id\":\"abc\"");
        json.ShouldContain("\"code\":\"NOT_FOUND\"");
        json.ShouldNotContain("requestId");
    }

    // --- обвязка ---------------------------------------------------------------------

    /// <summary>
    /// Номер обращения не отражается в теле успешного ответа.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ ⚠ <b>Это машинная половина довода, которым разрешено сжатие на прокси (этап 60.1).</b>
    /// BREACH требует, чтобы в одном ответе встретились <b>секрет</b> и <b>значение, пришедшее из
    /// запроса</b>: тогда по длине сжатого тела значение подбирают по знаку. Секрет в теле есть
    /// только у пары токенов и у показа секрета, и оба — только под <c>Authorization: Bearer</c>,
    /// который чужая страница проставить не может. Единственное отражаемое значение — номер
    /// обращения; в телах <b>ошибок</b> он законно есть (по нему ищут строку в логе), а в телах
    /// <b>успеха</b> его быть не должно.
    ///
    /// ⚠ ⚠ <b>Проверяются 2xx, и только они.</b> Требовать того же от конверта ошибок значило бы
    /// отобрать у поддержки единственную ниточку от жалобы к строке лога. Разница не косметическая:
    /// у ошибки нет секрета в теле, у успеха бывает.
    ///
    /// ⚠ Номер задаётся <b>своим</b>, узнаваемым: сервер принимает присланный
    /// (<c>RequestIdMiddleware</c>), и подстановка чужого — это ровно то, чем пользуется атака.
    ///
    /// Мутация 2026-09-07: обработчик успеха дописывает номер в тело → покраснел этот факт.
    /// </remarks>
    [Fact]
    public async Task Тело_успешного_ответа_не_несёт_номера_обращения()
    {
        const string Номер = "breach-probe-0123456789";

        host.User.SignIn();

        var успешных = 0;

        foreach (var адрес in (string[])["/api/health", "/api/ready", "/api/me", "/api/directory"])
        {
            var запрос = new HttpRequestMessage(HttpMethod.Get, адрес);

            запрос.Headers.TryAddWithoutValidation(RequestIdMiddleware.HeaderName, Номер);

            var ответ = await host.CreateClient().SendAsync(запрос, Ct);

            if (!ответ.IsSuccessStatusCode)
            {
                continue;
            }

            // ⚠ Сначала убеждаемся, что номер ВООБЩЕ доехал: иначе проверка «его нет в теле»
            // зеленела бы оттого, что сервер присланный номер выбросил, и о самом свойстве не
            // говорила бы ничего.
            ответ.Headers.GetValues(RequestIdMiddleware.HeaderName).Single().ShouldBe(Номер);

            var тело = await ответ.Content.ReadAsStringAsync(Ct);

            тело.Contains(Номер, StringComparison.Ordinal).ShouldBeFalse(
                $"{адрес}: тело успешного ответа отражает номер обращения. Вместе со сжатием "
                + "(этап 60.1) отражённое значение — это половина условий BREACH");

            успешных++;
        }

        // ⚠ ⚠ Сторож сторожа. Ответь все четыре адреса отказом — цикл прошёл бы насквозь и
        // факт зеленел бы, не проверив ни одного тела. Ровно тот класс, из-за которого
        // проверка, испытанная только успехом, доказывает не то, что кажется (правило 9).
        успешных.ShouldBeGreaterThanOrEqualTo(
            2, "ни один из адресов не ответил успехом — проверено ноль тел");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Те же два слоя в том же порядке, что в `Program.cs`, и падающий обработчик.
    /// </summary>
    /// <remarks>
    /// `HostBuilder` + `UseTestServer`, а не `WebHostBuilder`: последний в .NET 10 объявлен
    /// устаревшим вместе с принимающим его конструктором `TestServer`.
    /// </remarks>
    private static async Task<IHost> PipelineAsync(RequestDelegate terminal) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddLogging())
                .Configure(app =>
                {
                    app.UseMiddleware<RequestIdMiddleware>();
                    app.UseMiddleware<ErrorHandlingMiddleware>();
                    app.Run(terminal);
                }))
            .StartAsync(Ct);
}
