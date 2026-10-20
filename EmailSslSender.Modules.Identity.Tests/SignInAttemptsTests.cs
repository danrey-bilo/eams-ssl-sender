using System.Net;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Предел попыток входа — на пару «адрес + почта», а не на адрес (решение 0096).
/// </summary>
/// <remarks>
/// Что предел стоит в самом входе, держит <c>IntegrationTests/AuthApi.Попытки_одной_почты_режутся_а_соседи_по_адресу_входят</c>;
/// здесь — как он считает.
/// </remarks>
public sealed class SignInAttemptsTests
{
    private static readonly IPAddress Офис = IPAddress.Parse("203.0.113.7");

    // мутация 2026-10-19: ключ без почты (только адрес) → покраснел этот факт (сосед отвергнут).
    [Fact]
    public void Почта_считается_отдельно_а_сосед_по_адресу_проходит()
    {
        using var попытки = new SignInAttempts(new IdentityModuleOptions { SignInAttemptsPerMinute = 2 });

        попытки.Require(Офис, "anna@example.com");
        попытки.Require(Офис, "  ANNA@example.com ");

        var отказ = Should.Throw<AppException>(() => попытки.Require(Офис, "Anna@Example.com"));

        отказ.Code.ShouldBe(ErrorCodes.RateLimited);
        отказ.Status.ShouldBe(HttpStatusCode.TooManyRequests);

        Should.NotThrow(() => попытки.Require(Офис, "boris@example.com"), "сосед по адресу заперт чужим перебором");
        Should.NotThrow(() => попытки.Require(IPAddress.Parse("198.51.100.1"), "anna@example.com"), "другой адрес заперт");
    }

    /// <summary>Адреса одной IPv6-сети /64 делят один предел.</summary>
    /// <remarks>
    /// ⚠ Своя /64 у абонента — 2⁶⁴ адресов: без свёртки перебирающий брал новый адрес на каждую
    /// попытку, и предел на пару «адрес + почта» не ограничивал ничего.
    /// </remarks>
    [Fact]
    public void Адреса_одной_сети_64_делят_предел()
    {
        using var попытки = new SignInAttempts(new IdentityModuleOptions { SignInAttemptsPerMinute = 2 });

        попытки.Require(IPAddress.Parse("2001:db8:5:6::1"), "anna@example.com");
        попытки.Require(IPAddress.Parse("2001:db8:5:6::2"), "anna@example.com");

        Should.Throw<AppException>(() => попытки.Require(IPAddress.Parse("2001:db8:5:6:ffff::3"), "anna@example.com"))
            .Code.ShouldBe(ErrorCodes.RateLimited, "смена адреса внутри своей /64 обошла предел");

        Should.NotThrow(
            () => попытки.Require(IPAddress.Parse("2001:db8:5:7::1"), "anna@example.com"), "соседняя /64 заперта чужим перебором");
    }
}
