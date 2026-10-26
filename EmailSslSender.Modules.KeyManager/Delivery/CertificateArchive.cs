using System.IO.Compression;
using Modules.Core.Contracts.Mail;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>
/// Файлы сертификата одним архивом ZIP — единственным вложением письма.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Почему архив</b> (замечание владельца 2026-10-26). Outlook блокирует вложения
/// <c>.crt</c>, <c>.cer</c>, <c>.der</c> как опасные: получатель видел письмо, а сертификат открыть
/// не мог. ZIP Outlook пропускает, а внутри файлы сохраняют свои имена и расширения.
///
/// ⚠ Архив без пароля: шифрование ZIP из BCL — только слабое ZipCrypto, а оно создаёт видимость
/// защиты, которой нет. Закрытый ключ защищает его собственный пароль (если он есть) и сам канал
/// почты; решение «слать ли ключ» принимает администратор в подписке.
/// </remarks>
internal static class CertificateArchive
{
    /// <summary>Тип содержимого вложения.</summary>
    public const string ContentType = "application/zip";

    /// <summary>Сложить файлы в архив <paramref name="fileName"/>.</summary>
    /// <param name="fileName">Имя архива у получателя, например <c>site.example.com.zip</c>.</param>
    /// <param name="files">Файлы в том порядке, в каком их выбрали в подписке.</param>
    /// <param name="when">Время изменения файлов внутри архива.</param>
    public static MailAttachment Pack(string fileName, IReadOnlyList<MailAttachment> files, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(files);

        using var поток = new MemoryStream();

        using (var архив = new ZipArchive(поток, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var файл in files)
            {
                var запись = архив.CreateEntry(файл.FileName, CompressionLevel.Optimal);
                запись.LastWriteTime = when;

                using var вход = запись.Open();
                вход.Write(файл.Content);
            }
        }

        return new MailAttachment(fileName, ContentType, поток.ToArray());
    }
}
