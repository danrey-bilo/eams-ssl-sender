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

/// <summary>Конверт ошибок: настоящий хост проверяет порядок слоёв, мини-конвейер — обработчик исключений.</summary>
[Collection(SenderHost.Collection)]
public sealed class ErrorEnvelope(SenderHost host)
{
    [Fact]
    public async Task Здоровье_отвечает_без_базы()
    {
        // Иначе оркестратор перезапускал бы здоровое приложение при недоступной базе.
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
        // Номер мог присвоить туннель или прокси: свой разорвал бы цепочку.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add(RequestIdMiddleware.HeaderName, "outside-123");

        var response = await host.CreateClient().SendAsync(request, Ct);

        response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single().ShouldBe("outside-123");
    }

    /// <summary>⚠ Kestrel роняет управляющий знак в заголовке ответа — номер заменяется своим, а не отражается.</summary>
    [Fact]
    public async Task Номер_обращения_с_управляющим_знаком_не_роняет_запрос()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");

        // `TryAddWithoutValidation`: обычный `Add` не пропустит значение, а проверяется сервер.
        request.Headers.TryAddWithoutValidation(RequestIdMiddleware.HeaderName, "плохой\u0001номер");

        var response = await host.CreateClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, "управляющий знак в номере обращения уронил запрос");

        response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single()
            .ShouldNotContain("\u0001", Case.Sensitive, "управляющий знак уехал в ответ");
    }

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
        // `details` — машинное поле без секретов, уезжает целиком.
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
        // ⚠ Текст исключения может нести строку подключения с паролем — наружу только номер обращения.
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
    [Fact]
    public async Task Отказ_платформы_выходит_своим_статусом()
    {
        using var server = await PipelineAsync(_ => throw new BadHttpRequestException("тело велико", 413));

        var response = await server.GetTestClient().GetAsync("/", Ct);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        body!.Code.ShouldBe(ErrorCodes.PayloadTooLarge);
    }

    /// <summary>Вне <c>/api</c> пустой отказ не трогается.</summary>
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
        // Иначе поддержка получит один номер, а в логе будет другой.
        using var server = await PipelineAsync(_ => throw AppException.NotFound());

        var response = await server.GetTestClient().GetAsync("/", Ct);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(Ct);

        body!.RequestId.ShouldBe(response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public void Конверт_сериализуется_в_snake_case()
    {
        // `requestId` вместо `request_id` сломал бы разбор у клиента молча.
        var json = JsonSerializer.Serialize(
            new ErrorResponse(ErrorCodes.NotFound, "Не найдено", null, "abc"), Web);

        json.ShouldContain("\"request_id\":\"abc\"");
        json.ShouldContain("\"code\":\"NOT_FOUND\"");
        json.ShouldNotContain("requestId");
    }

    /// <summary>⚠ Против BREACH при сжатии: секрет бывает только в успешном теле, отражённого значения там быть не должно.</summary>
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

            // Номер обязан доехать: иначе «его нет в теле» ничего не доказывает.
            ответ.Headers.GetValues(RequestIdMiddleware.HeaderName).Single().ShouldBe(Номер);

            var тело = await ответ.Content.ReadAsStringAsync(Ct);

            тело.Contains(Номер, StringComparison.Ordinal).ShouldBeFalse(
                $"{адрес}: тело успешного ответа отражает номер обращения. Вместе со сжатием "
                + "(этап 60.1) отражённое значение — это половина условий BREACH");

            успешных++;
        }

        // Иначе при отказе всех адресов факт зеленел бы, не проверив ни одного тела.
        успешных.ShouldBeGreaterThanOrEqualTo(
            2, "ни один из адресов не ответил успехом — проверено ноль тел");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Те же два слоя в том же порядке, что в <c>Program.cs</c>, и падающий обработчик.</summary>
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
