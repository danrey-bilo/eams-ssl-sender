using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Дата выпуска (<c>NotBefore</c>) из файла сертификата: список Key Manager Plus её не отдаёт.</summary>
/// <remarks>⚠ Откат определяется по выпуску, не по сроку: досрочно продлённый истекает раньше прежнего.</remarks>
internal static partial class CertificateIssue
{
    /// <summary><c>NotBefore</c> сертификата с этим серийным из PEM или DER; единственный в файле — без сверки номера.</summary>
    public static DateTimeOffset? NotBefore(byte[] file, string serialNumber)
    {
        ArgumentNullException.ThrowIfNull(file);

        var выпуски = new List<(string Serial, DateTimeOffset NotBefore)>();

        foreach (var der in Сертификаты(file))
        {
            try
            {
                using var сертификат = X509CertificateLoader.LoadCertificate(der);
                выпуски.Add((Серийный(сертификат.SerialNumber), new DateTimeOffset(сертификат.NotBefore.ToUniversalTime(), TimeSpan.Zero)));
            }
            catch (CryptographicException)
            {
                // Не сертификат (ключ в PEM, мусор) — следующий блок.
            }
        }

        var номер = Серийный(serialNumber);
        var свой = выпуски.Where(в => в.Serial == номер).Select(в => (DateTimeOffset?)в.NotBefore).FirstOrDefault();

        return свой ?? (выпуски.Count == 1 ? выпуски[0].NotBefore : null);
    }

    /// <summary>Дата выпуска для хранения в подписке: ISO 8601 с поясом.</summary>
    public static string Format(DateTimeOffset notBefore) => notBefore.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Дата выпуска из подписки; негодная строка — <c>null</c>.</summary>
    public static DateTimeOffset? Parse(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var когда) ? когда : null;

    /// <summary>Серийный номер в одном виде: только hex, без разделителей и ведущих нулей, строчными.</summary>
    internal static string Серийный(string serial) =>
        string.Concat(serial.Where(Uri.IsHexDigit)).TrimStart('0').ToLowerInvariant();

    private static IEnumerable<byte[]> Сертификаты(byte[] file)
    {
        var текст = Encoding.ASCII.GetString(file);
        var блоки = БлокPem().Matches(текст);

        if (блоки.Count == 0)
        {
            // Не PEM — пробуем как один сертификат в DER.
            yield return file;
            yield break;
        }

        foreach (Match блок in блоки)
        {
            byte[]? der;

            try
            {
                der = Convert.FromBase64String(string.Concat(блок.Groups[1].Value.Where(с => !char.IsWhiteSpace(с))));
            }
            catch (FormatException)
            {
                der = null;
            }

            if (der is not null)
            {
                yield return der;
            }
        }
    }

    [GeneratedRegex("-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----", RegexOptions.Singleline)]
    private static partial Regex БлокPem();
}
