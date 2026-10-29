using Microsoft.AspNetCore.Mvc.Testing;

namespace EmailSslSender.IntegrationTests;

/// <summary>Заголовки защиты: на API, на панели, HSTS только по HTTPS.</summary>
[Collection(SenderHost.Collection)]
public sealed class SecurityHeadersTests(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ответ_API_не_кэшируется_и_несёт_заголовки_защиты()
    {
        var ответ = await host.CreateClient().GetAsync("/api/health", Ct);

        ответ.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        ответ.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        ответ.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        ответ.Headers.GetValues("Permissions-Policy").Single().ShouldContain("camera=()");
        ответ.Headers.GetValues("Cross-Origin-Opener-Policy").Single().ShouldBe("same-origin");
    }

    [Fact]
    public async Task Ответ_с_ошибкой_тоже_несёт_заголовки_защиты()
    {
        // ⚠ Личность общая на коллекцию — без выхода запрос пришёл бы от соседа.
        host.User.SignOut();
        var ответ = await host.CreateClient().GetAsync("/api/me", Ct);

        ответ.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        ответ.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        ответ.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
    }

    [Fact]
    public async Task HSTS_только_на_ответ_по_HTTPS()
    {
        var поHttp = await host.CreateClient().GetAsync("/api/health", Ct);
        поHttp.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();

        var клиент = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var поHttps = await клиент.GetAsync("/api/health", Ct);

        поHttps.Headers.GetValues("Strict-Transport-Security").Single().ShouldStartWith("max-age=31536000");
    }
}
