using System.Net;
using System.Text.Json.Nodes;
using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// DEFAULT DENY живым запросом: всё, что не объявлено публичным, закрыто для невошедшего.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Самое важное свойство конвейера, и проверяется оно на КАЖДОМ действии</b>, а не на
/// выборке. Забытая пометка <see cref="PublicEndpointAttribute"/> даёт 401, забытое объявление доступа —
/// 403; обратная
/// ошибка (фильтр перестал работать, действие открылось) не краснеет больше нигде.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class ЗакрытоПоУмолчанию(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Действие_без_пометки_закрыто_для_невошедшего()
    {
        host.User.SignOut();

        var ответ = await host.CreateClient().GetAsync("/api/test/unguarded", Ct);

        // ⚠ Действие без объявления доступа закрыто для всех, вошедших тоже: это ошибка разработчика,
        // и фильтр прав модуля хостинга отвечает отказом в праве, а не просьбой войти.
        ответ.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var тело = JsonNode.Parse(await ответ.Content.ReadAsStringAsync(Ct))!;
        тело["code"]!.GetValue<string>().ShouldBe("PERMISSION_DENIED");
    }

    [Fact]
    public async Task Явно_открытое_отвечает()
    {
        host.User.SignOut();

        (await host.CreateClient().GetAsync("/api/test/public", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Внутренний контроллер чужой сборки маршрутизацией не находится вовсе.</summary>
    [Fact]
    public async Task Внутренний_контроллер_чужой_сборки_не_становится_маршрутом()
    {
        host.User.SignOut();

        (await host.CreateClient().GetAsync("/api/test/foreign", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Каждое действие приложения без пометки отвечает невошедшему 401.
    /// </summary>
    /// <remarks>
    /// ⚠ Параметры маршрута подставляются случайным <c>Guid</c>: до разбора ресурса дело
    /// доходить не должно — отказ обязан случиться раньше, в фильтре.
    /// </remarks>
    [Fact]
    public async Task Каждое_действие_без_пометки_отвечает_невошедшему_401()
    {
        host.User.SignOut();

        var действия = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(маршрут => маршрут.Metadata.GetMetadata<ControllerActionDescriptor>() is { } описание
                && описание.ControllerTypeInfo.Assembly != typeof(SenderHost).Assembly)
            .Where(маршрут => маршрут.Metadata.GetMetadata<PublicEndpointAttribute>() is null)
            .ToList();

        действия.Count.ShouldBeGreaterThan(20, "действий не видно — проверка смотрит в пустоту");

        var открытые = new List<string>();
        var клиент = host.CreateClient();

        foreach (var действие in действия)
        {
            var метод = действие.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var путь = "/" + System.Text.RegularExpressions.Regex.Replace(
                действие.RoutePattern.RawText ?? string.Empty, @"\{[^}]+\}", Guid.CreateVersion7().ToString());

            using var запрос = new HttpRequestMessage(new HttpMethod(метод), путь)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };

            var ответ = await клиент.SendAsync(запрос, Ct);

            if (ответ.StatusCode != HttpStatusCode.Unauthorized)
            {
                открытые.Add($"{метод} {путь} → {(int)ответ.StatusCode}");
            }
        }

        открытые.ShouldBeEmpty("действие без пометки ответило невошедшему не 401: " + string.Join(" | ", открытые));
    }
}
