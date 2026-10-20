using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EmailSslSender.Modules.Identity.TwoFactor;

/// <summary>
/// Одноразовые коды по времени: RFC 6238 поверх RFC 4226.
/// </summary>
/// <remarks>
/// <b>Своя реализация, а не пакет, и это осознанно.</b> Алгоритм — тридцать строк, а
/// проверить его можно <b>векторами самого стандарта</b>: RFC 6238 §5 печатает ожидаемые коды
/// для известного ключа и известных моментов времени. Это сильнее, чем доверие непроверенному
/// пакету с NuGet: там пришлось бы верить на слово, здесь ответ сверяется с первоисточником.
///
/// <b>Параметры зафиксированы и совпадают с тем, что умеют приложения.</b> SHA-1, шаг 30
/// секунд, шесть цифр — это то, что Google Authenticator, Aegis и 1Password считают
/// умолчанием и что многие из них умеют <i>только</i>. SHA-1 здесь не слабое место: HMAC-SHA1
/// не сломан, а секрет живёт тридцать секунд.
///
/// <b>Окно в один шаг</b> — плата за расхождение часов. Ноль означал бы, что человек с
/// секундой отставания не входит никогда; два и больше — что украденный код годен полторы
/// минуты. RFC 6238 §5.2 рекомендует ровно один.
/// </remarks>
internal static class Totp
{
    /// <summary>Длина шага в секундах. Меняется только вместе со всеми уже заведёнными приложениями.</summary>
    public const int StepSeconds = 30;

    /// <summary>Сколько цифр в коде.</summary>
    public const int Digits = 6;

    /// <summary>Сколько шагов в обе стороны прощаем за расхождение часов.</summary>
    public const int Window = 1;

    /// <summary>Длина секрета. Двадцать байт — размер выхода SHA-1, как в примерах RFC.</summary>
    public const int SecretBytes = 20;

    /// <summary>Завести новый секрет.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>Номер шага для момента времени.</summary>
    public static long StepAt(DateTimeOffset moment) => moment.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>
    /// Код для конкретного шага.
    /// </summary>
    /// <param name="secret">Общий секрет.</param>
    /// <param name="step">Номер шага.</param>
    /// <param name="digits">Сколько цифр. Шесть в бою; восемь — только чтобы сверяться с RFC.</param>
    public static string Code(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, hash);

        // Динамическое усечение, RFC 4226 §5.3: младшие четыре бита последнего байта
        // указывают, откуда брать четыре байта результата. Старший бит гасится, чтобы
        // число не оказалось отрицательным на платформах со знаковой арифметикой.
        var offset = hash[^1] & 0x0F;

        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var modulo = (int)Math.Pow(10, digits);

        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// Проверить код и сказать, какому шагу он соответствует.
    /// </summary>
    /// <remarks>
    /// ⚠ Возвращается <b>номер шага</b>, а не «да/нет», и это не удобство. Принятый шаг надо
    /// запомнить, чтобы тот же код нельзя было предъявить второй раз: подсмотренный код
    /// живёт тридцать секунд, и без этой памяти его хватает на второй вход.
    ///
    /// Сравнение постоянного времени: коды короткие, и посимвольное сравнение выдало бы
    /// правильный префикс замером.
    /// </remarks>
    /// <param name="secret">Общий секрет.</param>
    /// <param name="code">Что ввёл человек.</param>
    /// <param name="now">Текущий момент.</param>
    /// <param name="after">Шаг последнего принятого кода: этот и более ранние не принимаются.</param>
    /// <returns>Номер шага или <c>null</c>, если код не подошёл.</returns>
    public static long? Verify(ReadOnlySpan<byte> secret, string code, DateTimeOffset now, long after = -1)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

        if (trimmed.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);

        for (var step = current - Window; step <= current + Window; step++)
        {
            if (step <= after)
            {
                // Этот код уже принимали. Молча пропускаем: наружу разницы быть не должно.
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Code(secret, step)),
                Encoding.ASCII.GetBytes(trimmed)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>
    /// Секрет в base32 — так его ждут приложения.
    /// </summary>
    /// <remarks>
    /// RFC 4648 без выравнивания: приложения-аутентификаторы принимают строку без знаков
    /// <c>=</c>, а некоторые на них спотыкаются.
    /// </remarks>
    public static string ToBase32(ReadOnlySpan<byte> secret)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var builder = new StringBuilder((secret.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var value in secret)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Ссылка <c>otpauth://</c> для QR-кода.
    /// </summary>
    /// <remarks>
    /// Формат — де-факто стандарт Google Authenticator. Имя издателя повторяется дважды
    /// намеренно: в пути оно показывается в списке приложения, в параметре — используется при
    /// переносе между устройствами, и приложения, читающие только одно из мест, встречаются
    /// оба вида.
    /// </remarks>
    public static string Uri(string issuer, string account, ReadOnlySpan<byte> secret)
    {
        var label = System.Uri.EscapeDataString($"{issuer}:{account}");

        return $"otpauth://totp/{label}"
            + $"?secret={ToBase32(secret)}"
            + $"&issuer={System.Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}
