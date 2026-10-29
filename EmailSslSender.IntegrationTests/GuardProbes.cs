using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Mvc;

namespace EmailSslSender.IntegrationTests;

/// <summary>Зонд: действие без объявленного доступа обязано быть закрыто.</summary>
[ApiController]
[Route("api/test")]
public sealed class GuardProbeController : ControllerBase
{
    /// <summary>Доступ не объявлен нарочно — ждём отказ.</summary>
    [HttpGet("unguarded")]
    public string Unguarded() => "сюда попадать нельзя";

    /// <summary>Открыт явно — ждём ответ.</summary>
    [HttpGet("public")]
    [PublicEndpoint("зонд: проверяет, что явная открытость работает")]
    public string Open() => "открыто намеренно";
}

/// <summary>Внутренний контроллер чужой сборки: ждём 404.</summary>
/// <remarks>⚠ <c>ModuleControllers</c> поднимает внутренние контроллеры только своей сборки.</remarks>
[ApiController]
[Route("api/test")]
internal sealed class ForeignInternalController : ControllerBase
{
    [HttpGet("foreign")]
    [PublicEndpoint("зонд: даже открытым он не должен быть найден маршрутизацией")]
    public string Foreign() => "этого маршрута быть не должно";
}
