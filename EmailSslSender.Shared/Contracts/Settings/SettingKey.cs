namespace EmailSslSender.Shared.Contracts.Settings;

/// <summary>
/// Где живёт значение настройки: одно на установку или своё у каждого человека.
/// </summary>
/// <remarks>
/// Значения <c>Team</c> нет намеренно: настройка команды означала бы разрешение конфликта
/// («человек в двух командах с разными значениями»), а разрешать его некому — правило
/// пришлось бы придумать здесь и повторить в каждом читателе.
/// </remarks>
public enum SettingScope
{
    /// <summary>Одно значение на установку. Правится администратором.</summary>
    System,

    /// <summary>Своё у каждого человека. Правится им самим под <c>/api/me</c>.</summary>
    User,
}

/// <summary>
/// Объявление настройки без её типа — то, что нужно экрану и проверке значения.
/// </summary>
/// <remarks>
/// <b>Нужен потому, что <c>SettingKey&lt;T&gt;</c> с разными <c>T</c> — разные типы.</b>
/// Собрать объявления шести модулей в один список можно было либо через <c>object</c> с
/// последующей рефлексией, либо через общий интерфейс. Рефлексия по <c>typeof(SettingKey&lt;&gt;)</c>
/// работает ровно до первого объявления, обёрнутого во что-нибудь ещё, и падает в рантайме —
/// то есть на экране настроек у пользователя, а не при сборке.
/// </remarks>
public interface ISettingDeclaration
{
    /// <summary>Имя вида <c>audit.retention_months</c>.</summary>
    string Name { get; }

    /// <summary>Название для экрана настроек. По-английски — язык API.</summary>
    string Title { get; }

    /// <summary>Пояснение для экрана настроек. По-английски — язык API.</summary>
    string Description { get; }

    /// <summary>Раздел: обычно имя модуля.</summary>
    string Category { get; }

    /// <summary>Правка меняет поведение защиты.</summary>
    bool IsSecurity { get; }

    /// <summary>Одно на установку или своё у каждого.</summary>
    SettingScope Scope { get; }

    /// <summary>Тип значения — <c>typeof(T)</c>, включая обнуляемость числовых.</summary>
    Type ValueType { get; }

    /// <summary>Что действует, пока не задано. <c>null</c> — законное значение.</summary>
    object? DefaultValue { get; }

    /// <summary>Нижняя граница числа или длины строки. <c>null</c> — не ограничено.</summary>
    double? Minimum { get; }

    /// <summary>Верхняя граница. <c>null</c> — не ограничено.</summary>
    double? Maximum { get; }

    /// <summary>Допустимые значения. Пусто — любое подходящее по типу.</summary>
    IReadOnlyList<string> Choices { get; }

    /// <summary>Принимает ли настройка <c>null</c>.</summary>
    bool IsNullable { get; }

    /// <summary>Правка действует только после перезапуска — экран обязан это сказать.</summary>
    bool RequiresRestart { get; }
}

/// <summary>
/// Объявление настройки: имя, значение по умолчанию и то, что о ней надо знать человеку.
/// </summary>
/// <remarks>
/// <b>Значение по умолчанию живёт в ключе, а не в читателе.</b> Настройка, которой нет в
/// базе, обязана вести себя одинаково у любого читателя — включая тот, что придёт на смену
/// временному.
/// <para>
/// ⚠ <b>Название и пояснение — ПО-АНГЛИЙСКИ.</b> Это язык API, а не один из языков
/// интерфейса: русский и украинский возвращает клиент, картой по имени ключа
/// (<c>eams.web/src/shared/i18n/serverText.ts</c>). Русское название на сервере сделало бы
/// английский интерфейс русским молча — и заметил бы это только тот, кто им пользуется.
/// Разбор — решение 0027.
/// </para>
/// <para>
/// ⚠ <b>Новые позиционные параметры добавляются ТОЛЬКО в конец.</b> Вставка в середину не
/// сломает сборку у вызовов, передающих аргументы позиционно, — она молча переставит им
/// смысл. Сегодня так вызывают все 38 объявлений шести модулей.
/// </para>
/// </remarks>
/// <param name="Name">Имя вида <c>audit.retention_months</c>.</param>
/// <param name="Default">Что действует, пока не задано.</param>
/// <param name="Title">Название для экрана настроек.</param>
/// <param name="Description">Пояснение для экрана настроек.</param>
/// <param name="Category">Раздел: обычно имя модуля.</param>
/// <param name="IsSecurity">
/// Правка меняет поведение защиты. Такие настройки показываются отдельно и правятся с
/// подтверждением: «включить запись разрешённых проверок» и «удалять журнал через N
/// месяцев» — не то, что меняют мимоходом.
/// </param>
/// <param name="Scope">Одно значение на установку или своё у каждого человека.</param>
/// <param name="Minimum">Нижняя граница числа или длины строки.</param>
/// <param name="Maximum">Верхняя граница.</param>
/// <param name="Choices">Допустимые значения; пусто — любое подходящее по типу.</param>
/// <param name="Nullable">
/// Принимает ли настройка <c>null</c>. По умолчанию выводится: обнуляемое число или
/// умолчание <c>null</c>. Задаётся явно там, где вывод неверен, — см. замечание к
/// <see cref="IsNullable"/>.
/// </param>
/// <param name="RequiresRestart">Правка действует только после перезапуска.</param>
public sealed record SettingKey<T>(
    string Name,
    T Default,
    string Title,
    string Description,
    string Category,
    bool IsSecurity = false,
    SettingScope Scope = SettingScope.System,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? Choices = null,
    bool? Nullable = null,
    bool RequiresRestart = false) : ISettingDeclaration
{
    /// <summary>
    /// Допустимые значения — своей копией, а не переданным массивом.
    /// </summary>
    /// <remarks>
    /// ⚠ Объявление живёт синглтоном на процесс, а массив изменяем: один потребитель,
    /// отсортировавший его «для показа», поменял бы порядок всем и навсегда.
    /// </remarks>
    public IReadOnlyList<string> Choices { get; } = Choices is null ? [] : [.. Choices];

    /// <inheritdoc />
    public Type ValueType => typeof(T);

    /// <inheritdoc />
    public object? DefaultValue => Default;

    /// <summary>
    /// Принимает ли настройка <c>null</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Для ссылочных типов это НЕ выводится, и вывод здесь — догадка по умолчанию.</b>
    /// <c>SettingKey&lt;string&gt;</c> и <c>SettingKey&lt;string?&gt;</c> — один и тот же тип в
    /// рантайме: обнуляемость ссылки живёт в атрибутах компилятора, а не в
    /// <see cref="Type"/>, и <c>Nullable.GetUnderlyingType</c> про строку не знает ничего.
    /// <para>
    /// Поэтому правило: обнуляемое число или умолчание <c>null</c> — <c>true</c>; всё
    /// остальное — <c>false</c>, и несовпадающий случай (строка с непустым умолчанием,
    /// принимающая <c>null</c>) задаётся параметром явно. Ошибка здесь — это
    /// <c>nullable: false</c> у веба там, где <c>null</c> законен: кнопка «очистить» не
    /// появится, и настройку станет нельзя вернуть в неопределённое состояние.
    /// </para>
    /// </remarks>
    // мутация 2026-08-29: заменил вывод на `Nullable ?? false` → покраснели
    // SettingDeclarationTests.Обнуляемое_число_объявляет_себя_обнуляемым и
    // .Строка_с_умолчанием_null_считается_обнуляемой (правило 9).
    public bool IsNullable { get; } =
        Nullable ?? (System.Nullable.GetUnderlyingType(typeof(T)) is not null || Default is null);
}

/// <summary>Чтение действующего значения настройки.</summary>
public interface ISettingsReader
{
    /// <summary>Значение настройки или её умолчание.</summary>
    ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Модуль объявляет свои настройки.
/// </summary>
/// <remarks>
/// Точка расширения того же рода, что таблицы и письма модулей: справочник настроек собирает
/// объявленное со всех модулей и не знает заранее, что там будет.
/// </remarks>
public interface ISettingDeclarationProvider
{
    /// <summary>Настройки этого модуля — для экрана и для проверки допустимых значений.</summary>
    IReadOnlyList<ISettingDeclaration> Declarations { get; }
}
