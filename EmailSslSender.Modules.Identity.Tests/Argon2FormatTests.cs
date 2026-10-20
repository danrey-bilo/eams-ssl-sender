using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Параметры и формат хранения — то, что нельзя менять случайно.
/// </summary>
public sealed class Argon2FormatTests
{
    [Fact]
    public void Действующие_параметры_именно_такие()
    {
        // ⚠ Тест-канарейка, и он существует ровно затем, чтобы падать. На Python-версии
        // параметры приезжали из умолчаний библиотеки: `PasswordHasher()` без единого
        // аргумента. Обновление библиотеки молча меняло и стоимость хеширования, и решение
        // «пора ли перехешировать», и ни один тест этих чисел не сторожил.
        //
        // Числа — RFC 9106, SECOND RECOMMENDED. Менять их можно, но не мимоходом: сменa
        // означает перехеширование всем при следующем входе.
        var profile = Argon2Profile.Current;

        profile.MemoryKib.ShouldBe(65_536);
        profile.Iterations.ShouldBe(3);
        profile.Parallelism.ShouldBe(4);
        profile.SaltBytes.ShouldBe(16);
        profile.HashBytes.ShouldBe(32);
    }

    [Fact]
    public void Бессмысленные_параметры_отвергаются_сразу()
    {
        // Границы из RFC 9106 §3.1, там они сформулированы как MUST. Библиотека проверяет
        // не всё — соль не проверяет вовсе, — поэтому проверяем на старте, а не при первом
        // входе.
        Should.Throw<ArgumentOutOfRangeException>(() => new Argon2Profile(1024, 0, 1).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new Argon2Profile(1024, 1, 0).Validate());

        // Памяти меньше, чем 8×p.
        Should.Throw<ArgumentOutOfRangeException>(() => new Argon2Profile(16, 1, 4).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new Argon2Profile(1024, 1, 1, SaltBytes: 4).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new Argon2Profile(1024, 1, 1, HashBytes: 8).Validate());
    }

    [Fact]
    public void Небывало_дорогие_параметры_отвергаются_как_данные_а_не_считаются()
    {
        // ⚠ Профиль собирается и из PHC-строки колонки пароля — его числа это ДАННЫЕ.
        // Прежняя редакция ставила только нижние границы: строка `m=2147483647` проходила
        // проверку, и вход этого человека честно пытался выделить два терабайта — падение
        // процесса по памяти от одной испорченной или подменённой записи. Красно-зелёная:
        // на редакции без верхних границ ни один из трёх бросков не происходит.
        Should.Throw<ArgumentOutOfRangeException>(() => new Argon2Profile(int.MaxValue, 3, 4).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new Argon2Profile(65_536, int.MaxValue, 4).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new Argon2Profile(65_536, 3, 1024).Validate());

        // Границы включительны: потолок — годный профиль, боевой — тем более.
        new Argon2Profile(
            Argon2Profile.MaxMemoryKib, Argon2Profile.MaxIterations, Argon2Profile.MaxParallelism).Validate();
    }

    [Fact]
    public void Слабее_значит_слабее_хоть_по_одному_измерению()
    {
        var current = Argon2Profile.Current;

        new Argon2Profile(1024, 3, 4).IsWeakerThan(current).ShouldBeTrue();
        new Argon2Profile(65_536, 1, 4).IsWeakerThan(current).ShouldBeTrue();
        new Argon2Profile(65_536, 3, 1).IsWeakerThan(current).ShouldBeTrue();

        current.IsWeakerThan(current).ShouldBeFalse();
        new Argon2Profile(131_072, 4, 8).IsWeakerThan(current).ShouldBeFalse();
    }

    // --- формат PHC --------------------------------------------------------------------

    [Fact]
    public void Строка_собирается_в_общепринятом_виде()
    {
        // Тот же формат, что у argon2-cffi и libsodium. Прочитать эту колонку однажды может
        // понадобиться не нам — например, при переносе на другую реализацию.
        var hash = new PasswordHash(Argon2Profile.Current, new byte[16], new byte[32]);

        PhcString.Encode(hash).ShouldStartWith("$argon2id$v=19$m=65536,t=3,p=4$");
    }

    [Fact]
    public void База64_пишется_без_выравнивания()
    {
        // ⚠ Требование стандарта: «the padding `=` signs are omitted». Оставленный хвост
        // сделает строку нечитаемой для любой другой реализации.
        var encoded = PhcString.Encode(new PasswordHash(Argon2Profile.Current, new byte[16], new byte[32]));

        // Проверяются именно части с солью и хешем: знаки равенства в `m=/t=/p=` — часть
        // формата, и первая редакция этого теста запрещала их заодно. Проверка, написанная
        // шире, чем свойство, краснеет на верном коде.
        encoded.Split('$')[4].ShouldNotEndWith("=");
        encoded.Split('$')[5].ShouldNotEndWith("=");
    }

    [Fact]
    public void Строка_разбирается_обратно_в_то_же_самое()
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var raw = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        var source = new PasswordHash(Argon2Profile.Current, salt, raw);

        PhcString.TryDecode(PhcString.Encode(source), out var back).ShouldBeTrue();

        back!.Salt.ShouldBe(salt);
        back.Hash.ShouldBe(raw);
        back.Profile.ShouldBe(Argon2Profile.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не строка вовсе")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=4$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=16$m=65536,t=3,p=4$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$t=3,m=65536,p=4$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=4$не-база64$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=4,t=3,p=4$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=2147483647,t=3,p=4$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Мусор_и_чужие_варианты_отвергаются(string? encoded)
    {
        // Разбор не бросает исключений ни на чём: испорченная строка обязана превратиться в
        // обычный отказ во входе, а не в пятисотку. Отдельно отвергается argon2i (другой
        // вариант), другая версия алгоритма, переставленные параметры и памяти меньше 8×p.
        PhcString.TryDecode(encoded, out var hash).ShouldBeFalse();
        hash.ShouldBeNull();
    }

    [Fact]
    public void Эталонная_строка_чужой_реализации_разбирается()
    {
        // Посчитана argon2-cffi 25.1.0 из Python-версии этого же репозитория.
        const string Encoded =
            "$argon2id$v=19$m=65536,t=3,p=4$OqNhfqDe58TyiqrXgzEdzA$A/GI9IwKX/oUkqVsOApNmp4USyUbHHcCAkSKMcypLZ0";

        PhcString.TryDecode(Encoded, out var hash).ShouldBeTrue();

        hash!.Profile.ShouldBe(Argon2Profile.Current);
        hash.Salt.Length.ShouldBe(16);
        hash.Hash.Length.ShouldBe(32);

        // И обратно — байт в байт: собранная нами строка совпадает с чужой.
        PhcString.Encode(hash).ShouldBe(Encoded);
    }
}
