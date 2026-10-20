using System.Text.Json;
using System.Text.Json.Serialization;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Crypto;
using MimeKit;

namespace EmailSslSender.Modules.Mail.Sending;

/// <summary>
/// Вложения письма в очереди: запечатать при постановке, открыть при отправке.
/// </summary>
/// <remarks>
/// ⚠ <b>Тем же ключом данных, что тело, но СВОИМ контекстом.</b> Ключ общий — значит, ротация
/// мастер-ключа перекладывает одну обёртку и вложений не касается. Контекст свой — значит,
/// запечатку вложений нельзя подставить на место тела и открыть как текст письма.
///
/// ⚠ Все вложения письма — одно значение одной колонки: JSON-массив, байты в base64. Файлы
/// сертификатов весят килобайты, и таблица под каждое вложение была бы сложностью без пользы.
/// </remarks>
internal static class MailAttachments
{
    /// <summary>Контекст AEAD вложений.</summary>
    private const string Контекст = "sender:mail-attachments";

    /// <summary>Тип, если присланный не разбирается.</summary>
    internal const string ЗапасныйТип = "application/octet-stream";

    /// <summary>
    /// Срок следующей попытки у письма, чьи вложения стёрты, — «никогда».
    /// </summary>
    /// <remarks>
    /// ⚠ Без него поднятый потом <c>mail.max_attempts</c> вернул бы мёртвое письмо в работу, и оно
    /// ушло бы <b>без файлов</b> — сертификат, который нельзя установить, под видом доставленного.
    /// </remarks>
    internal static readonly DateTimeOffset Никогда = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Запечатать вложения. Пустой список — <c>null</c>: колонки нет, нечего открывать.</summary>
    public static byte[]? Seal(
        ISecretEnvelope envelope, WrappedDataKey key, IReadOnlyList<MailAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return null;
        }

        var записи = attachments
            .Select(файл => new Запись(Имя(файл.FileName), Тип(файл.ContentType), файл.Content))
            .ToList();

        var открыто = JsonSerializer.SerializeToUtf8Bytes(записи);

        try
        {
            return envelope.Seal(key, открыто, Контекст).Bytes;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(открыто);
        }
    }

    /// <summary>Открыть вложения письма. <c>null</c> в колонке — пустой список.</summary>
    public static IReadOnlyList<MailAttachment> Open(
        ISecretEnvelope envelope, WrappedDataKey key, byte[]? sealedAttachments)
    {
        if (sealedAttachments is null or { Length: 0 })
        {
            return [];
        }

        var открыто = envelope.Open(key, new SealedSecret(sealedAttachments), Контекст);

        try
        {
            var записи = JsonSerializer.Deserialize<List<Запись>>(открыто) ?? [];

            return [.. записи.Select(з => new MailAttachment(з.Name, з.Type, з.Content))];
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(открыто);
        }
    }

    /// <summary>
    /// Имя файла без пути.
    /// </summary>
    /// <remarks>
    /// ⚠ Имя приходит снаружи (из ответа чужого сервера), а почтовый клиент получателя кладёт файл
    /// по нему. <c>../../</c> в имени — повод для обхода каталога у получателя, и отрезается здесь.
    /// </remarks>
    internal static string Имя(string fileName)
    {
        var имя = Path.GetFileName(fileName.Replace('\\', '/')).Trim();

        return string.IsNullOrEmpty(имя) ? "attachment.bin" : имя;
    }

    /// <summary>Тип содержимого или запасной, если присланный не разбирается.</summary>
    internal static string Тип(string contentType) =>
        ContentType.TryParse(contentType, out var тип) ? тип.MimeType : ЗапасныйТип;

    /// <summary>Одно вложение в запечатанном массиве.</summary>
    private sealed record Запись(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("content")] byte[] Content);
}
