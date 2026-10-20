using System.Diagnostics;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// argon2id: хеширование, проверка, перехеш.
/// </summary>
/// <remarks>
/// Часть проверок идёт на <b>боевых</b> параметрах и потому небыстра — по три десятка
/// миллисекунд на вызов. Так и должно быть: подменять стойкость ради скорости тестов
/// значит проверять не тот код, который бежит в бою. Дешёвый профиль берут только те
/// проверки, где важна логика, а не стойкость.
/// </remarks>
public sealed class PasswordHasherTests
{
    [Fact]
    public async Task Один_и_тот_же_пароль_даёт_разные_строки()
    {
        // Соль новая на каждый хеш. Одинаковые строки означали бы, что соли нет, и тогда
        // одна радужная таблица вскрывала бы всех разом.
        var hasher = Cheap();

        var первый = await hasher.HashAsync("пароль", Ct);
        var второй = await hasher.HashAsync("пароль", Ct);

        первый.ShouldNotBe(второй);
        (await hasher.VerifyAsync(первый, "пароль", Ct)).ShouldBeTrue();
        (await hasher.VerifyAsync(второй, "пароль", Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Неверный_пароль_не_проходит()
    {
        var hasher = Cheap();
        var hash = await hasher.HashAsync("пароль", Ct);

        (await hasher.VerifyAsync(hash, "паролЬ", Ct)).ShouldBeFalse();
        (await hasher.VerifyAsync(hash, "пароль ", Ct)).ShouldBeFalse();
        (await hasher.VerifyAsync(hash, "", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Хеш_не_содержит_пароля_ни_в_каком_виде()
    {
        // Очевидное утверждение, которое стоит проверить один раз: строка уезжает в базу, в
        // дампы и в бэкапы, и если пароль просочится туда, узнают об этом не от нас.
        var hasher = Cheap();

        var hash = await hasher.HashAsync("совершенно-секретный-пароль", Ct);

        hash.ShouldNotContain("совершенно");
        hash.ShouldNotContain(Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("совершенно-секретный-пароль")).TrimEnd('='));
    }

    [Fact]
    public async Task Мусор_в_колонке_это_обычный_отказ_а_не_исключение()
    {
        // ⚠ Иначе вход отвечает пятисоткой вместо отказа — то есть рассказывает, что с этой
        // учётной записью что-то не так, и заодно роняет обработчик.
        var hasher = Cheap();

        (await hasher.VerifyAsync("не-хеш-вовсе", "пароль", Ct)).ShouldBeFalse();
        (await hasher.VerifyAsync("$argon2id$v=19$сломано", "пароль", Ct)).ShouldBeFalse();
        (await hasher.VerifyAsync("", "пароль", Ct)).ShouldBeFalse();
        (await hasher.VerifyAsync(null, "пароль", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Слишком_длинный_пароль_не_принимается()
    {
        // Дорогая функция на эндпоинте, куда пускают без пароля. Граница ставится бесплатно,
        // а на Python-версии её нет вовсе — единственный потолок там размер тела в nginx,
        // шестнадцать мегабайт.
        var hasher = Cheap();
        var длинный = new string('ы', PasswordHasher.MaxPasswordBytes);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => hasher.HashAsync(длинный, Ct));
    }

    [Fact]
    public async Task Слишком_длинный_пароль_на_проверке_просто_не_подходит()
    {
        // На проверке — отказ, а не исключение: такой пароль никому не заведён, значит
        // совпасть не может, и различать по времени тут нечего.
        var hasher = Cheap();
        var hash = await hasher.HashAsync("пароль", Ct);

        (await hasher.VerifyAsync(hash, new string('ы', PasswordHasher.MaxPasswordBytes), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Пароль_на_границе_длины_принимается()
    {
        // Граница проверяется в БАЙТАХ UTF-8, а не в символах: кириллическая буква весит
        // два байта, и «1024 символа» было бы вдвое больше обещанного.
        var hasher = Cheap();
        var ровно = new string('a', PasswordHasher.MaxPasswordBytes);

        var hash = await hasher.HashAsync(ровно, Ct);

        (await hasher.VerifyAsync(hash, ровно, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Пустой_пароль_хешировать_нельзя()
    {
        // ⚠ Найдено тестом: библиотека на пустом массиве бросает
        // ArgumentException("Argon2 needs a password set"). Без своей проверки форма входа
        // с пустым полем отвечала бы пятисоткой вместо отказа — то есть роняла бы
        // обработчик и заодно рассказывала, что поле дошло до хеширования.
        var hasher = Cheap();

        var error = await Should.ThrowAsync<ArgumentException>(() => hasher.HashAsync("", Ct));

        // Сверяем ТЕКСТ, а не только тип: библиотека бросает ArgumentException тем же
        // типом, и проверка на один лишь тип проходила бы и без нашей защиты — мутация это
        // и показала. Своё сообщение отличает понятный отказ от падения нутра наружу.
        error.Message.ShouldContain("Пустой пароль");
    }

    [Fact]
    public async Task Пустой_пароль_на_проверке_это_отказ_а_не_исключение()
    {
        // Пустое поле в форме входа случается постоянно, и это обычный отказ.
        var hasher = Cheap();
        var hash = await hasher.HashAsync("пароль", Ct);

        (await hasher.VerifyAsync(hash, "", Ct)).ShouldBeFalse();
    }

    // --- перехеширование ------------------------------------------------------------

    [Fact]
    public async Task Слабый_хеш_помечается_к_пересчёту()
    {
        var слабый = await Cheap().HashAsync("пароль", Ct);

        new PasswordHasher(Argon2Profile.Current).NeedsRehash(слабый).ShouldBeTrue();
    }

    [Fact]
    public async Task Свежий_хеш_пересчёта_не_требует()
    {
        var hasher = Cheap();
        var hash = await hasher.HashAsync("пароль", Ct);

        hasher.NeedsRehash(hash).ShouldBeFalse();
    }

    [Fact]
    public async Task Более_сильный_хеш_НЕ_пересчитывается_в_слабую_сторону()
    {
        // ⚠ Дефект Python-версии, перенесённый как урок. Там «пора перехешировать»
        // определяется НЕРАВЕНСТВОМ параметров, а не сравнением стойкости. Следствие:
        // администратор снизил стоимость ради нагрузки — и каждый следующий вход молча
        // пересчитывает пароль в более слабую сторону, без единого предупреждения.
        var сильный = await new PasswordHasher(Argon2Profile.Current).HashAsync("пароль", Ct);

        Cheap().NeedsRehash(сильный).ShouldBeFalse();
    }

    [Fact]
    public void Нечитаемая_строка_к_пересчёту_не_помечается()
    {
        // Пересчитывать нечего: проверка по ней всё равно не пройдёт, а открытого пароля на
        // руках не окажется.
        Cheap().NeedsRehash("мусор").ShouldBeFalse();
        Cheap().NeedsRehash(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Проверка_идёт_параметрами_из_строки_а_не_текущими()
    {
        // ⚠ Свойство, ради которого параметры лежат рядом с хешем. Считай мы проверку
        // текущими параметрами — смена параметров разом сделала бы неверными все прежние
        // пароли, и починить это было бы уже нечем: открытых паролей ни у кого нет.
        var старый = await Cheap().HashAsync("пароль", Ct);

        var новый = new PasswordHasher(Argon2Profile.Current);

        (await новый.VerifyAsync(старый, "пароль", Ct)).ShouldBeTrue();
        новый.NeedsRehash(старый).ShouldBeTrue();
    }

    // --- совместимость и время --------------------------------------------------------

    [Theory]
    [InlineData(
        "правильный-пароль-42",
        "$argon2id$v=19$m=65536,t=3,p=4$OqNhfqDe58TyiqrXgzEdzA$A/GI9IwKX/oUkqVsOApNmp4USyUbHHcCAkSKMcypLZ0")]
    [InlineData(
        "a",
        "$argon2id$v=19$m=65536,t=3,p=4$ODiBcY37kk34rhEqW4XThg$w5l8p1Ffcrlp24xnA+IGWPUnIH3Lycva/KIChkZmJWY")]
    public async Task Хеш_чужой_реализации_проверяется_нашей(string password, string encoded)
    {
        // ⚠ Эталонные векторы, посчитанные argon2-cffi 25.1.0 из Python-версии этого же
        // репозитория боевыми параметрами. Проверяют разом две вещи: что наш разбор строки
        // PHC совпадает с общепринятым и что сам счёт даёт те же байты, что признанная
        // реализация. Своя реализация формата, проверенная только собой, доказывает лишь
        // внутреннюю согласованность.
        var hasher = new PasswordHasher(Argon2Profile.Current);

        (await hasher.VerifyAsync(encoded, password, Ct)).ShouldBeTrue();
        (await hasher.VerifyAsync(encoded, password + "!", Ct)).ShouldBeFalse();
        hasher.NeedsRehash(encoded).ShouldBeFalse();
    }

    [Fact]
    public async Task Отказ_по_отсутствующему_хешу_стоит_столько_же_сколько_настоящая_проверка()
    {
        // ⚠ Иначе форма входа становится оракулом: ответ на несуществующий адрес
        // возвращается заметно быстрее, чем на существующий с неверным паролем, и правило
        // «вход не различает эти два случая» держится на тексте ошибки, а не на поведении.
        //
        // Сравниваются медианы, а не одиночные замеры, и на боевых параметрах: под нагрузкой
        // замедляется и то и другое, поэтому отношение устойчивее абсолютного времени.
        // Порог намеренно щедрый — тест ловит «отвечает мгновенно», а не разницу в проценты.
        var hasher = new PasswordHasher(Argon2Profile.Current);
        var hash = await hasher.HashAsync("пароль", Ct);

        var настоящая = await MedianAsync(() => hasher.VerifyAsync(hash, "не тот пароль", Ct));
        var холостая = await MedianAsync(() => hasher.VerifyAsync(null, "не тот пароль", Ct));

        холостая.ShouldBeGreaterThan(
            настоящая * 0.3,
            $"холостой отказ занял {холостая:F1} мс против {настоящая:F1} мс у настоящей проверки");
    }

    [Fact]
    public async Task Одновременные_вызовы_не_мешают_друг_другу()
    {
        // Ограничитель одновременности стоит внутри хешера. Проверяем, что он именно
        // ограничивает, а не ломает: двадцать параллельных хешей должны дать двадцать
        // разных строк, и каждая — рабочая.
        var hasher = Cheap();

        var hashes = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => hasher.HashAsync($"пароль-{i}", Ct)));

        hashes.Distinct().Count().ShouldBe(20);

        var checks = await Task.WhenAll(
            hashes.Select((h, i) => hasher.VerifyAsync(h, $"пароль-{i}", Ct)));

        checks.ShouldAllBe(ok => ok);
    }

    // --- обвязка ----------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PasswordHasher Cheap() => new(Argon2Profile.Cheap);

    /// <summary>Медиана трёх замеров в миллисекундах.</summary>
    private static async Task<double> MedianAsync(Func<Task> action)
    {
        var times = new List<double>();

        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            await action();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();

        return times[1];
    }
}
