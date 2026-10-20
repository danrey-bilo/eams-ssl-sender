namespace EmailSslSender.Modules.KeyManager.Client;

/// <summary>
/// Типы файлов, которые отдаёт <c>getCertificate</c> Key Manager Plus (параметр <c>fileType</c>).
/// </summary>
/// <remarks>
/// ⚠ Список закрыт и взят из документации REST API дословно. Что именно лежит в <c>PKCS</c> и
/// <c>KEY</c>, документация не говорит; по смыслу это PKCS#12 и закрытый ключ.
/// </remarks>
internal static class CertificateFormats
{
    public const string Cer = "CER";
    public const string Crt = "CRT";
    public const string Der = "DER";
    public const string P7b = "P7B";
    public const string Pkcs = "PKCS";
    public const string Jks = "JKS";
    public const string Pem = "PEM";
    public const string Key = "KEY";

    /// <summary>Все допустимые, в порядке документации.</summary>
    public static readonly IReadOnlyList<string> All = [Cer, Crt, Der, P7b, Pkcs, Jks, Pem, Key];

    /// <summary>Что кладём в подписку, если администратор не выбрал: сертификат и ключ.</summary>
    public static readonly IReadOnlyList<string> Default = [Pem, Key];

    /// <summary>Привести к виду из справочника или <c>null</c>, если такого типа нет.</summary>
    public static string? Normalize(string? format)
    {
        var тип = format?.Trim().ToUpperInvariant();

        return тип is not null && All.Contains(тип, StringComparer.Ordinal) ? тип : null;
    }

    /// <summary>Расширение файла для типа.</summary>
    public static string Extension(string format) => format switch
    {
        Cer => "cer",
        Crt => "crt",
        Der => "der",
        P7b => "p7b",
        Pkcs => "pfx",
        Jks => "jks",
        Pem => "pem",
        Key => "key",
        _ => "bin",
    };

    /// <summary>Несёт ли тип закрытый ключ сам по себе (PEM — смотря что внутри).</summary>
    public static bool CarriesPrivateKey(string format) => format is Key or Pkcs or Jks;

    /// <summary>
    /// Тип содержимого вложения.
    /// </summary>
    /// <remarks>
    /// ⚠ Свой, а не из ответа Key Manager Plus: сервер может назвать что угодно
    /// <c>application/octet-stream</c> или <c>text/plain</c>, а почтовому клиенту получателя тип
    /// подсказывает, чем файл открыть.
    /// </remarks>
    public static string ContentType(string format) => format switch
    {
        Cer or Crt or Der => "application/pkix-cert",
        P7b => "application/x-pkcs7-certificates",
        Pkcs => "application/x-pkcs12",
        Jks => "application/x-java-keystore",
        Pem or Key => "application/x-pem-file",
        _ => "application/octet-stream",
    };
}
