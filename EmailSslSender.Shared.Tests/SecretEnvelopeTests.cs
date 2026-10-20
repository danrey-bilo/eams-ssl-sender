using System.Security.Cryptography;
using System.Text;
using EmailSslSender.Shared.Crypto;

namespace EmailSslSender.Shared.Tests;

/// <summary>
/// Конверт секретов: то, ради чего он существует, и то, что он обязан отвергать.
/// </summary>
/// <remarks>
/// Самая дорогая по последствиям проверка в библиотеке: ошибка здесь либо теряет данные
/// навсегда, либо отдаёт их тому, кто не должен был их получить.
/// </remarks>
public sealed class SecretEnvelopeTests
{
    [Fact]
    public void Секрет_открывается_тем_же_ключом_и_контекстом()
    {
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        var secret = envelope.Seal(key, Seed, Context);

        envelope.Open(key, secret, Context).ShouldBe(Seed);
    }

    [Fact]
    public void Открытого_текста_в_запечатанном_нет()
    {
        // Проверка на «а точно ли мы шифруем». Звучит наивно ровно до того дня, когда
        // кто-нибудь заменит вызов на заглушку и все остальные тесты останутся зелёными.
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        var secret = envelope.Seal(key, Seed, Context);

        Contains(secret.Bytes, Seed).ShouldBeFalse();
    }

    [Fact]
    public void Чужой_мастер_ключ_не_открывает()
    {
        var mine = Envelope();
        var key = mine.CreateDataKey();
        var secret = mine.Seal(key, Seed, Context);

        // Другая установка: тот же формат, другой мастер-ключ той же версии.
        var stranger = Envelope();

        var отказ = Should.Throw<CryptographicException>(() => stranger.Open(key, secret, Context));

        // ⚠ ⚠ ОТКАЗ ОБЯЗАН НАЗЫВАТЬ ПРИЧИНУ, а не отделываться «не сошёлся тег». Разница между
        // «поднято с другим мастер-ключом» и «данные испорчены» — это разница между «поправь
        // переменную» и «зови безопасность и восстанавливай из копии»; одно сообщение на два
        // случая означает, что половину раз будут делать не то. На живой проверке 2026-08-28
        // голое исключение криптографии стоило получаса разбора не в ту сторону.
        отказ.Message.ShouldContain("мастер-ключ", Case.Insensitive);
        отказ.Message.ShouldContain("EAMS_MASTER_KEK", Case.Sensitive);

        // ⚠ И НИ ОДНОГО БАЙТА КЛЮЧА в тексте (правило 14): сообщение уезжает в лог и в тикет.
        отказ.Message.ShouldNotContain(Convert.ToBase64String(Seed), Case.Sensitive);
    }

    [Fact]
    public void Чужой_ключ_данных_не_открывает()
    {
        // ⚠ Ради этого свойства ключ данных заводится на каждое хранилище. Компрометация
        // одного ключа стоит одного хранилища, а не всей установки.
        var envelope = Envelope();
        var mine = envelope.CreateDataKey();
        var other = envelope.CreateDataKey();

        var secret = envelope.Seal(mine, Seed, Context);

        Should.Throw<CryptographicException>(() => envelope.Open(other, secret, Context));
    }

    [Fact]
    public void Секрет_привязан_к_месту()
    {
        // ⚠ ГЛАВНАЯ проверка этапа, и её нельзя добавить потом. Тот, у кого есть запись в
        // базу, но нет ключей, переставляет чужой шифротекст в свою строку и получает секрет
        // в открытом виде обычным показом. Привязка к месту уходит в связанные данные AEAD и
        // делает такой перенос бесполезным.
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        var secret = envelope.Seal(key, Seed, "user:0199-aaaa:totp_seed");

        Should.Throw<CryptographicException>(
            () => envelope.Open(key, secret, "user:0199-bbbb:totp_seed"));

        // И соседнее поле той же строки — тоже чужое место.
        Should.Throw<CryptographicException>(
            () => envelope.Open(key, secret, "user:0199-aaaa:recovery_codes"));
    }

    [Fact]
    public void Подмена_любого_байта_ломает_расшифровку()
    {
        var envelope = Envelope();
        var key = envelope.CreateDataKey();
        var secret = envelope.Seal(key, Seed, Context);

        // Каждый байт по очереди: заголовок, nonce, тег, шифротекст. Ни один не должен
        // оказаться незащищённым — заголовок в том числе, он входит в связанные данные.
        for (var index = 0; index < secret.Bytes.Length; index++)
        {
            var damaged = secret.Bytes.ToArray();
            damaged[index] ^= 0x01;

            Should.Throw<CryptographicException>(
                () => envelope.Open(key, new SealedSecret(damaged), Context),
                $"байт {index} остался незащищённым");
        }
    }

    [Fact]
    public void Обёртка_и_секрет_не_путаются()
    {
        // Оба — массивы байтов из одной таблицы, и перепутать их местами при рефакторинге
        // легко. Разводят их сразу три вещи: разные ключи (обёртку шифрует мастер-ключ,
        // секрет — ключ данных), разные заголовки и разные домены в связанных данных.
        // Здесь проверяется итог; сами домены — отдельным тестом, потому что косвенно они
        // не проверяются ничем (найдено мутацией).
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        Should.Throw<CryptographicException>(
            () => envelope.Open(key, new SealedSecret(key.Bytes), Context));
    }

    [Fact]
    public void Nonce_не_повторяется()
    {
        // ⚠ Повтор пары «ключ, nonce» в GCM — это не «чуть слабее». Он выдаёт исключающее
        // ИЛИ двух открытых текстов и позволяет подделывать теги. Одинаковый вход шифруется
        // здесь десять тысяч раз одним ключом — ровно тот случай, где счётчик или nonce от
        // содержимого дал бы совпадение.
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        var nonces = new HashSet<string>(StringComparer.Ordinal);
        var blobs = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 10_000; i++)
        {
            var secret = envelope.Seal(key, Seed, Context);

            // Раскладка: формат(1) + поколение(4) + nonce(12) + тег(16) + шифротекст.
            nonces.Add(Convert.ToHexString(secret.Bytes.AsSpan(5, 12))).ShouldBeTrue("nonce повторился");
            blobs.Add(Convert.ToHexString(secret.Bytes)).ShouldBeTrue("шифротекст повторился");
        }
    }

    [Fact]
    public void Ключ_данных_каждый_раз_новый()
    {
        var envelope = Envelope();

        Convert.ToHexString(envelope.CreateDataKey().Bytes)
            .ShouldNotBe(Convert.ToHexString(envelope.CreateDataKey().Bytes));
    }

    // --- ротация мастер-ключа ---------------------------------------------------------------

    [Fact]
    public void Ротация_мастер_ключа_не_трогает_данные()
    {
        // ⚠ Свойство, ради которого конверт двухуровневый. Смена мастер-ключа обязана
        // трогать только обёртки ключей — их столько, сколько хранилищ. Перешифруй она сами
        // секреты, ротацию не делали бы никогда: это часы работы и окно, в котором часть
        // данных уже новая, а часть ещё старая.
        var first = Key();
        var old = new SecretEnvelope(MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = first }));

        var key = old.CreateDataKey();
        var secret = old.Seal(key, Seed, Context);

        var rotated = new SecretEnvelope(MasterKeyRing.FromKeys(
            new Dictionary<int, byte[]> { [1] = first, [2] = Key() }));

        rotated.NeedsRewrap(key).ShouldBeTrue();

        // До перезаворачивания старая обёртка ещё работает — иначе ротация была бы
        // мгновенной остановкой системы.
        rotated.Open(key, secret, Context).ShouldBe(Seed);

        var moved = rotated.Rewrap(key);

        rotated.NeedsRewrap(moved).ShouldBeFalse();

        // И главное: ТОТ ЖЕ запечатанный секрет открывается новой обёрткой. Ни один байт
        // данных не переписан.
        rotated.Open(moved, secret, Context).ShouldBe(Seed);
    }

    [Fact]
    public void Потерянный_мастер_ключ_называет_версию()
    {
        var old = new SecretEnvelope(MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = Key() }));
        var key = old.CreateDataKey();

        // Установка, где первую версию выбросили, не дождавшись конца ротации.
        var truncated = new SecretEnvelope(MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [2] = Key() }));

        var error = Should.Throw<CryptographicException>(() => truncated.Rewrap(key));

        // Разница между «ключ потерян» и «данные испорчены» — это разница между «ищите
        // резервную копию ключа» и «зовите безопасность». Сообщение обязано её называть.
        error.Message.ShouldContain("версии 1");
    }

    [Fact]
    public void Поколение_ключа_данных_не_путается()
    {
        // Перевыпуск ключа хранилища оставляет старые секреты на прежнем поколении, пока их
        // не перешифруют. Открывать их новым ключом нельзя, и отказ должен это объяснять.
        var envelope = Envelope();
        var older = envelope.CreateDataKey();
        var newer = envelope.CreateDataKey(generation: 2);

        var secret = envelope.Seal(older, Seed, Context);

        var error = Should.Throw<CryptographicException>(() => envelope.Open(newer, secret, Context));

        error.Message.ShouldContain("поколением ключа 1");
    }

    // --- границы ------------------------------------------------------------------------------

    [Fact]
    public void Пустой_секрет_переживает_круг()
    {
        // Ноль байт — законное значение (пустая заметка), и на нём ломается арифметика
        // смещений, если её писали «от конца».
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        envelope.Open(key, envelope.Seal(key, [], Context), Context).ShouldBeEmpty();
    }

    [Fact]
    public void Обрезанное_значение_отвергается_понятно()
    {
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        // Пустая колонка — самый частый случай: секрета нет, а не секрет испорчен.
        Should.Throw<CryptographicException>(() => envelope.Open(key, new SealedSecret([]), Context));
        Should.Throw<CryptographicException>(() => envelope.Open(key, new SealedSecret([1, 2, 3]), Context));
    }

    [Fact]
    public void Текст_переживает_круг_вместе_с_юникодом()
    {
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        const string secret = "Пароль от МикроТика №7 — «ёжик», 🦔";

        envelope.OpenText(key, envelope.SealText(key, secret, Context), Context).ShouldBe(secret);
    }

    [Fact]
    public void Пустое_место_не_принимается()
    {
        // Контекст без содержимого означал бы «секрет ни к чему не привязан» — то есть
        // отключённую защиту от переноса. Такое должно падать, а не тихо работать.
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        Should.Throw<ArgumentException>(() => envelope.Seal(key, Seed, "  "));
    }

    [Fact]
    public void Ничего_секретного_не_печатается()
    {
        // ⚠ Условие завершения этапа: секрет не появляется в логах. Печать объекта —
        // самый короткий путь туда: одна строка `log.LogInformation("{Key}", key)`.
        var master = Key();
        var ring = MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = master });
        var envelope = new SecretEnvelope(ring);

        var key = envelope.CreateDataKey();
        var secret = envelope.Seal(key, Seed, Context);

        var printed = $"{ring} {key} {secret}";

        printed.ShouldNotContain(Convert.ToBase64String(master));
        printed.ShouldNotContain(Convert.ToHexString(master));
        printed.ShouldNotContain(Convert.ToBase64String(secret.Bytes));
        printed.ShouldNotContain(Convert.ToBase64String(key.Bytes));

        // И при этом печать остаётся полезной: сколько версий и какая действует — не секрет.
        printed.ShouldContain("действующая: 1");
    }

    [Fact]
    public void Раскладка_байтов_именно_такая()
    {
        // ⚠ Тест-канарейка, и он существует затем, чтобы падать. Ни одна другая проверка
        // здесь не заметит укороченный тег: AES-GCM допускает тег от 12 байт, обрезая его,
        // и система с 12-байтовым тегом работает полностью — просто подделать его в 65 тысяч
        // раз дешевле. Менять эти числа можно, но не мимоходом: смена делает нечитаемым всё
        // уже записанное.
        var envelope = Envelope();
        var key = envelope.CreateDataKey();

        // формат(1) + версия мастер-ключа(4) + поколение(4) + nonce(12) + тег(16) + ключ(32)
        key.Bytes.Length.ShouldBe(69);

        // формат(1) + поколение(4) + nonce(12) + тег(16) + шифротекст
        envelope.Seal(key, [], Context).Bytes.Length.ShouldBe(33);
        envelope.Seal(key, Seed, Context).Bytes.Length.ShouldBe(33 + Seed.Length);

        // Версия формата и версия мастер-ключа читаются с известных мест: их разбирает
        // ротация, и молча съехавший заголовок она заметит только на живых данных.
        key.Bytes[0].ShouldBe((byte)1);
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(key.Bytes.AsSpan(1, 4)).ShouldBe(1);
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(key.Bytes.AsSpan(5, 4)).ShouldBe(1);
    }

    [Fact]
    public void Связанные_данные_разводят_домены_и_держат_заголовок()
    {
        // ⚠ Написан по следу двух мутаций, прошедших зелёными: «сделать домены одинаковыми»
        // и «не класть заголовок в связанные данные». Обе защиты проверялись только
        // косвенно — через отказ, который наступал по другой причине (разные ключи; явная
        // проверка формата и поколения). То есть сломай их — и никто бы не заметил, пока
        // причина, случайно спасавшая, не исчезла бы сама.
        ReadOnlySpan<byte> header = [1, 0, 0, 0, 1];

        var asWrap = Hex(SecretEnvelope.Associated(SecretEnvelope.WrapDomain, header, null));
        var asSeal = Hex(SecretEnvelope.Associated(SecretEnvelope.SealDomain, header, null));

        asWrap.ShouldNotBe(asSeal, "обёртку и секрет должны разводить сами связанные данные");

        // Заголовок входит целиком: версию мастер-ключа и поколение нельзя переписать в
        // базе, не сломав проверку целостности.
        Hex(SecretEnvelope.Associated(SecretEnvelope.SealDomain, [1, 0, 0, 0, 2], null))
            .ShouldNotBe(asSeal, "поколение из заголовка не защищено");

        Hex(SecretEnvelope.Associated(SecretEnvelope.SealDomain, [2, 0, 0, 0, 1], null))
            .ShouldNotBe(asSeal, "версия формата из заголовка не защищена");

        // И привязка к месту — тоже часть связанных данных, а не отдельная проверка.
        Hex(SecretEnvelope.Associated(SecretEnvelope.SealDomain, header, "user:a:totp_seed"))
            .ShouldNotBe(Hex(SecretEnvelope.Associated(SecretEnvelope.SealDomain, header, "user:b:totp_seed")));
    }

    // --- обвязка --------------------------------------------------------------------------------

    /// <summary>TOTP-seed — первый секрет, ради которого этот конверт и написан.</summary>
    private static readonly byte[] Seed = Encoding.UTF8.GetBytes("JBSWY3DPEHPK3PXP");

    private const string Context = "user:0199-aaaa:totp_seed";

    private static byte[] Key() => RandomNumberGenerator.GetBytes(MasterKeyRing.KeyBytes);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static SecretEnvelope Envelope() =>
        new(MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = Key() }));

    /// <summary>Встречается ли последовательность внутри другой.</summary>
    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
