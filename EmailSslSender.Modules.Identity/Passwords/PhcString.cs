using System.Globalization;

namespace EmailSslSender.Modules.Identity.Passwords;

/// <summary>
/// Разобранный хеш пароля: параметры, соль и сам хеш — всё, что лежит в колонке.
/// </summary>
/// <param name="Profile">С какими параметрами он посчитан.</param>
/// <param name="Salt">Соль. У каждого пароля своя.</param>
/// <param name="Hash">Собственно хеш.</param>
internal sealed record PasswordHash(Argon2Profile Profile, byte[] Salt, byte[] Hash);

/// <summary>
/// Формат PHC — то, как хеш пароля живёт в базе.
/// </summary>
/// <remarks>
/// <c>$argon2id$v=19$m=65536,t=3,p=4$&lt;соль&gt;$&lt;хеш&gt;</c> — стандарт
/// <a href="https://github.com/C2SP/C2SP/blob/main/phc-strings.md">C2SP phc-strings</a>,
/// тот же, что у argon2-cffi, libsodium и всех прочих. Библиотека Konscious его не умеет:
/// она отдаёт голый <c>byte[]</c>, а собирать и разбирать строку обязан вызывающий.
///
/// <b>Почему именно он, а не свой компактный формат.</b> Строка <b>самоописательна</b>:
/// параметры лежат рядом с хешем, а не в конфигурации. Отсюда два свойства, ради которых
/// всё и делается: смена параметров не делает нечитаемыми прежние хеши, а определить, какие
/// строки устарели, можно не входя в систему — обычным запросом по колонке.
///
/// ⚠ <b>База64 без выравнивания.</b> Стандарт: «The B64 encoding is the standard Base64
/// encoding (RFC 4648, section 4) except that the padding `=` signs are omitted». Оставленный
/// хвост <c>==</c> сделает строку нечитаемой для любой другой реализации — а прочитать её
/// однажды может понадобиться не нам.
/// </remarks>
internal static class PhcString
{
    /// <summary>Идентификатор варианта. Единственный, который мы пишем.</summary>
    private const string Algorithm = "argon2id";

    /// <summary>Версия самого argon2 — 0x13, она же 19. Другой у нас не бывает.</summary>
    private const int Version = 19;

    /// <summary>Собрать строку для колонки.</summary>
    public static string Encode(PasswordHash hash)
    {
        var profile = hash.Profile;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"${Algorithm}$v={Version}$m={profile.MemoryKib},t={profile.Iterations},p={profile.Parallelism}"
            + $"${Base64NoPadding(hash.Salt)}${Base64NoPadding(hash.Hash)}");
    }

    /// <summary>
    /// Разобрать строку из колонки.
    /// </summary>
    /// <remarks>
    /// Возвращает <c>false</c> на любой мусор и <b>не бросает исключений</b>. Так надо:
    /// испорченная строка в колонке обязана превратиться в обычный отказ во входе, а не в
    /// пятисотку. Разница между «не тот пароль» и «мусор в базе» наружу не просачивается —
    /// иначе она становится оракулом.
    ///
    /// В лог о таком стоит сказать отдельно, и это делает вызывающий: молчаливо
    /// проглоченный мусор означает, что человека заблокирует его же правильный пароль, а
    /// причины не узнает никто.
    /// </remarks>
    public static bool TryDecode(string? encoded, out PasswordHash? hash)
    {
        hash = null;

        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        // Пять частей: пустая (строка начинается с «$»), вариант, версия, параметры, соль,
        // хеш. Итого шесть кусков после разбиения.
        var parts = encoded.Split('$');

        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != Algorithm)
        {
            return false;
        }

        if (parts[2] != $"v={Version}")
        {
            return false;
        }

        if (!TryParameters(parts[3], out var memory, out var iterations, out var parallelism))
        {
            return false;
        }

        if (!TryBase64(parts[4], out var salt) || !TryBase64(parts[5], out var raw))
        {
            return false;
        }

        var profile = new Argon2Profile(memory, iterations, parallelism, salt.Length, raw.Length);

        try
        {
            profile.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            // Числа разобрались, но бессмысленны: например, память меньше 8×p. Такую строку
            // мы не писали — значит её либо испортили, либо подменили.
            return false;
        }

        hash = new PasswordHash(profile, salt, raw);

        return true;
    }

    /// <summary>Разобрать <c>m=…,t=…,p=…</c> — строго в этом порядке.</summary>
    /// <remarks>
    /// Порядок не свободен: стандарт требует, чтобы производитель писал параметры в порядке,
    /// заданном функцией, а для argon2 это <c>m,t,p</c>. Принимать любой порядок значило бы
    /// принимать и то, чего сами не пишем.
    /// </remarks>
    private static bool TryParameters(string text, out int memory, out int iterations, out int parallelism)
    {
        memory = iterations = parallelism = 0;

        var parts = text.Split(',');

        return parts.Length == 3
            && TryNamed(parts[0], "m", out memory)
            && TryNamed(parts[1], "t", out iterations)
            && TryNamed(parts[2], "p", out parallelism);
    }

    private static bool TryNamed(string text, string name, out int value)
    {
        value = 0;

        return text.StartsWith(name + '=', StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(name.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string Base64NoPadding(byte[] value) => Convert.ToBase64String(value).TrimEnd('=');

    private static bool TryBase64(string text, out byte[] value)
    {
        value = [];

        // Выравнивание дописывается обратно: `Convert` без него не работает, а в строке его
        // нет по стандарту.
        var padded = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');

        if (!Convert.TryFromBase64String(padded, new byte[padded.Length], out var written))
        {
            return false;
        }

        var buffer = new byte[written];
        Convert.TryFromBase64String(padded, buffer, out _);
        value = buffer;

        return value.Length > 0;
    }
}
