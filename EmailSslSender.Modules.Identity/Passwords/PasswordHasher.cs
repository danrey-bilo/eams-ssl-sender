using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace EmailSslSender.Modules.Identity.Passwords;

/// <summary>
/// argon2id поверх Konscious.
/// </summary>
/// <remarks>
/// Библиотека даёт ровно одно: посчитать argon2 от байтов. Ни формата хранения, ни
/// проверки, ни затирания памяти в ней нет — всё это здесь.
///
/// <b>Экземпляр без состояния и годится синглтоном.</b> Сам объект <c>Argon2id</c>
/// переиспользовать нельзя и незачем: пароль привязывается к нему конструктором и не
/// меняется, а соль у каждого своя. Поэтому новый объект на каждый вызов — как в примерах
/// самой библиотеки.
/// </remarks>
internal sealed class PasswordHasher : IPasswordHasher, IDisposable
{
    /// <summary>
    /// Предел длины пароля в байтах UTF-8.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Не косметика.</b> На Python-версии верхней границы нет вообще: все поля пароля
    /// объявлены голым типом без ограничения длины, и единственный потолок — размер тела
    /// запроса в nginx, шестнадцать мегабайт. Принимать мегабайтную строку в заведомо
    /// дорогую функцию на <b>неаутентифицированном</b> эндпоинте не следует: сама по себе
    /// длина в argon2 стоит немного, но обнулять её незачем, когда границу поставить
    /// бесплатно.
    ///
    /// Тысяча двадцать четыре байта — это парольная фраза из полутора сотен слов. Кто
    /// уткнётся в этот предел, тот не пароль вводит.
    /// </remarks>
    public const int MaxPasswordBytes = 1024;

    private readonly Argon2Profile _profile;
    private readonly SemaphoreSlim _gate;

    /// <summary>
    /// Соль для холостой проверки. Постоянная нарочно — она ничего не защищает.
    /// </summary>
    private static readonly byte[] DummySalt = Encoding.UTF8.GetBytes("sender-timing-salt");

    /// <param name="profile">Параметры. По умолчанию — действующие.</param>
    /// <param name="maxConcurrency">
    /// Сколько хешей считается одновременно.
    /// <para>
    /// ⚠ Ограничение есть, потому что <b>каждый хеш занимает свои 64 МиБ</b>. Полсотни
    /// одновременных попыток входа — три гигабайта, и это на эндпоинте, куда пускают без
    /// пароля. Очередь превращает исчерпание памяти в ожидание, а это несравнимо меньшая
    /// беда. Ограничение частоты входа стоит выше и отсекает такое раньше, но полагаться на
    /// один слой там, где второй стоит пятнадцать строк, незачем.
    /// </para>
    /// </param>
    public PasswordHasher(Argon2Profile? profile = null, int? maxConcurrency = null)
    {
        _profile = profile ?? Argon2Profile.Current;
        _profile.Validate();

        _gate = new SemaphoreSlim(maxConcurrency ?? Math.Max(2, Environment.ProcessorCount));
    }

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

        // ⚠ Пустой пароль отсекается ЗДЕСЬ, и это не формальность. Библиотека на пустом
        // массиве бросает ArgumentException("Argon2 needs a password set") — то есть форма
        // входа с пустым полем отвечала бы пятисоткой вместо отказа. Поймано тестом.
        if (password.Length == 0)
        {
            throw new ArgumentException("Пустой пароль хешировать нельзя", nameof(password));
        }

        var bytes = Encoding.UTF8.GetBytes(password);

        if (bytes.Length > MaxPasswordBytes)
        {
            // Затираем и здесь: пароль уже в памяти, а до `finally` дело не дойдёт.
            CryptographicOperations.ZeroMemory(bytes);

            throw new ArgumentOutOfRangeException(
                nameof(password), "Пароль длиннее допустимого");
        }

        var salt = RandomNumberGenerator.GetBytes(_profile.SaltBytes);

        try
        {
            var hash = await ComputeAsync(bytes, salt, _profile, cancellationToken);

            return PhcString.Encode(new PasswordHash(_profile, salt, hash));
        }
        finally
        {
            // Библиотека держит ССЫЛКУ на этот массив и не обнуляет его никогда — ни в
            // Dispose (он не переопределён и пуст), ни после счёта. Затирать обязан
            // вызывающий, то есть мы.
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<bool> VerifyAsync(
        string? encoded, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!PhcString.TryDecode(encoded, out var stored) || stored is null)
        {
            // Мусор в колонке — обычный отказ. Но время всё равно тратим: иначе строка
            // «пароля нет» отвечала бы мгновенно и выдавала бы такие учётные записи.
            await WasteVerificationTimeAsync(cancellationToken);

            return false;
        }

        // Пустой пароль — обычный отказ, а не исключение: пустое поле в форме входа
        // случается постоянно. Время всё равно тратим, чтобы не отвечать мгновенно.
        if (password.Length == 0)
        {
            await WasteVerificationTimeAsync(cancellationToken);

            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(password);

        if (bytes.Length > MaxPasswordBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);

            // Такого пароля мы бы не сохранили, значит совпасть он не может. Считать
            // argon2 незачем — и это не утечка по времени: слишком длинный пароль ни у кого
            // не заведён, а значит различать по времени нечего.
            return false;
        }

        try
        {
            // ⚠ Параметры берутся ИЗ СТРОКИ, а не текущие. Иначе смена параметров разом
            // сделала бы неверными все прежние пароли — и починить это было бы уже нечем,
            // потому что открытых паролей ни у кого нет.
            var actual = await ComputeAsync(bytes, stored.Salt, stored.Profile, cancellationToken);

            // Постоянное время: обычное сравнение массивов выходит на первом же
            // несовпавшем байте и тем самым рассказывает, сколько байтов угадано.
            return CryptographicOperations.FixedTimeEquals(actual, stored.Hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool NeedsRehash(string? encoded)
    {
        if (!PhcString.TryDecode(encoded, out var stored) || stored is null)
        {
            // Строку, которую не удалось разобрать, пересчитывать бессмысленно: проверка по
            // ней всё равно не пройдёт, и открытого пароля на руках не окажется.
            return false;
        }

        return stored.Profile.IsWeakerThan(_profile);
    }

    public async Task WasteVerificationTimeAsync(CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes("этот пароль не подходит никому");

        try
        {
            // Ровно одна работа argon2 нынешними параметрами — столько же, сколько стоит
            // настоящая проверка. Заранее посчитанного хеша здесь нет намеренно: он жил бы
            // константой и разошёлся бы с параметрами при первой же их правке, а вместе с
            // ним разошлось бы и время ответа.
            var burned = await ComputeAsync(bytes, DummySalt, _profile, cancellationToken);

            CryptographicOperations.ZeroMemory(burned);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Собственно счёт, под ограничением одновременности.</summary>
    private async Task<byte[]> ComputeAsync(
        byte[] password, byte[] salt, Argon2Profile profile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            using var argon2 = new Argon2id(password)
            {
                Salt = salt,
                MemorySize = profile.MemoryKib,
                Iterations = profile.Iterations,
                DegreeOfParallelism = profile.Parallelism,
            };

            // Асинхронная перегрузка, а не GetBytes: у синхронной внутри
            // `Task.Run(...).Result`, то есть она занимает поток пула и ждёт на нём же —
            // ровно то, чего в конвейере ASP.NET быть не должно.
            return await argon2.GetBytesAsync(profile.HashBytes);
        }
        finally
        {
            _gate.Release();
        }
    }
}
