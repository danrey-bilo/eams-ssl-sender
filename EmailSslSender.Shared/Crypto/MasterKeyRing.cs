using System.Globalization;
using System.Security.Cryptography;

namespace EmailSslSender.Shared.Crypto;

/// <summary>
/// Мастер-ключи установки: действующий и прежние.
/// </summary>
/// <remarks>
/// <b>Ключей несколько, а не один, и это единственное, что делает ротацию возможной.</b>
/// Смена мастер-ключа означает перезаворачивание всех ключей данных, а их столько, сколько
/// хранилищ и людей в установке. Пока перезаворачивание идёт, в базе лежат ключи, обёрнутые
/// и старым мастер-ключом, и новым. Ринг с единственным ключом заставил бы останавливать
/// систему на время ротации — то есть ротацию не делали бы вовсе.
///
/// ⚠ <b>Мастер-ключ не шифрует данные. Никогда.</b> Он оборачивает ключи данных, и только
/// их. Разница не косметическая: AES-GCM безопасен, пока один ключ не зашифровал слишком
/// много сообщений, а мастер-ключ в системе один на всё. Оборачивая только ключи данных, он
/// делает считанные тысячи операций за жизнь установки вместо миллионов.
///
/// <b>Ключи лежат в памяти процесса в открытом виде</b> — это принятый риск, тот же, что и
/// на Python-версии: расшифровать секрет, не имея ключа, нельзя, а держать его в HSM в
/// self-hosted установке не из чего. Дамп памяти процесса отдаёт всё.
/// </remarks>
public sealed class MasterKeyRing
{
    /// <summary>Длина мастер-ключа. AES-256 — 32 байта, других длин мы не принимаем.</summary>
    /// <remarks>
    /// AES принимает 16, 24 и 32; мы принимаем только 32. Ключ короче не «чуть слабее», он
    /// молча превращает AES-256 в AES-128, и заметить это по работающей системе нельзя.
    /// </remarks>
    public const int KeyBytes = 32;

    private readonly Dictionary<int, byte[]> _keys;

    private MasterKeyRing(Dictionary<int, byte[]> keys, int currentVersion)
    {
        _keys = keys;
        CurrentVersion = currentVersion;
    }

    /// <summary>Версия, которой оборачиваем новое. Наибольшая из имеющихся.</summary>
    public int CurrentVersion { get; }

    /// <summary>Сколько версий в ринге. Больше одной — идёт или не завершена ротация.</summary>
    public int Count => _keys.Count;

    /// <summary>
    /// Разобрать содержимое файла секрета.
    /// </summary>
    /// <remarks>
    /// Формат нарочно совпадает с тем, что уже создаёт <c>scripts/gen-secrets.sh</c>: одна
    /// строка base64 — это версия 1. Заводить второй формат ради .NET значило бы завести и
    /// второй генератор, и второй способ ошибиться.
    ///
    /// Для ротации к тому же файлу дописываются строки вида <c>2:base64</c>; действующей
    /// становится наибольшая версия. Пустые строки и строки с <c>#</c> пропускаются, чтобы
    /// рядом с ключом можно было написать, когда и зачем он появился.
    ///
    /// ⚠ <b>Ни одно сообщение об ошибке не содержит содержимого файла</b> — только номер
    /// строки. Правило 14: секрет не уезжает в текст ошибки, а разобрать «строку 3» ничуть
    /// не сложнее.
    /// </remarks>
    /// <param name="content">Содержимое файла или переменной.</param>
    /// <exception cref="InvalidOperationException">Формат не разобран или ключей нет.</exception>
    public static MasterKeyRing Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var keys = new Dictionary<int, byte[]>();
        var lines = content.Split('\n');

        try
        {
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                var number = index + 1;

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var (version, encoded) = Split(line, number);

                if (!keys.TryAdd(version, Decode(encoded, number, version)))
                {
                    throw new InvalidOperationException(
                        $"Мастер-ключ версии {version} задан дважды (строка {number}). "
                        + "Какой из них действующий — угадывать нельзя.");
                }
            }
        }
        catch
        {
            // ⚠ Отказ на поздней строке не должен оставлять уже разобранные ключи в куче:
            // Decode при неверной длине затирает свой — то есть намерение затирать здесь
            // было, но исполнялось наполовину (этап 23.5). Словарь никто не подберёт, а
            // ключи в нём — настоящие.
            foreach (var осиротевший in keys.Values)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(осиротевший);
            }

            throw;
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                "В источнике мастер-ключа нет ни одного ключа. Пустой файл секрета обычно "
                + "означает, что его забыли создать: ./scripts/gen-secrets.sh");
        }

        return new MasterKeyRing(keys, keys.Keys.Max());
    }

    /// <summary>Собрать ринг из готовых ключей — для тестов и для ротации в коде.</summary>
    /// <exception cref="ArgumentException">Ключей нет или длина не та.</exception>
    public static MasterKeyRing FromKeys(IReadOnlyDictionary<int, byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            throw new ArgumentException("Ринг без ключей бессмыслен", nameof(keys));
        }

        var copy = new Dictionary<int, byte[]>(keys.Count);

        foreach (var (version, key) in keys)
        {
            if (version < 1)
            {
                throw new ArgumentException($"Версия ключа должна быть от 1, а не {version}", nameof(keys));
            }

            if (key.Length != KeyBytes)
            {
                throw new ArgumentException(
                    $"Мастер-ключ версии {version} длиной {key.Length} байт вместо {KeyBytes}", nameof(keys));
            }

            // Копия, а не ссылка: иначе вызывающий сможет затереть ключ у нас под ногами,
            // и обнаружится это расшифровкой, которая перестала работать.
            copy[version] = key.ToArray();
        }

        return new MasterKeyRing(copy, copy.Keys.Max());
    }

    /// <summary>Есть ли такая версия. Нужна конверту, чтобы дать внятный отказ.</summary>
    public bool Has(int version) => _keys.ContainsKey(version);

    /// <summary>
    /// Ключ нужной версии.
    /// </summary>
    /// <remarks>
    /// <c>internal</c>, а не <c>public</c>: наружу мастер-ключ не отдаётся. Единственный, кому
    /// он нужен, — <see cref="SecretEnvelope"/>, и тот живёт в этой же сборке.
    /// </remarks>
    /// <exception cref="CryptographicException">Такой версии в ринге нет.</exception>
    internal ReadOnlySpan<byte> Key(int version) =>
        _keys.TryGetValue(version, out var key)
            ? key
            : throw new CryptographicException(
                $"Ключ данных обёрнут мастер-ключом версии {version}, а его в установке нет. "
                + "Так выглядит потерянный при ротации ключ: без него эти данные не прочитать.");

    /// <summary>
    /// Ни при каких обстоятельствах не печатает ключи.
    /// </summary>
    /// <remarks>
    /// ⚠ Умолчание для класса — имя типа, и оно безобидно; переопределение стоит здесь не
    /// ради него, а ради того, кто однажды добавит сюда <c>record</c> или сериализацию.
    /// Правило 14 проверяется тестом, а не памятью.
    /// </remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"MasterKeyRing(версий: {_keys.Count}, действующая: {CurrentVersion})");

    /// <summary>Разобрать строку в пару «версия, ключ».</summary>
    private static (int Version, string Encoded) Split(string line, int number)
    {
        var colon = line.IndexOf(':');

        if (colon < 0)
        {
            // Голая строка base64 — версия 1. Ровно то, что пишет gen-secrets.sh.
            return (1, line);
        }

        var prefix = line[..colon];

        if (!int.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidOperationException(
                $"Строка {number}: перед двоеточием ожидается номер версии от 1.");
        }

        return (version, line[(colon + 1)..]);
    }

    /// <summary>Раскодировать и проверить длину.</summary>
    private static byte[] Decode(string encoded, int number, int version)
    {
        byte[] key;

        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            // Содержимое строки в сообщение не уезжает — это и есть ключ.
            throw new InvalidOperationException(
                $"Строка {number}: ключ версии {version} не разбирается как base64.");
        }

        if (key.Length != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);

            throw new InvalidOperationException(
                $"Строка {number}: ключ версии {version} длиной {key.Length} байт вместо {KeyBytes}. "
                + "Короче — это молча не AES-256; генератор даёт ровно 32 байта.");
        }

        return key;
    }
}
