using System.Net;
using System.Net.Sockets;

namespace EmailSslSender.Shared.Api;

/// <summary>
/// Ключ адреса клиента для пределов частоты.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>IPv6 считается по сети /64, а не по адресу.</b> Провайдер выдаёт абоненту целую /64
/// (обычно и больше), и каждый адрес в ней — его собственный: 2⁶⁴ «разных клиентов» у одного
/// человека. Предел «на адрес» для IPv6 без свёртки не ограничивал ничего — перебирающий брал
/// новый адрес на каждую попытку. Свёртка до /64 даёт ему столько же ключей, сколько у
/// IPv4-абонента: один.
///
/// ⚠ IPv4, завёрнутый в IPv6 (<c>::ffff:a.b.c.d</c>), разворачивается обратно в IPv4: иначе
/// один и тот же клиент за двухстековым прокси считался бы по двум ключам, а свёртка до /64
/// слила бы весь IPv4-интернет в один ключ.
///
/// Один на хост и модули: пределы хоста (<c>Program.cs</c>) и предел входа модуля личностей
/// обязаны считать адрес одинаково, иначе один из них обходится тем, от чего защищён другой.
/// </remarks>
public static class RateLimitAddress
{
    /// <summary>Сколько старших бит IPv6 составляют ключ.</summary>
    public const int Ipv6PrefixLength = 64;

    /// <summary>Ключ адреса. <c>null</c> — адреса нет, ключ выбирает вызывающий.</summary>
    public static string? Key(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> байты = stackalloc byte[16];
        address.TryWriteBytes(байты, out _);

        // Младшие 64 бита — адрес внутри сети абонента; обнуляем их. Зона (scope id) не входит:
        // она про интерфейс сервера, а не про клиента.
        байты[(Ipv6PrefixLength / 8)..].Clear();

        return $"{new IPAddress(байты)}/{Ipv6PrefixLength}";
    }
}
