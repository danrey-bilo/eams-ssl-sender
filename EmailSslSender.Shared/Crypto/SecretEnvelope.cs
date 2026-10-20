using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EmailSslSender.Shared.Crypto;

/// <summary>
/// Конверт секретов на AES-256-GCM.
/// </summary>
/// <remarks>
/// <b>Формат самоописывающийся, и это осознанная плата.</b> И обёрнутый ключ, и запечатанный
/// секрет — один массив байтов, в начале которого написано, чем и как их открывать. Так они
/// ложатся в одну колонку <c>bytea</c>, и ни одна таблица в системе не заводит рядом полей
/// «версия ключа» и «nonce», которые однажды разъедутся со своим шифротекстом при копировании
/// строки.
///
/// <b>Заголовок входит в связанные данные.</b> Версию мастер-ключа или поколение ключа данных
/// нельзя подменить, не сломав проверку целостности: попытка «подсказать» системе, что строка
/// обёрнута другой версией, кончается отказом расшифровки, а не тихим чтением не того ключа.
///
/// <b>Разные домены не путаются.</b> Обёртка ключа и запечатанный секрет помечены разными
/// строками домена, поэтому шифротекст одного типа не открывается как другой, даже если ключ
/// подойдёт. Это дешёвая защита от целого класса ошибок, где значение переставили не в ту
/// колонку.
/// </remarks>
/// <param name="ring">Мастер-ключи установки.</param>
public sealed class SecretEnvelope(MasterKeyRing ring) : ISecretEnvelope
{
    /// <summary>Версия формата. Меняется, только если меняется раскладка байтов.</summary>
    private const byte Format = 1;

    /// <summary>Длина ключа данных: AES-256.</summary>
    private const int DataKeyBytes = 32;

    /// <summary>Длина nonce. AES-GCM в .NET принимает <b>только</b> 96 бит.</summary>
    private const int NonceBytes = 12;

    /// <summary>
    /// Длина тега проверки подлинности.
    /// </summary>
    /// <remarks>
    /// Шестнадцать байт, полный тег. AES-GCM допускает от 12 до 16, укорачивая его, и до .NET 8
    /// длина бралась из самого тега — то есть вызывающий, подсунувший тег покороче, молча
    /// ослаблял проверку. Начиная с .NET 8 длину требуют указать в конструкторе
    /// (SYSLIB0053), и мы указываем максимум: на Apple короче и не поддерживается.
    /// </remarks>
    private const int TagBytes = 16;

    /// <summary>Заголовок обёртки: формат, версия мастер-ключа, поколение ключа данных.</summary>
    private const int WrapHeaderBytes = 1 + 4 + 4;

    /// <summary>Заголовок секрета: формат и поколение ключа данных.</summary>
    private const int SealHeaderBytes = 1 + 4;

    /// <summary>Домен обёртки. Не даёт открыть обёрнутый ключ как секрет.</summary>
    /// <remarks>
    /// <c>internal</c> ради теста, и это не уступка: мутация «сделать домены одинаковыми»
    /// проходила весь сьют зелёной. Косвенно домены не проверяются никак — обёртку и секрет
    /// и без них разводят разные ключи, — а значит защита, которую однажды придётся
    /// починить, не была бы подтверждена ничем.
    /// </remarks>
    internal static readonly byte[] WrapDomain = Encoding.UTF8.GetBytes("sender:dek:v1\0");

    /// <summary>Домен секрета.</summary>
    internal static readonly byte[] SealDomain = Encoding.UTF8.GetBytes("sender:secret:v1\0");

    public WrappedDataKey CreateDataKey(int generation = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);

        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            RandomNumberGenerator.Fill(dataKey);

            return Wrap(dataKey, generation);
        }
        finally
        {
            // Ключ данных не должен пережить эту строку в открытом виде. Один вызов —
            // одна жизнь ключа в памяти.
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public SealedSecret Seal(WrappedDataKey key, ReadOnlySpan<byte> plaintext, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            var generation = Unwrap(key, dataKey);

            var blob = new byte[SealHeaderBytes + NonceBytes + TagBytes + plaintext.Length];

            blob[0] = Format;
            BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(1, 4), generation);

            var nonce = blob.AsSpan(SealHeaderBytes, NonceBytes);
            var tag = blob.AsSpan(SealHeaderBytes + NonceBytes, TagBytes);
            var ciphertext = blob.AsSpan(SealHeaderBytes + NonceBytes + TagBytes);

            // ⚠ Случайный nonce на КАЖДОЕ запечатывание. Повтор пары «ключ, nonce» в GCM —
            // это не «чуть слабее»: он выдаёт исключающее ИЛИ двух открытых текстов и
            // позволяет подделывать теги. Счётчик здесь не годится: процессов может быть
            // несколько, а общего состояния между ними нет.
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(dataKey, TagBytes);

            aes.Encrypt(nonce, plaintext, ciphertext, tag, Associated(SealDomain, blob.AsSpan(0, SealHeaderBytes), context));

            return new SealedSecret(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public byte[] Open(WrappedDataKey key, SealedSecret secret, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        var blob = Require(secret.Bytes, SealHeaderBytes + NonceBytes + TagBytes, "запечатанный секрет");

        if (blob[0] != Format)
        {
            throw new CryptographicException(
                $"Запечатанный секрет формата {blob[0]}, а этот код понимает только {Format}.");
        }

        var generation = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(1, 4));

        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            var keyGeneration = Unwrap(key, dataKey);

            if (keyGeneration != generation)
            {
                // Внятный отказ вместо общего «не сошёлся тег»: разница между «ключ не тот»
                // и «данные испорчены» — это разница между «перешифруй» и «зови безопасность».
                throw new CryptographicException(
                    $"Секрет запечатан поколением ключа {generation}, а предъявлено {keyGeneration}. "
                    + "Так выглядит секрет, не перешифрованный после перевыпуска ключа хранилища.");
            }

            var nonce = blob.AsSpan(SealHeaderBytes, NonceBytes);
            var tag = blob.AsSpan(SealHeaderBytes + NonceBytes, TagBytes);
            var ciphertext = blob.AsSpan(SealHeaderBytes + NonceBytes + TagBytes);

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(dataKey, TagBytes);

            aes.Decrypt(nonce, ciphertext, tag, plaintext, Associated(SealDomain, blob.AsSpan(0, SealHeaderBytes), context));

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public SealedSecret SealText(WrappedDataKey key, string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var bytes = Encoding.UTF8.GetBytes(plaintext);

        try
        {
            return Seal(key, bytes, context);
        }
        finally
        {
            // Затираем то, что можем. Саму строку затереть нечем — она неизменяема, и это
            // принятый риск, а не недосмотр.
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string OpenText(WrappedDataKey key, SealedSecret secret, string context)
    {
        var bytes = Open(key, secret, context);

        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool NeedsRewrap(WrappedDataKey key) => KekVersion(key) != ring.CurrentVersion;

    public WrappedDataKey Rewrap(WrappedDataKey key)
    {
        Span<byte> dataKey = stackalloc byte[DataKeyBytes];

        try
        {
            var generation = Unwrap(key, dataKey);

            return Wrap(dataKey, generation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Какой версией мастер-ключа обёрнут ключ данных.</summary>
    private static int KekVersion(WrappedDataKey key)
    {
        var blob = Require(key.Bytes, WrapHeaderBytes + NonceBytes + TagBytes + DataKeyBytes, "обёрнутый ключ данных");

        if (blob[0] != Format)
        {
            throw new CryptographicException(
                $"Обёрнутый ключ формата {blob[0]}, а этот код понимает только {Format}.");
        }

        return BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(1, 4));
    }

    /// <summary>Обернуть ключ данных действующим мастер-ключом.</summary>
    private WrappedDataKey Wrap(ReadOnlySpan<byte> dataKey, int generation)
    {
        var blob = new byte[WrapHeaderBytes + NonceBytes + TagBytes + DataKeyBytes];

        blob[0] = Format;
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(1, 4), ring.CurrentVersion);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(5, 4), generation);

        var nonce = blob.AsSpan(WrapHeaderBytes, NonceBytes);
        var tag = blob.AsSpan(WrapHeaderBytes + NonceBytes, TagBytes);
        var ciphertext = blob.AsSpan(WrapHeaderBytes + NonceBytes + TagBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(ring.Key(ring.CurrentVersion), TagBytes);

        aes.Encrypt(nonce, dataKey, ciphertext, tag, Associated(WrapDomain, blob.AsSpan(0, WrapHeaderBytes), null));

        return new WrappedDataKey(blob);
    }


    /// <summary>Развернуть ключ данных в переданный буфер. Возвращает поколение ключа.</summary>
    private int Unwrap(WrappedDataKey key, Span<byte> destination)
    {
        var blob = Require(key.Bytes, WrapHeaderBytes + NonceBytes + TagBytes + DataKeyBytes, "обёрнутый ключ данных");

        if (blob[0] != Format)
        {
            throw new CryptographicException(
                $"Обёрнутый ключ формата {blob[0]}, а этот код понимает только {Format}.");
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(1, 4));
        var generation = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(5, 4));

        var nonce = blob.AsSpan(WrapHeaderBytes, NonceBytes);
        var tag = blob.AsSpan(WrapHeaderBytes + NonceBytes, TagBytes);
        var ciphertext = blob.AsSpan(WrapHeaderBytes + NonceBytes + TagBytes);

        using var aes = new AesGcm(ring.Key(version), TagBytes);

        try
        {
            aes.Decrypt(
                nonce, ciphertext, tag, destination, Associated(WrapDomain, blob.AsSpan(0, WrapHeaderBytes), null));
        }
        catch (AuthenticationTagMismatchException error)
        {
            // ⚠ ⚠ ВНЯТНЫЙ ОТКАЗ ВМЕСТО «не сошёлся тег», и цена разницы измерена.
            // Обёртка ключа хранилища расшифровывается МАСТЕР-КЛЮЧОМ установки; не сошлась —
            // значит предъявлен не тот мастер-ключ. Голое исключение криптографии на этом
            // месте выглядит как порча данных и уводит разбираться не туда: на живой проверке
            // 2026-08-28 оно стоило получаса на вопрос «почему второй фактор ломает сервер»,
            // при том что ответ был «поднято с другим ключом».
            //
            // ⚠ Отличать это от порчи данных ОБЯЗАТЕЛЬНО: «ключ не тот» лечится правильным
            // ключом или перешифровкой, «данные испорчены» — восстановлением из копии и
            // разговором со службой безопасности. Одно сообщение на два случая означает, что
            // половину раз будут делать не то.
            //
            // ⚠ Номер версии в тексте есть, ключа в тексте нет и быть не может (правило 14).
            throw new CryptographicException(
                $"Обёртка ключа хранилища не расшифровывается мастер-ключом версии {version}. "
                + "Так выглядит установка, поднятая с ДРУГИМ мастер-ключом: проверьте "
                + "EAMS_MASTER_KEK_FILE и связку ключей. Если ключ заведомо верный — данные "
                + "испорчены, и это разговор с безопасностью, а не перезапуск.",
                error);
        }

        return generation;
    }

    /// <summary>
    /// Связанные данные: домен, заголовок и — для секрета — привязка к месту.
    /// </summary>
    /// <remarks>
    /// Домен разводит обёртки и секреты. Заголовок входит сюда, чтобы версию и поколение
    /// нельзя было переписать в базе. Контекст привязывает шифротекст к строке и колонке:
    /// перенесённый в чужую строку секрет не откроется.
    /// </remarks>
    internal static byte[] Associated(byte[] domain, ReadOnlySpan<byte> header, string? context)
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

    /// <summary>Проверить, что значение вообще похоже на то, чем себя называет.</summary>
    private static byte[] Require(byte[]? blob, int minimum, string what)
    {
        if (blob is null || blob.Length < minimum)
        {
            // Длина — не секрет, и назвать её полезно: чаще всего сюда приезжает пустая
            // колонка, то есть «секрета нет», а не «секрет испорчен».
            throw new CryptographicException(
                $"Ожидался {what} длиной от {minimum} байт, получено {blob?.Length ?? 0}.");
        }

        return blob;
    }
}
