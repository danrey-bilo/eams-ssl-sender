using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Каждый маршрут вне MVC объявлен публичным — осознанно, а не по забывчивости.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Маршруты вне MVC проходят мимо фильтра входа, и молча.</b>
/// <c>AuthenticationFilter</c> — фильтр MVC: он видит действия контроллеров и не видит ничего,
/// отображённого через <c>MapGet</c>. Такой маршрут открыт всем, и единственный способ это
/// заметить — потребовать, чтобы открытость была объявлена пометкой
/// <see cref="PublicEndpointAttribute"/> с причиной. Нужен закрытый маршрут вне MVC — его
/// закрывают сами (<c>RequireAuthorization</c> не подойдёт: схемы ASP.NET здесь нет), и этот
/// сторож покраснеет, напоминая об этом.
///
/// ⚠ Проверка живёт здесь, а не в отдельном арх-тесте: <c>EndpointDataSource</c> существует только
/// у <b>поднятого</b> приложения.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class МаршрутыВнеMVC(SenderHost host)
{
    [Fact]
    public void Каждый_маршрут_вне_MVC_объявлен_публичным()
    {
        var непомеченные = ВнеMvc()
            .Where(маршрут => маршрут.Metadata.GetMetadata<PublicEndpointAttribute>() is null)
            .Select(маршрут => маршрут.DisplayName ?? "без имени")
            .ToList();

        непомеченные.ShouldBeEmpty(
            "маршрут вне MVC не виден фильтру входа и открыт всем. Непомеченные: "
            + string.Join(" | ", непомеченные));
    }

    /// <summary>
    /// Сторож действительно видит маршруты вне MVC — и знает их поимённо.
    /// </summary>
    /// <remarks>
    /// ⚠ Без этого пустой список маршрутов сошёлся бы с пустым списком непомеченных, и
    /// проверка зеленела бы вечно.
    /// </remarks>
    [Fact]
    public void Сторож_видит_маршруты_вне_MVC()
    {
        var шаблоны = ВнеMvc().OfType<RouteEndpoint>()
            .Select(маршрут => маршрут.RoutePattern.RawText ?? string.Empty)
            .ToList();

        шаблоны.ShouldContain("/api/health");
        шаблоны.ShouldContain("/api/ready");
        шаблоны.ShouldContain("/activate");
        шаблоны.ShouldContain("/password-reset");
        шаблоны.ShouldContain("/");
        шаблоны.ShouldContain("/assets/{file}");
    }

    private IReadOnlyList<Endpoint> ВнеMvc() =>
        [.. host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(маршрут => маршрут.Metadata.GetMetadata<ControllerActionDescriptor>() is null)];
}
