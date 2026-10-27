using System.IO.Compression;
using EmailSslSender.Modules.KeyManager.Client;
using Modules.Core.Contracts.Mail;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>
/// Файлы сертификатов одним архивом ZIP — единственным вложением письма.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Почему архив</b> (замечание владельца 2026-10-26). Outlook блокирует вложения
/// <c>.crt</c>, <c>.cer</c>, <c>.der</c> как опасные: получатель видел письмо, а сертификат открыть
/// не мог. ZIP Outlook пропускает, а внутри файлы сохраняют свои имена и расширения.
///
/// ⚠ <b>Один архив на письмо</b> (решение владельца 2026-10-27): один сертификат — файлы в корне и архив
/// по его имени, как прежде (<c>site.example.com.zip</c>); несколько — <see cref="Several"/>, внутри папка
/// на каждый сертификат: у разных сертификатов файлы называются одинаково (<c>passphrase.txt</c>).
///
/// ⚠ Архив без пароля: шифрование ZIP из BCL — только слабое ZipCrypto, а оно создаёт видимость
/// защиты, которой нет. Закрытый ключ защищает его собственный пароль (если он есть) и сам канал
/// почты; решение «слать ли ключ» принимает администратор в подписке.
/// </remarks>
internal static class CertificateArchive
{
    /// <summary>Тип содержимого вложения.</summary>
    public const string ContentType = "application/zip";

    /// <summary>Имя архива с несколькими сертификатами.</summary>
    public const string Several = "ssl-certificates.zip";

    /// <summary>Сложить файлы сертификатов в один архив.</summary>
    /// <param name="certificates">Сертификаты и их файлы — в порядке подписки.</param>
    /// <param name="when">Время изменения файлов внутри архива.</param>
    public static MailAttachment Pack(IReadOnlyList<CertificateFiles> certificates, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentOutOfRangeException.ThrowIfZero(certificates.Count);

        if (certificates is [var один])
        {
            return Pack(CertificateFormats.FileBase(один.Certificate.CommonName) + ".zip", [.. один.Files.Select(ф => ("", ф))], when);
        }

        // ⚠ Папка — по имени файла сертификата; два Common Name с одним именем файла (`*.a` и `_.a`)
        // различаются номером, а не затирают друг друга.
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
