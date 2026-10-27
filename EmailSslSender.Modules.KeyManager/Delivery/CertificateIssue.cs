using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>
/// Когда выпущен сертификат: <c>NotBefore</c> из файла, выгруженного из Key Manager Plus.
/// </summary>
/// <remarks>
/// ⚠ Защита от отката (ревью надёжности 2026-10-27) сравнивает выпуски по дате выпуска, а не по сроку:
/// сертификат, продлённый досрочно под новые предельные сроки, истекает РАНЬШЕ прежнего, но выпущен позже.
/// Список Key Manager Plus даты выпуска не отдаёт — она берётся из самого сертификата.
/// </remarks>
internal static partial class CertificateIssue
{
    /// <summary>
    /// Дата выпуска сертификата с этим серийным номером из файла PEM (цепочка — ищется нужный) или DER.
    /// <c>null</c> — разобрать не удалось или в файле нет сертификата с этим номером.
    /// </summary>
    /// <remarks>
    /// Файл с одним сертификатом принимается и без совпадения номера: Key Manager Plus не обещает писать
    /// серийный номер так же, как он записан в сертификате. В цепочке без совпадения — <c>null</c>: какой из
    /// сертификатов «тот», не угадать.
    /// </remarks>
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
