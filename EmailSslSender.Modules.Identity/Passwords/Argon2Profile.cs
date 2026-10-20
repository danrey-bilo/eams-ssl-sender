namespace EmailSslSender.Modules.Identity.Passwords;

/// <summary>
/// Параметры argon2id: во что обходится один хеш.
/// </summary>
/// <remarks>
/// <b>Задаются явно и проверяются тестом-канарейкой.</b> На Python-версии хешер создавался
/// вызовом <c>PasswordHasher()</c> без единого аргумента, то есть параметры приезжали из
/// умолчаний библиотеки. Обновление библиотеки молча меняло и стоимость хеширования, и
/// решение «пора ли перехешировать», а ни один тест этих чисел не сторожил. Здесь числа в
/// коде, и тест падает, если они изменились не нарочно.
///
/// <b>Единицы измерения памяти названы в имени свойства.</b> У библиотеки свойство зовётся
/// <c>MemorySize</c> и молчаливо означает килобайты; ошибка «указал байты» не бросает
/// исключения, а просто даёт слабый хеш. Здесь спутать нечем.
/// </remarks>
/// <param name="MemoryKib">Сколько памяти занимает вычисление, в КиБ. Это <b>вся</b> память, а не на полосу.</param>
/// <param name="Iterations">Сколько проходов по памяти.</param>
/// <param name="Parallelism">Сколько полос, они же потоки.</param>
/// <param name="SaltBytes">Длина соли.</param>
/// <param name="HashBytes">Длина самого хеша.</param>
internal sealed record Argon2Profile(
    int MemoryKib,
    int Iterations,
    int Parallelism,
    int SaltBytes = 16,
    int HashBytes = 32)
{
    /// <summary>
    /// Действующие параметры.
    /// </summary>
    /// <remarks>
    /// <b>RFC 9106, SECOND RECOMMENDED</b> — дословно: «If much less memory is available, a
    /// uniformly safe option is Argon2id with t=3 iterations, p=4 lanes, m=2^(16) (64 MiB
    /// of RAM), 128-bit salt, and 256-bit tag size.»
    ///
    /// Почему не FIRST RECOMMENDED (2 ГиБ, t=1): два гигабайта на каждый вход означают, что
    /// десяток одновременных попыток съедает двадцать гигабайт. Для сервера, который должен
    /// оставаться отзывчивым, это не защита, а способ его уронить.
    ///
    /// Почему не минимум OWASP (19 МиБ, t=2, p=1): он вчетверо дешевле для атакующего, а
    /// экономит нам единицы миллисекунд на вход. Разрыв между источниками здесь стократный —
    /// RFC считает стойкость одного вызова, OWASP считает бюджет нагруженного веб-сервера.
    /// Наш случай ближе к первому: входов у корпоративного менеджера паролей десятки в день,
    /// а не тысячи в секунду.
    ///
    /// Те же числа стоят в Python-версии (умолчания argon2-cffi = профиль
    /// <c>RFC_9106_LOW_MEMORY</c>), так что хеши двух реализаций взаимно проверяемы. Это не
    /// цель, а приятное следствие: переносить базу неоткуда, боевой установки не было.
    ///
    /// ⚠ <b>Менять эти числа можно ровно один раз в жизни каждой установки</b>, и стоит это
    /// перехеширования всем при следующем входе. Поэтому они записываются <b>рядом с
    /// хешем</b>, а не читаются из конфигурации на лету: иначе смена настройки сделала бы
    /// нечитаемыми все прежние хеши разом.
    /// </remarks>
    public static Argon2Profile Current { get; } = new(MemoryKib: 65_536, Iterations: 3, Parallelism: 4);

    /// <summary>
    /// Дешёвый профиль — <b>только для тестов</b>, которым нужен сам факт хеширования.
    /// </summary>
    /// <remarks>
    /// ⚠ Никогда не для боя. Заведён затем, что тесты, проверяющие разбор строки, границы
    /// длины и правила перехеширования, не должны платить по тридцать миллисекунд за каждый
    /// вызов. Проверки, где важна стойкость, берут <see cref="Current"/>.
    ///
    /// На Python-версии эту задачу решили иначе и лучше: пользователь в тестах по умолчанию
    /// заводится <b>без пароля</b>, а вход проверяют единицы тестов на боевых параметрах.
    /// Приём стоит перенести на этап 6 — тогда дешёвый профиль понадобится реже.
    /// </remarks>
    public static Argon2Profile Cheap { get; } = new(MemoryKib: 64, Iterations: 1, Parallelism: 1);

    /// <summary>
    /// Проверить, что числа вообще осмысленны.
    /// </summary>
    /// <remarks>
    /// Границы — из RFC 9106 §3.1, там они сформулированы как <c>MUST</c>: память не меньше
    /// <c>8 × p</c> КиБ, проходов хотя бы один, полос хотя бы одна, тег не короче четырёх
    /// байт. Библиотека проверяет не всё (соль не проверяет вовсе), поэтому проверяем сами
    /// и на старте, а не при первом входе.
    /// </remarks>
    public void Validate()
    {
        if (Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism), Parallelism, "Нужна хотя бы одна полоса");
        }

        if (Iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Iterations), Iterations, "Нужен хотя бы один проход");
        }

        if (MemoryKib < 8 * Parallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemoryKib), MemoryKib, $"Памяти должно быть не меньше 8×p = {8 * Parallelism} КиБ");
        }

        if (SaltBytes < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(SaltBytes), SaltBytes, "Соль короче восьми байт бессмысленна");
        }

        if (HashBytes < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(HashBytes), HashBytes, "Хеш короче шестнадцати байт бессмысленен");
        }

        // ⚠ ⚠ Верхние границы — не педантизм, а защита процесса от данных из базы. Профиль
        // собирается и из PHC-строки колонки пароля (`PhcString.Parse`), то есть его числа —
        // это данные, а не конфигурация: строка `m=2147483647` проходила прежнюю проверку, и
        // `VerifyAsync` честно пытался выделить два терабайта при входе этого человека —
        // падение процесса по памяти от одной испорченной (или подменённой) записи. Пределы
        // взяты с запасом от боевого профиля (64 МиБ, t=3, p=4): в 64 раза по памяти и
        // на порядок по остальным. Найдено ревью (этап 23.1).
        //
        // мутация 2026-08-31: подменил границу памяти на int.MaxValue → покраснели
        // Небывало_дорогие_параметры и строка m=2147483647 в Мусор_и_чужие_варианты.
        if (MemoryKib > MaxMemoryKib)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemoryKib), MemoryKib, $"Памяти больше {MaxMemoryKib} КиБ (4 ГиБ) не бывает у честной строки");
        }

        if (Iterations > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Iterations), Iterations, $"Проходов больше {MaxIterations} не бывает у честной строки");
        }

        if (Parallelism > MaxParallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Parallelism), Parallelism, $"Полос больше {MaxParallelism} не бывает у честной строки");
        }
    }

    /// <summary>Потолок памяти: 4 ГиБ. В 64 раза больше боевого профиля.</summary>
    public const int MaxMemoryKib = 4 * 1024 * 1024;

    /// <summary>Потолок проходов.</summary>
    public const int MaxIterations = 64;

    /// <summary>Потолок полос.</summary>
    public const int MaxParallelism = 64;

    /// <summary>
    /// Слабее ли этот профиль, чем <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Именно «слабее», а не «отличается».</b> Библиотека Python-версии определяет
    /// необходимость перехеширования как <c>self._parameters != extract_parameters(hash)</c>
    /// — любое расхождение, в том числе в сторону <b>более сильных</b> параметров. Следствие
    /// неочевидное и скверное: администратор снизил стоимость ради нагрузки — и каждый вход
    /// молча перехеширует пароль в более слабую сторону, без единого предупреждения.
    ///
    /// Здесь перехеш происходит только вверх. Понизили параметры — старые сильные хеши
    /// остаются сильными и продолжают проверяться: строка несёт свои параметры с собой.
    /// </remarks>
    public bool IsWeakerThan(Argon2Profile other) =>
        MemoryKib < other.MemoryKib
        || Iterations < other.Iterations
        || Parallelism < other.Parallelism
        || HashBytes < other.HashBytes
        || SaltBytes < other.SaltBytes;
}
