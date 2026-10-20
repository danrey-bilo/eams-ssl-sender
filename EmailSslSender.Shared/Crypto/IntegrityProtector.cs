using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EmailSslSender.Shared.Crypto;

/// <summary>
/// Подпись значений на ключе, выведенном из мастер-ключа.
/// </summary>
/// <remarks>
/// <b>Формат конверта:</b> <c>версия.подпись.значение</c> — номер версии мастер-ключа,
/// затем HMAC-SHA256 в base64url, затем само значение как есть.
///
/// ⚠ <b>Подписывается действующей версией мастер-ключа, и номер её входит в конверт.</b> После
/// ротации прежние подписи с новыми не совпадут: для кодов входа, живущих минуты, это
/// безобидно, а для долгоживущих значений подпись пришлось бы перевыпускать.
///
/// ⚠ <b>Разделение назначений.</b> Ключ выводится HKDF-SHA256 с информацией
/// <c>sender:integrity:v1:{назначение}</c>. Разные назначения получают разные ключи:
/// подпись, годная для одного потребителя, не совпадёт у другого.
/// </remarks>
public sealed class IntegrityProtector(MasterKeyRing ring) : IIntegrityProtector
{
    /// <summary>Длина ключа подписи. Столько же, сколько у SHA-256 на выходе.</summary>
    private const int KeyBytes = 32;

    /// <summary>
    /// Разделитель назначений. Меняется только вместе с форматом конверта.
    /// </summary>
    /// <remarks>
    /// Номер версии здесь — не украшение: смени формат подписи, не сменив строку, и прежние
    /// подписи начнут отличаться от новых без всякой видимой причины.
    /// </remarks>
    private const string Domain = "sender:integrity:v1:";

    /// <summary>
    /// Выведенные ключи: по одному на пару «назначение, версия».
    /// </summary>
    /// <remarks>
    /// HKDF дешёвый, но зовётся он на каждую выдачу и проверку кода. Кэш здесь ограничен сверху
    /// сам собой: назначений единицы, версий ринга — тоже.
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

    /// <summary>Подпись: сначала ключ записи, потом значение, между ними — разделитель.</summary>
    /// <remarks>
    /// ⚠ <b>Разделитель обязателен, и вот почему.</b> Без него пара
    /// («ключ <c>ab</c>», «значение <c>c</c>») и пара («ключ <c>a</c>», «значение
    /// <c>bc</c>») дают одну и ту же подписываемую строку — то есть годную подпись можно
    /// переставить между записями, подобрав границу. Нулевой байт в ключах записей и в кодах
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
}

/// <summary>base64url без выравнивания — тот же алфавит, что у JWT.</summary>
/// <remarks>
/// Обычный base64 даёт <c>+</c>, <c>/</c> и <c>=</c>: первые два ничего не ломают в базе,
/// но делают значение неудобным везде, куда оно однажды попадёт, — в URL, в имя файла, в
/// заголовок. Дешевле выбрать безопасный алфавит сразу. Кодек — штатный, из BCL (есть с .NET 9).
/// </remarks>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
