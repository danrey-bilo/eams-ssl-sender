using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EmailSslSender.Shared.Crypto;

/// <summary>
/// Подпись значений недоверенных хранилищ на ключе, выведенном из мастер-ключа.
/// </summary>
/// <remarks>
/// <b>Формат конверта:</b> <c>версия.подпись.значение</c> — номер версии мастер-ключа,
/// затем HMAC-SHA256 в base64url, затем само значение как есть.
///
/// ⚠ <b>Номер версии в конверте обязателен.</b> Мастер-ключ ротируется, ключ подписи
/// выводится из него — значит после ротации все прежние записи перестают проверяться. Без
/// номера различить «ротация» и «подделка» было бы нечем, и каждая ротация выглядела бы
/// как атака. С номером: версия в ринге есть, а подпись не сошлась — подделка; версии нет —
/// промах.
///
/// ⚠ <b>Подписывается действующей версией, проверяется любой из ринга.</b> Ровно то же
/// правило, что у <see cref="SecretEnvelope"/>, и по той же причине: пока ротация идёт, в
/// хранилище лежат записи обеих версий.
///
/// ⚠ <b>Разделение назначений.</b> Ключ выводится HKDF-SHA256 с информацией
/// <c>sender:integrity:v1:{назначение}</c>. Разные назначения получают разные ключи:
/// значение, годное в одном хранилище, не должно оказаться годным в другом.
/// </remarks>
public sealed class IntegrityProtector(MasterKeyRing ring) : IIntegrityProtector
{
    /// <summary>Длина ключа подписи. Столько же, сколько у SHA-256 на выходе.</summary>
    private const int KeyBytes = 32;

    /// <summary>
    /// Разделитель назначений. Меняется только вместе с форматом конверта.
    /// </summary>
    /// <remarks>
    /// Номер версии здесь — не украшение: смени формат подписи, не сменив строку, и старые
    /// записи начнут проверяться новым способом, то есть выглядеть подделанными.
    /// </remarks>
    private const string Domain = "sender:integrity:v1:";

    /// <summary>
    /// Выведенные ключи: по одному на пару «назначение, версия».
    /// </summary>
    /// <remarks>
    /// HKDF дешёвый, но зовётся он на <b>каждое</b> чтение кэша, то есть несколько раз на
    /// запрос. Кэш здесь ограничен сверху сам собой: назначений единицы, версий ринга —
    /// тоже.
    /// </remarks>
    private readonly ConcurrentDictionary<(string Purpose, int Version), byte[]> _keys = new();

    public string Protect(string purpose, string recordKey, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(recordKey);
        ArgumentNullException.ThrowIfNull(value);

        var version = ring.CurrentVersion;
        var tag = Sign(Key(purpose, version), recordKey, value);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{version}.{Base64Url.Encode(tag)}.{value}");
    }

    public IntegrityVerdict TryUnprotect(string purpose, string recordKey, string envelope, out string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(recordKey);

        value = null;

        if (!Разобрать(envelope, out var version, out var tag, out var payload))
        {
            // ⚠ Именно Tampered, а не Foreign. Неразбираемое значение под нашим ключом
            // означает, что туда писал кто-то, кроме нас: форму записи различает номер в
            // самом ключе хранилища, и записи прежней формы просто не находятся.
            return IntegrityVerdict.Tampered;
        }

        if (!ring.Has(version))
        {
            return IntegrityVerdict.Foreign;
        }

        var ожидаемая = Sign(Key(purpose, version), recordKey, payload);

        // ⚠ Сравнение постоянного времени. Обычное «равно» сравнивает побайтно и выходит на
        // первом различии — по времени ответа подпись подбирается байт за байтом. Здесь это
        // особенно к месту: ключ записи вычисляется детерминированно, то есть повторять
        // попытку можно сколько угодно.
        // мутация 2026-08-25: сравнение, не отвергающее ничего → покраснели четыре
        // проверки IntegrityProtectorTests (чужая запись, чужое назначение, сдвиг границы,
        // изменённое значение).
        if (!CryptographicOperations.FixedTimeEquals(tag, ожидаемая))
        {
            return IntegrityVerdict.Tampered;
        }

        value = payload;

        return IntegrityVerdict.Ok;
    }

    /// <summary>Подпись: сначала ключ записи, потом значение, между ними — разделитель.</summary>
    /// <remarks>
    /// ⚠ <b>Разделитель обязателен, и вот почему.</b> Без него пара
    /// («ключ <c>ab</c>», «значение <c>c</c>») и пара («ключ <c>a</c>», «значение
    /// <c>bc</c>») дают одну и ту же подписываемую строку — то есть годную подпись можно
    /// переставить между записями, подобрав границу. Нулевой байт в ключах записей и в JSON
    /// не встречается, поэтому граница однозначна.
    /// </remarks>
    // мутация 2026-08-25: ключ записи выброшен из подписываемых байтов → сначала НЕ
    // покраснело ничего, потому что все ключи в сьюте были разной длины и подписи
    // расходились по размеру буфера. Дописан тест на ключи одной длины — покраснел он.
    private static byte[] Sign(byte[] key, string recordKey, string value)
    {
        var bytes = new byte[
            Encoding.UTF8.GetByteCount(recordKey) + 1 + Encoding.UTF8.GetByteCount(value)];

        var written = Encoding.UTF8.GetBytes(recordKey, bytes);
        bytes[written] = 0;
        Encoding.UTF8.GetBytes(value, bytes.AsSpan(written + 1));

        return HMACSHA256.HashData(key, bytes);
    }

    /// <summary>Ключ подписи для назначения и версии мастер-ключа.</summary>
    /// <remarks>
    /// ⚠ Не <c>GetOrAdd</c> с фабрикой-лямбдой: мастер-ключ приходит из ринга
    /// <see cref="ReadOnlySpan{T}"/>'ом — намеренно, чтобы его нельзя было унести, — а
    /// span не захватывается замыканием. Двойной вывод при гонке безобиден: HKDF
    /// детерминирован, оба потока получат один и тот же ключ.
    /// </remarks>
    private byte[] Key(string purpose, int version)
    {
        if (_keys.TryGetValue((purpose, version), out var готовый))
        {
            return готовый;
        }

        var ключ = new byte[KeyBytes];

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ring.Key(version),
            ключ,
            salt: default,
            info: Encoding.UTF8.GetBytes(Domain + purpose));

        return _keys.GetOrAdd((purpose, version), ключ);
    }

    /// <summary>Разобрать конверт. <c>false</c> — это не наш конверт вовсе.</summary>
    private static bool Разобрать(
        string? envelope, out int version, out byte[] tag, out string payload)
    {
        version = 0;
        tag = [];
        payload = string.Empty;

        if (string.IsNullOrEmpty(envelope))
        {
            return false;
        }

        var первая = envelope.IndexOf('.', StringComparison.Ordinal);

        if (первая <= 0)
        {
            return false;
        }

        var вторая = envelope.IndexOf('.', первая + 1);

        if (вторая < 0)
        {
            return false;
        }

        if (!int.TryParse(
                envelope.AsSpan(0, первая), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            || version < 1)
        {
            return false;
        }

        if (!Base64Url.TryDecode(envelope.AsSpan(первая + 1, вторая - первая - 1), out tag)
            || tag.Length != KeyBytes)
        {
            return false;
        }

        payload = envelope[(вторая + 1)..];

        return true;
    }
}

/// <summary>base64url без выравнивания — тот же алфавит, что у JWT.</summary>
/// <remarks>
/// Обычный base64 даёт <c>+</c>, <c>/</c> и <c>=</c>: первые два ничего не ломают в базе,
/// но делают значение неудобным везде, куда оно однажды попадёт, — в URL, в имя файла, в
/// заголовок. Дешевле выбрать безопасный алфавит сразу.
/// </remarks>
internal static class Base64Url
{
    // ⚠ Штатный кодек из BCL (есть с .NET 9), а не Replace-цепочки: прежняя редакция строила
    // три-четыре промежуточные строки на КАЖДУЮ проверку подписи кэша прав — то есть на
    // каждый авторизованный запрос (этап 23.2). Алфавит и отсутствие выравнивания кодек
    // понимает из коробки.
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);

    public static bool TryDecode(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];

        Span<byte> буфер = stackalloc byte[64];

        // ⚠ Перегрузка с OperationStatus, а не Try*: `TryDecodeFromChars` вопреки имени
        // БРОСАЕТ FormatException на невалидном знаке — false у неё только про размер
        // буфера. Поймано тестом «мусор считается подделкой» при переходе на BCL-кодек.
        var статус = System.Buffers.Text.Base64Url.DecodeFromChars(
            text, буфер, out var прочитано, out var written);

        if (статус != System.Buffers.OperationStatus.Done || прочитано != text.Length)
        {
            return false;
        }

        bytes = буфер[..written].ToArray();

        return true;
    }
}
