using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EmailSslSender.Modules.KeyManager.Client;

/// <summary>
/// Проверка сертификата сервера Key Manager Plus: обычная цепочка или закреплённый отпечаток.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Отключить проверку нельзя ничем.</b> В примерах документации Key Manager Plus у каждого
/// <c>curl</c> стоит <c>-k</c>, и соблазн повторить это велик. Цена — ключ API (а через него все
/// закрытые ключи установки) у любого, кто встал посередине. Самоподписанный сертификат
/// принимается только закреплённым отпечатком: администратор сверяет его один раз сам.
///
/// ⚠ ⚠ <b>Отпечаток СУЖАЕТ, а не расширяет.</b> Заданный отпечаток принимает ровно закреплённый
/// лист — и только его: ни цепочка, ни имя хоста тогда не важны, но и сертификат, годный для
/// удостоверяющего центра, с другим отпечатком отвергается. Прежде годный по цепочке
/// принимался раньше сверки, и закрепление ничего не закрепляло: любой, кто получил на имя
/// сервера сертификат публичного центра, проходил мимо отпечатка. Перевыпуск сертификата
/// сервера потребует обновить отпечаток — опрос скажет об этом ошибкой TLS.
///
/// ⚠ Без отпечатка — обычная проверка: цепочка, имя хоста и отзыв (режим отзыва ставит
/// <c>KeyManagerConnector</c>).
/// </remarks>
internal static class ServerCertificatePin
{
    /// <summary>Принять ли сертификат сервера.</summary>
    /// <remarks>
    /// мутация 2026-10-20: вернул «годный по цепочке — принять» до сверки отпечатка → покраснел
    /// PinTests.Годный_по_цепочке_но_чужой_при_закреплённом_отпечатке_отвергается.
    /// </remarks>
    public static bool Accept(X509Certificate? certificate, SslPolicyErrors errors, string? pinnedSha256)
    {
        var закреплённый = Normalize(pinnedSha256);

        if (закреплённый is null)
        {
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null)
        {
            return false;
        }

        var отпечаток = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

        return string.Equals(отпечаток, закреплённый, StringComparison.Ordinal);
    }

    /// <summary>
    /// Отпечаток в виде сплошного верхнего hex или <c>null</c>, если не задан или не похож на SHA-256.
    /// </summary>
    /// <remarks>
    /// ⚠ Двоеточия и пробелы разрешены: так отпечаток показывают браузер и <c>openssl x509
    /// -fingerprint -sha256</c>, и требовать ручной чистки — значит получить опечатку.
    /// </remarks>
    public static string? Normalize(string? pinnedSha256)
    {
        if (string.IsNullOrWhiteSpace(pinnedSha256))
        {
            return null;
        }

        var hex = new string([.. pinnedSha256.Where(Uri.IsHexDigit)]).ToUpperInvariant();
        var лишнее = pinnedSha256.Any(с => !Uri.IsHexDigit(с) && с is not (':' or ' ' or '-'));

        return hex.Length == SHA256.HashSizeInBytes * 2 && !лишнее ? hex : null;
    }
}
