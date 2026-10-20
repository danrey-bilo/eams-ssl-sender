using EmailSslSender.Modules.Settings.Catalog;
using EmailSslSender.Modules.Settings.Storage;
using EmailSslSender.Modules.Settings.Values;
using EmailSslSender.Shared.Contracts.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Settings.Reading;

/// <summary>Откуда взялось действующее значение настройки.</summary>
/// <remarks>
/// ⚠ <b>Три значения, а не два, и третье завёл не педантизм.</b> Python сливает файл и
/// константу в одно слово <c>default</c>. Последствие видно на живом администраторе: строка
/// задана в базе, он правит конфигурацию, перезапускает — и ничего не меняется, <b>без
/// объяснения</b>, потому что экран говорит «умолчание» в обоих случаях. Разделив источники,
/// экран может сказать «задано в базе, файл не действует».
/// </remarks>
internal enum SettingsSource
{
    /// <summary>Строка в <c>system_settings</c>.</summary>
    System,

    /// <summary>Раздел <c>Settings:</c> конфигурации — файл, окружение, секрет.</summary>
    Configuration,

    /// <summary>Умолчание из объявления ключа.</summary>
    Default,
}

/// <summary>Действующее значение и его источник.</summary>
internal readonly record struct SettingValue(object? Value, SettingsSource Source);

/// <summary>
/// Настоящий читатель настроек: база → конфигурация → умолчание.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Снимок таблицы делается ОДИН РАЗ на область запроса.</b> Настройки читаются на самом
/// горячем пути системы: одна проверка пароля читает <b>пять</b> ключей политики подряд, вход —
/// ещё пороги блокировки и сроки сессий. Запрос на ключ
/// умножался бы на это число, а не прибавлялся к нему. Проверяется это счётчиком команд, а не
/// глазами: «одна область — один SELECT».
///
/// ⚠ <b>Время жизни — <c>Scoped</c>, и это не кэш.</b> Снимок живёт ровно один запрос: правка
/// настройки действует со следующего, а не «когда-нибудь». Настоящий кэш — 16.9, и он
/// заводится <b>только по замеру</b> (правило 18).
///
/// ⚠ <b>Негодное значение в базе даёт УМОЛЧАНИЕ И ПРЕДУПРЕЖДЕНИЕ, а не пятисотку.</b> Строка
/// могла стать негодной без чьей-либо ошибки: границу сузили выкаткой, ключ вывели из
/// обращения, значение положили руками через psql. Уронить на этом запрос значит уронить
/// <b>каждый</b> запрос системы из-за одной строки в таблице. Молча взять умолчание — тоже
/// нельзя: администратор будет уверен, что задал порог блокировки, а действует другой.
/// </remarks>
internal sealed class SettingsReader(
    SettingsRepository repository,
    SettingsCatalog catalog,
    IConfiguration configuration,
    ILogger<SettingsReader> log) : ISettingsReader
{
    /// <summary>Раздел конфигурации, где ищутся значения.</summary>
    public const string Section = "Settings";

    private IReadOnlyDictionary<string, string>? _snapshot;

    public async ValueTask<T> GetAsync<T>(
        SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var найдено = await ResolveAsync(key, cancellationToken);

        return Действующее(key, найдено);
    }

    /// <summary>
    /// Значение для кода: заданное — как есть, в том числе заданное пустым.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Заданный <c>null</c> у обнуляемого ключа — это значение, а не «не задано»</b>
    /// (ревью 2026-09-15, settings-1). Прежде здесь стояло <c>Value is T ? … : key.Default</c>, а
    /// шаблон типа с <c>null</c> не совпадает никогда. Экран показывал «пусто», а код получал
    /// умолчание: очищенный <c>server.public_url</c> читался как <c>https://sender.example.com</c>, и
    /// письма приглашений уходили с мёртвыми ссылками.
    ///
    /// ⚠ <c>default(T)</c>, а не приведение <c>(T)null!</c>: у необнуляемого типа-значения,
    /// объявленного с <c>Nullable: true</c>, приведение бросило бы. Такого объявления сегодня нет,
    /// но и падать всему, что читает настройку, из-за одного объявления незачем.
    /// </remarks>
    internal static T Действующее<T>(SettingKey<T> key, SettingValue найдено)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (найдено.Value is T значение)
        {
            return значение;
        }

        // мутация 2026-09-15: `key.IsNullable` → `!key.IsNullable` → покраснели
        // ReaderTests.Очищенная_строка_с_непустым_умолчанием_читается_пустой и
        // ReaderTests.Очищенный_предел_с_непустым_умолчанием_читается_безлимитом.
        return найдено.Value is null && найдено.Source != SettingsSource.Default && key.IsNullable
            ? default!
            : key.Default;
    }

    /// <summary>Действующее значение вместе с источником — для экрана настроек.</summary>
    public async ValueTask<SettingValue> ResolveAsync(
        ISettingDeclaration declaration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        _snapshot ??= await repository.SystemAsync(cancellationToken);

        if (_snapshot.TryGetValue(declaration.Name, out var json))
        {
            // ⚠ ⚠ Годность спрашивается отдельно от значения (замечание ревью к PR #32): у обнуляемого
            // ключа негодная строка и заданный null оба дают null, а действовать должно только второе.
            if (SettingValues.TryRead(declaration, json, out var значение))
            {
                return new SettingValue(значение, SettingsSource.System);
            }

            // ⚠ Громко, а не молча. Строка в базе есть, но действует не она — администратор
            // обязан узнать об этом из лога, а не из расследования «почему порог другой».
            // Само значение в лог НЕ уходит: настройка не секрет, но привычка печатать
            // содержимое базы в лог однажды напечатает не то (правило 14).
            log.LogWarning(
                "setting_value_invalid {Setting} {Type}",
                declaration.Name,
                SettingValues.Имя(declaration));
        }

        // ⚠ Конфигурация читается по ПОЛНОМУ имени ключа внутри раздела: `Settings:audit.retention_months`.
        // Так же читал временный читатель, и менять это на вложенные секции нельзя — установки,
        // задавшие настройку файлом, потеряли бы её молча при выкатке.
        var изФайла = configuration.GetSection(Section).GetSection(declaration.Name);

        if (изФайла.Value is not null)
        {
            var разобрано = FromConfiguration(declaration, изФайла.Value);

            if (разобрано is not null)
            {
                return new SettingValue(разобрано, SettingsSource.Configuration);
            }

            log.LogWarning(
                "setting_value_invalid_configuration {Setting} {Type}",
                declaration.Name,
                SettingValues.Имя(declaration));
        }

        return new SettingValue(declaration.DefaultValue, SettingsSource.Default);
    }

    /// <summary>
    /// Значение из конфигурации: там всё строки.
    /// </summary>
    /// <remarks>
    /// ⚠ Конфигурация не знает типов — <c>"12"</c>, <c>"true"</c>, <c>"dark"</c> приходят
    /// строками, — поэтому разбор здесь свой, а не через <see cref="SettingValues"/>: тот ждёт
    /// уже разобранный JSON. Границы и варианты при этом проверяет он же, чтобы правило было
    /// одно: значение из файла обязано проходить ту же проверку, что и присланное экраном.
    /// </remarks>
    private static object? FromConfiguration(ISettingDeclaration declaration, string текст)
    {
        var тип = Nullable.GetUnderlyingType(declaration.ValueType) ?? declaration.ValueType;

        // ⚠ ⚠ Логическое — через `bool.TryParse`, а не как есть (ревью 2026-09-15, settings-2).
        // Поставщик JSON-конфигурации хранит `true` из appsettings.json строкой
        // `JsonElement.ToString()`, то есть «True» с заглавной, а JSON-литерал регистр различает:
        // `"mail.enabled": true` в файле давал предупреждение и умолчание, а тот же ключ
        // переменной окружения со строчным `true` — работал. Нормализуем в «true»/«false», и
        // проверка идёт тем же `SettingValues.Read`, что и у всего прочего.
        var json = тип == typeof(bool)
            ? (bool.TryParse(текст.Trim(), out var логическое) ? (логическое ? "true" : "false") : текст)
            : тип == typeof(int) || тип == typeof(long)
                ? текст
                : System.Text.Json.JsonSerializer.Serialize(текст);

        return SettingValues.Read(declaration, json);
    }

    /// <summary>
    /// Забыть снимок: следующее чтение сходит в базу заново.
    /// </summary>
    /// <remarks>
    /// ⚠ Нужен ровно одному месту — ответу на правку. Снимок области устаревает в тот момент,
    /// когда мы записали, и ответить по нему значило бы показать администратору прежние
    /// значения сразу после «Сохранить». Он нажал бы ещё раз.
    /// </remarks>
    public void Забыть() => _snapshot = null;

    /// <summary>Все объявления вместе с действующими значениями — для экрана настроек.</summary>
    public async Task<IReadOnlyList<(ISettingDeclaration Declaration, SettingValue Value)>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        var итог = new List<(ISettingDeclaration, SettingValue)>(catalog.All.Count);

        foreach (var объявление in catalog.All)
        {
            итог.Add((объявление, await ResolveAsync(объявление, cancellationToken)));
        }

        return итог;
    }
}
