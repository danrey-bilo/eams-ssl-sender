using EmailSslSender.Modules.Settings.Catalog;
using EmailSslSender.Modules.Settings.Reading;
using EmailSslSender.Modules.Settings.Settings;
using EmailSslSender.Modules.Settings.Storage;
using EmailSslSender.Shared.Contracts.Data;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace EmailSslSender.Modules.Settings.Tests;

/// <summary>
/// Настоящий PostgreSQL на прогон и схема модуля над ним.
/// </summary>
/// <remarks>
/// ⚠ <b>Проверяется поведение БАЗЫ, а не кода.</b> Главное свойство схемы настроек —
/// <c>'null'::jsonb</c> отличается от отсутствия строки — на подделке не проверяется вовсе:
/// словарь в памяти не знает разницы между «ключа нет» и «значение null», а вся модель
/// хранения на ней и стоит.
///
/// ⚠ <b>Схема накатывается конфигурацией сущностей через <c>EnsureCreated</c>, а не
/// миграцией</b> — миграция живёт в хосте, а модуль на хост не ссылается. Что миграция
/// прикладывается и создаёт то же самое, проверяют интеграционные тесты.
/// </remarks>
public sealed class SettingsDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("sender")
        .WithUsername("sender_owner")
        .WithPassword("тест-не-секрет")
        .Build();

    private ServiceProvider? _services;

    /// <summary>Собранный контейнер служб.</summary>
    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("Обвязка не поднята");

    /// <summary>
    /// Часы, которые двигает тест.
    /// </summary>
    /// <remarks>
    /// ⚠ Без них не проверить порядок правок: <c>updated_at</c> двух записей подряд на живых
    /// часах различается на микросекунды, и утверждение «вторая правка позже первой» зеленело
    /// бы даже там, где время вообще не обновляют.
    /// </remarks>
    public ПодвижныеЧасы Часы { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Сколько команд ушло в базу. Им проверяется цена чтения, а не намерение.</summary>
    public СчётчикКоманд Команды { get; } = new();

    /// <summary>Что уехало в лог. Им проверяется предупреждение о негодном значении.</summary>
    public ЗаписнойЖурнал Логи { get; } = new();

    /// <summary>
    /// Раздел `Settings:` конфигурации. Задаёт тест — это средний слой разрешения.
    /// </summary>
    /// <remarks>
    /// Словарь правится между тестами, а конфигурация строится поверх него: пересобирать
    /// контейнер ради одного значения дороже, чем контейнер PostgreSQL, который он поднимает.
    /// </remarks>
    public Dictionary<string, string?> Файл { get; } = new(StringComparer.Ordinal);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();

        services.AddLogging(строитель => строитель.AddProvider(Логи));
        services.AddSingleton<IEntityConfigurationProvider, SettingsEntities>();
        services.AddDbContext<SenderDbContext>(
            options => options.UseNpgsql(_postgres.GetConnectionString()).AddInterceptors(Команды));
        services.AddSingleton<TimeProvider>(Часы);
        services.AddScoped<SettingsRepository>();

        // Справочник и читатель — как в хосте. Свои `ui.*` в справочнике есть: без них не
        // проверить, что читатель видит объявления модуля, а не только чужие.
        services.AddSingleton<ISettingDeclarationProvider, SettingsSettingDeclarations>();
        services.AddSingleton<SettingsCatalog>();
        // ⚠ ПЕРЕХОДЯЩАЯ, а не синглтон: `AddInMemoryCollection` копирует словарь в момент
        // сборки, и конфигурация-синглтон навсегда запомнила бы пустой раздел — тест,
        // задающий значение файла, проверял бы умолчание и зеленел бы, ничего не проверив.
        // Стоило четырёх красных фактов при первом прогоне.
        services.AddTransient<IConfiguration>(
            _ => new ConfigurationBuilder().AddInMemoryCollection(Файл).Build());
        services.AddScoped<SettingsReader>();
        services.AddScoped<ISettingsReader>(sp => sp.GetRequiredService<SettingsReader>());

        _services = services.BuildServiceProvider();

        await using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<SenderDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    /// <summary>Область запроса: у каждого теста своя.</summary>
    public AsyncServiceScope Scope() => Services.CreateAsyncScope();
}

/// <summary>Контейнер один на коллекцию: две таблицы не стоят контейнера на файл.</summary>
[CollectionDefinition(Name)]
public sealed class SettingsDatabaseCollection : ICollectionFixture<SettingsDatabase>
{
    public const string Name = "settings-database";
}

/// <summary>Часы, которые двигает тест.</summary>
public sealed class ПодвижныеЧасы(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _теперь = start;

    public override DateTimeOffset GetUtcNow() => _теперь;

    /// <summary>Сдвинуть время вперёд.</summary>
    public void Вперёд(TimeSpan сколько) => _теперь = _теперь.Add(сколько);
}
