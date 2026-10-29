namespace EmailSslSender.Modules.KeyManager.Client;

/// <summary>Типы файлов <c>getCertificate</c> (параметр <c>fileType</c>), дословно из документации KMP.</summary>
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

    public static readonly IReadOnlyList<string> All = [Cer, Crt, Der, P7b, Pkcs, Jks, Pem, Key];

    /// <summary>Форматы подписки по умолчанию.</summary>
    public static readonly IReadOnlyList<string> Default = [Pem, Key];

    /// <summary>Тип из справочника или <c>null</c>.</summary>
    public static string? Normalize(string? format)
    {
        var тип = format?.Trim().ToUpperInvariant();

        return тип is not null && All.Contains(тип, StringComparer.Ordinal) ? тип : null;
    }

    /// <summary>Безопасное имя файла из Common Name: <c>*.example.com</c> → <c>wildcard.example.com</c>.</summary>
    public static string FileBase(string commonName)
    {
        ArgumentNullException.ThrowIfNull(commonName);

        var основа = commonName.Replace("*", "wildcard", StringComparison.Ordinal);
        var чистое = new string([.. основа.Select(с => char.IsLetterOrDigit(с) || с is '.' or '-' or '_' ? с : '_')]);

        return чистое.Length == 0 ? "certificate" : чистое;
    }

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

    /// <summary>Тип содержимого вложения — свой: ответ KMP бывает <c>application/octet-stream</c> на всё.</summary>
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
