using System.Text;
using EmailSslSender.Modules.Identity.TwoFactor;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// TOTP: сверка с векторами самого стандарта.
/// </summary>
/// <remarks>
/// ⚠ Ради этих векторов алгоритм и написан своими руками, а не взят пакетом. RFC 6238 §5
/// печатает ожидаемые коды для известного ключа и известных моментов — то есть реализацию
/// можно сверить с первоисточником, а не поверить на слово.
///
/// База не нужна: это чистая функция.
/// </remarks>
public sealed class TotpTests
{
    /// <summary>Ключ из RFC 6238 Appendix B: ASCII «12345678901234567890».</summary>
    private static readonly byte[] Reference = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Векторы_RFC_6238_сходятся(long unixSeconds, string expected)
    {
        // Восемь цифр — так печатает их сам RFC. В бою используется шесть, но это те же
        // числа, обрезанные короче: сойдясь на восьми, шесть сойдутся тем более.
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Totp.Code(Reference, step, digits: 8).ShouldBe(expected);
    }

    [Fact]
    public void Код_шести_цифр_и_с_ведущими_нулями()
    {
        // ⚠ Ведущий ноль теряется, если код форматировать как число. Приложение при этом
        // покажет «012345», а сервер сравнит с «12345» — и человек не войдёт раз в десять
        // попыток, причём воспроизвести это будет нечем.
        var codes = Enumerable.Range(0, 500).Select(step => Totp.Code(Reference, step)).ToList();

        codes.ShouldAllBe(code => code.Length == Totp.Digits);
        codes.ShouldAllBe(code => code.All(char.IsAsciiDigit));
        codes.ShouldContain(code => code[0] == '0', "среди пятисот шагов ноль впереди обязан встретиться");
    }

    [Fact]
    public void Свой_код_принимается_чужой_нет()
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Code(Reference, Totp.StepAt(now));

        Totp.Verify(Reference, code, now).ShouldBe(Totp.StepAt(now));
        Totp.Verify(Totp.NewSecret(), code, now).ShouldBeNull("чужой секрет не открывает");
    }

    [Fact]
    public void Расхождение_часов_в_один_шаг_прощается_в_обе_стороны()
    {
        // Ноль означал бы, что человек с секундой отставания не входит никогда.
        var now = DateTimeOffset.UtcNow;

        foreach (var shift in new[] { -Totp.StepSeconds, 0, Totp.StepSeconds })
        {
            var code = Totp.Code(Reference, Totp.StepAt(now.AddSeconds(shift)));

            Totp.Verify(Reference, code, now).ShouldNotBeNull($"сдвиг {shift} с должен прощаться");
        }
    }

    [Fact]
    public void Два_шага_расхождения_уже_не_прощаются()
    {
        // ⚠ Обратная сторона окна, и она важнее первой. Проверка, у которой испытано только
        // «прощаем», может прощать что угодно: с окном в час украденный код годен час.
        var now = DateTimeOffset.UtcNow;

        foreach (var shift in new[] { -2 * Totp.StepSeconds, 2 * Totp.StepSeconds })
        {
            var code = Totp.Code(Reference, Totp.StepAt(now.AddSeconds(shift)));

            Totp.Verify(Reference, code, now).ShouldBeNull($"сдвиг {shift} с прощаться не должен");
        }
    }

    [Fact]
    public void Принятый_шаг_и_всё_до_него_второй_раз_не_проходят()
    {
        // ⚠ Ради этого Verify возвращает номер шага, а не «да/нет». Подсмотренный код живёт
        // тридцать секунд, и без памяти о принятом шаге его хватает на второй вход — в том
        // числе тому, кто подсмотрел его через плечо.
        var now = DateTimeOffset.UtcNow;
        var step = Totp.StepAt(now);
        var code = Totp.Code(Reference, step);

        Totp.Verify(Reference, code, now, after: -1).ShouldBe(step);
        Totp.Verify(Reference, code, now, after: step).ShouldBeNull("тот же код второй раз");

        // И прошлый код, попавший в окно, тоже не проходит после более свежего.
        var previous = Totp.Code(Reference, step - 1);

        Totp.Verify(Reference, previous, now, after: step).ShouldBeNull();
    }

    [Fact]
    public void Мусор_вместо_кода_отвергается_без_исключений()
    {
        var now = DateTimeOffset.UtcNow;

        Totp.Verify(Reference, "", now).ShouldBeNull();
        Totp.Verify(Reference, "   ", now).ShouldBeNull();
        Totp.Verify(Reference, "12345", now).ShouldBeNull();
        Totp.Verify(Reference, "1234567", now).ShouldBeNull();
        Totp.Verify(Reference, "абвгде", now).ShouldBeNull();
    }

    [Fact]
    public void Пробелы_в_коде_прощаются()
    {
        // Приложения показывают код парами («123 456»), и человек копирует его с пробелом.
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Code(Reference, Totp.StepAt(now));

        Totp.Verify(Reference, $"{code[..3]} {code[3..]}", now).ShouldNotBeNull();
    }

    [Fact]
    public void Base32_совпадает_с_RFC_4648()
    {
        // Вектор из RFC 4648 §10: «foobar» → MZXW6YTBOI (без выравнивания).
        Totp.ToBase32(Encoding.ASCII.GetBytes("foobar")).ShouldBe("MZXW6YTBOI");

        // И секрет из RFC 6238 — то, что человек увидит в приложении.
        Totp.ToBase32(Reference).ShouldBe("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
    }

    [Fact]
    public void Ссылка_для_QR_несёт_всё_что_нужно_приложению()
    {
        var uri = Totp.Uri("EAMS", "пётр@example.com", Reference);

        uri.ShouldStartWith("otpauth://totp/");
        uri.ShouldContain($"secret={Totp.ToBase32(Reference)}");
        uri.ShouldContain("algorithm=SHA1");
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");

        // Адрес с не-ASCII обязан быть закодирован, иначе ссылка разъедется на первом же
        // почтовом ящике с кириллицей.
        uri.ShouldNotContain("пётр");
    }

    [Fact]
    public void Секрет_каждый_раз_новый_и_нужной_длины()
    {
        var first = Totp.NewSecret();
        var second = Totp.NewSecret();

        first.Length.ShouldBe(Totp.SecretBytes);
        Convert.ToHexString(first).ShouldNotBe(Convert.ToHexString(second));
    }
}
