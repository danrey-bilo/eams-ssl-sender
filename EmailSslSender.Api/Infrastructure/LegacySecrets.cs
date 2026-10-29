using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Modules.Crypto;
using Npgsql;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Разовая перекладка секретов из конверта «sender:» в конверт модулей «eams:».</summary>
/// <remarks>⚠ Идемпотентно: открывшееся конвертом модулей пропускается, запись — условием на прежний ключ данных.</remarks>
internal sealed class LegacySecrets(ISecretEnvelope envelope, IReadOnlyDictionary<int, byte[]> legacyKeys)
{
    private const byte Format = 1;
    private const int DataKeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int WrapHeaderBytes = 1 + 4 + 4;
    private const int SealHeaderBytes = 1 + 4;

    private static readonly byte[] WrapDomain = Encoding.UTF8.GetBytes("sender:dek:v1\0");
    private static readonly byte[] SealDomain = Encoding.UTF8.GetBytes("sender:secret:v1\0");

    // Таблица, ключ строки и контексты до и после.
    private sealed record Цель(
        string Name,
        string Select,
        string Update,
        Func<object, string> Прежний,
        Func<object, string> Новый);

    private static readonly Цель[] Цели =
    [
        new(
            "второй фактор (user_two_factor)",
            "SELECT user_id, data_key, secret FROM user_two_factor",
            "UPDATE user_two_factor SET data_key = @dk, secret = @s WHERE user_id = @id AND data_key = @old",
            id => $"user:{id}:totp_seed",
            id => $"user:{id}:totp_seed"),
        new(
            "секреты почты (system_secrets)",
            "SELECT key, data_key, sealed FROM system_secrets",
            "UPDATE system_secrets SET data_key = @dk, sealed = @s WHERE key = @id AND data_key = @old",
            key => $"sender:system-secret:{key}",
            key => $"eams:system-secret:{key}"),
        new(
            "ключ API Key Manager Plus (keymanager_state)",
            "SELECT id, data_key, api_key FROM keymanager_state WHERE api_key IS NOT NULL AND data_key IS NOT NULL",
            "UPDATE keymanager_state SET data_key = @dk, api_key = @s WHERE id = @id AND data_key = @old",
            _ => "sender:keymanager:api-key",
            _ => "sender:keymanager:api-key"),
    ];

    /// <summary>Итог по одной таблице.</summary>
    internal sealed record Итог(string Name, int Просмотрено, int Переложено, int НеОткрылось);

    /// <summary>Ключи прежнего конверта из содержимого источника мастер-ключа.</summary>
    internal static Dictionary<int, byte[]> Keys(string content)
    {
        var ring = MasterKeyRing.Parse(content);
        var keys = new Dictionary<int, byte[]>(ring.Count);

        foreach (var line in content.Split('\n'))
        {
            var строка = line.Trim();

            if (строка.Length == 0 || строка.StartsWith('#'))
            {
                continue;
            }

            var colon = строка.IndexOf(':');
            var version = colon < 0 ? 1 : int.Parse(строка[..colon], NumberStyles.None, CultureInfo.InvariantCulture);

            keys[version] = Convert.FromBase64String(colon < 0 ? строка : строка[(colon + 1)..]);
        }

        return keys;
    }

    /// <summary>Переложить всё, что лежит под прежним конвертом.</summary>
    public async Task<IReadOnlyList<Итог>> RunAsync(string connection, CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(cancellationToken);

        var итоги = new List<Итог>();

        foreach (var цель in Цели)
        {
            var строки = new List<(object Id, byte[] Key, byte[] Sealed)>();

            await using (var чтение = new NpgsqlCommand(цель.Select, db))
            await using (var читатель = await чтение.ExecuteReaderAsync(cancellationToken))
            {
                while (await читатель.ReadAsync(cancellationToken))
                {
                    строки.Add((читатель.GetValue(0), (byte[])читатель.GetValue(1), (byte[])читатель.GetValue(2)));
                }
            }

            int переложено = 0, неОткрылось = 0;

            foreach (var (id, ключ, запечатано) in строки)
            {
                if (ОткрываетсяМодулями(ключ, запечатано, цель.Новый(id)))
                {
                    continue;
                }

                byte[] открыто;

                try
                {
                    открыто = Open(ключ, запечатано, цель.Прежний(id));
                }
                catch (CryptographicException)
                {
                    неОткрылось++;
                    continue;
                }

                try
                {
                    var новыйКлюч = envelope.CreateDataKey();
                    var новое = envelope.Seal(новыйКлюч, открыто, цель.Новый(id));

                    await using var запись = new NpgsqlCommand(цель.Update, db);
                    запись.Parameters.AddWithValue("dk", новыйКлюч.Bytes);
                    запись.Parameters.AddWithValue("s", новое.Bytes);
                    запись.Parameters.AddWithValue("id", id);
                    запись.Parameters.AddWithValue("old", ключ);

                    переложено += await запись.ExecuteNonQueryAsync(cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(открыто);
                }
            }

            итоги.Add(new Итог(цель.Name, строки.Count, переложено, неОткрылось));
        }

        return итоги;
    }

    private bool ОткрываетсяМодулями(byte[] ключ, byte[] запечатано, string context)
    {
        try
        {
            CryptographicOperations.ZeroMemory(envelope.Open(new WrappedDataKey(ключ), new SealedSecret(запечатано), context));

            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Открыть значение прежним конвертом.</summary>
    internal byte[] Open(byte[] wrapped, byte[] sealedBlob, string context)
    {
        if (wrapped.Length != WrapHeaderBytes + NonceBytes + TagBytes + DataKeyBytes || wrapped[0] != Format)
        {
            throw new CryptographicException("Обёртка ключа данных не прежнего формата.");
        }

        if (sealedBlob.Length < SealHeaderBytes + NonceBytes + TagBytes || sealedBlob[0] != Format)
        {
            throw new CryptographicException("Запечатанное значение не прежнего формата.");
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(wrapped.AsSpan(1, 4));

        if (!legacyKeys.TryGetValue(version, out var kek))
        {
            throw new CryptographicException($"Мастер-ключа версии {version} нет в связке.");
        }

        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            using (var aes = new AesGcm(kek, TagBytes))
            {
                aes.Decrypt(
                    wrapped.AsSpan(WrapHeaderBytes, NonceBytes),
                    wrapped.AsSpan(WrapHeaderBytes + NonceBytes + TagBytes),
                    wrapped.AsSpan(WrapHeaderBytes + NonceBytes, TagBytes),
                    dataKey,
                    Associated(WrapDomain, wrapped.AsSpan(0, WrapHeaderBytes), null));
            }

            var открыто = new byte[sealedBlob.Length - SealHeaderBytes - NonceBytes - TagBytes];

            using (var aes = new AesGcm(dataKey, TagBytes))
            {
                aes.Decrypt(
                    sealedBlob.AsSpan(SealHeaderBytes, NonceBytes),
                    sealedBlob.AsSpan(SealHeaderBytes + NonceBytes + TagBytes),
                    sealedBlob.AsSpan(SealHeaderBytes + NonceBytes, TagBytes),
                    открыто,
                    Associated(SealDomain, sealedBlob.AsSpan(0, SealHeaderBytes), context));
            }

            return открыто;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static byte[] Associated(byte[] domain, ReadOnlySpan<byte> header, string? context)
    {
        var contextBytes = context is null ? 0 : Encoding.UTF8.GetByteCount(context);
        var associated = new byte[domain.Length + header.Length + contextBytes];

        domain.CopyTo(associated.AsSpan());
        header.CopyTo(associated.AsSpan(domain.Length));

        if (context is not null)
        {
            Encoding.UTF8.GetBytes(context, associated.AsSpan(domain.Length + header.Length));
        }

        return associated;
    }
}
