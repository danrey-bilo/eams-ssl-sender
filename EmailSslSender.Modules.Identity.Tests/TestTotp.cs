using EmailSslSender.Modules.Identity.TwoFactor;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Код второго фактора для тестов.
/// </summary>
/// <remarks>
/// ⚠ <b>Общий на сьют, а не по копии на файл.</b> Разбор base32 нужен только тестам — в бою
/// секрет декодирует приложение, — и жил он приватным методом в <c>TwoFactorTests</c>. Когда
/// код фактора понадобился второму файлу (проверка мест отзыва, этап 10.5.5), выбор был
/// между второй копией двадцати строк и общим местом. Копия разошлась бы молча: у неё нет ни
/// одного своего теста, а расхождение проявилось бы как «код не подходит» в чужом файле.
/// </remarks>
internal static class TestTotp
{
    /// <summary>
    /// Код на сейчас, со сдвигом на <paramref name="steps"/> окон.
    /// </summary>
    /// <remarks>
    /// ⚠ Сдвиг нужен там, где код уже предъявляли: повторное использование запрещено нарочно,
    /// и код, которым только что включили фактор, для входа не годится. В жизни между этими
    /// двумя действиями проходит больше тридцати секунд, в тесте — нисколько.
    /// </remarks>
    public static string Code(string base32, int steps = 0) =>
        Totp.Code(Decode(base32), Totp.StepAt(DateTimeOffset.UtcNow) + steps);

    /// <summary>Разбор base32 — нужен только тесту.</summary>
    public static byte[] Decode(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new List<byte>(value.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var symbol in value)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(symbol, StringComparison.Ordinal);
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
