using System.Net;
using EmailSslSender.Shared.Api;

namespace EmailSslSender.Shared.Tests;

/// <summary>
/// Ключ адреса для пределов частоты: IPv4 — адрес, IPv6 — сеть /64.
/// </summary>
/// <remarks>
/// ⚠ Без свёртки IPv6-абонент получал новый ключ на каждую попытку: своя /64 у него — это 2⁶⁴
/// адресов, и предел «на адрес» не ограничивал ничего.
/// </remarks>
public sealed class RateLimitAddressTests
{
    [Fact]
    public void Адреса_одной_сети_64_дают_один_ключ()
    {
        var первый = RateLimitAddress.Key(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        var второй = RateLimitAddress.Key(IPAddress.Parse("2001:db8:1:2::1"));

        первый.ShouldBe(второй, "абонент обходит предел сменой адреса внутри своей /64");
        первый.ShouldBe("2001:db8:1:2::/64");
    }

    [Fact]
    public void Соседняя_сеть_64_считается_отдельно()
    {
        RateLimitAddress.Key(IPAddress.Parse("2001:db8:1:2::1"))
            .ShouldNotBe(RateLimitAddress.Key(IPAddress.Parse("2001:db8:1:3::1")), "чужой абонент заперт чужим перебором");
    }

    [Fact]
    public void IPv4_считается_по_адресу_в_том_числе_завёрнутый_в_IPv6()
    {
        RateLimitAddress.Key(IPAddress.Parse("203.0.113.7")).ShouldBe("203.0.113.7");
        RateLimitAddress.Key(IPAddress.Parse("::ffff:203.0.113.7")).ShouldBe(
            "203.0.113.7", "свёртка до /64 слила бы весь IPv4 в один ключ");
        RateLimitAddress.Key(IPAddress.Parse("203.0.113.8")).ShouldNotBe("203.0.113.7");
    }

    [Fact]
    public void Без_адреса_ключа_нет()
    {
        RateLimitAddress.Key(null).ShouldBeNull();
    }
}
