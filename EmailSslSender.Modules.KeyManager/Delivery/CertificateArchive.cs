using System.IO.Compression;
using EmailSslSender.Modules.KeyManager.Client;
using Modules.Core.Contracts.Mail;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Файлы сертификатов одним ZIP — единственным вложением письма.</summary>
/// <remarks>⚠ ZIP — потому что Outlook блокирует .crt, .cer, .der; без пароля — ZipCrypto дал бы лишь видимость защиты.</remarks>
internal static class CertificateArchive
{
    public const string ContentType = "application/zip";

    /// <summary>Имя архива с несколькими сертификатами: внутри папка на каждый.</summary>
    public const string Several = "ssl-certificates.zip";

    /// <summary>Один сертификат — файлы в корне архива с его именем, несколько — по папкам.</summary>
    /// <param name="certificates">Сертификаты и их файлы в порядке подписки.</param>
    /// <param name="when">Время изменения файлов в архиве.</param>
    public static MailAttachment Pack(IReadOnlyList<CertificateFiles> certificates, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentOutOfRangeException.ThrowIfZero(certificates.Count);

        if (certificates is [var один])
        {
            return Pack(CertificateFormats.FileBase(один.Certificate.CommonName) + ".zip", [.. один.Files.Select(ф => ("", ф))], when);
        }

        // ⚠ Совпавшие имена папок (`*.a` и `_.a`) различаются номером, а не затирают друг друга.
        var папки = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var записи = new List<(string, MailAttachment)>();

        foreach (var сертификат in certificates)
        {
            var основа = CertificateFormats.FileBase(сертификат.Certificate.CommonName);
            var папка = основа;

            for (var номер = 2; !папки.Add(папка); номер++)
            {
                папка = $"{основа}-{номер}";
            }

            записи.AddRange(сертификат.Files.Select(ф => (папка + "/", ф)));
        }

        return Pack(Several, записи, when);
    }

    private static MailAttachment Pack(string fileName, IReadOnlyList<(string Folder, MailAttachment File)> files, DateTimeOffset when)
    {
        using var поток = new MemoryStream();

        using (var архив = new ZipArchive(поток, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (папка, файл) in files)
            {
                var запись = архив.CreateEntry(папка + файл.FileName, CompressionLevel.Optimal);
                запись.LastWriteTime = when;

                using var вход = запись.Open();
                вход.Write(файл.Content);
            }
        }

        return new MailAttachment(fileName, ContentType, поток.ToArray());
    }
}
