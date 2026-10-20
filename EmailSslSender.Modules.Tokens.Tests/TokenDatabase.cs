using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Modules.Tokens.Tokens;
using EmailSslSender.Modules.Identity;
using EmailSslSender.Modules.KeyManager;
using EmailSslSender.Modules.Mail;
using EmailSslSender.Modules.Settings;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace EmailSslSender.Modules.Tokens.Tests;

/// <summary>
/// Настоящий PostgreSQL и собранный модуль над ним.
/// </summary>
/// <remarks>
/// <b>Своя обвязка, а не общая с соседями</b>, и это не дублирование ради дублирования:
/// общий тестовый проект на все модули означал бы одну сборку с доступом к нутру всех сразу
/// (правило 1, и тесты — как раз то место, где срезают углы). Каждый модуль видит только
/// свои внутренности.
///
/// Схема накатывается <b>миграциями хоста</b>, а не собирается по модели. Разница
/// принципиальная: <c>EnsureCreated</c> даёт <b>другую</b> схему, чем та, что уезжает в
/// бой, — и расхождение обнаруживается не тестом, а пользователем. Здесь тестовая база
/// совпадает с боевой, включая частичные уникальные индексы и двустороннее ограничение на
/// список прав.
///
/// ⚠ <b>Подключаются ВСЕ модули хоста, хотя проверяется один.</b> Миграции хоста собраны по
/// модели всех; подключи меньше — и EF остановит прогон словами «модель содержит
/// неприменённые изменения». Это верное поведение: тестовая база обязана совпадать с боевой
/// целиком, а не наполовину. Плюс вход заводит токены через <c>SignInService</c> соседа —
/// то есть проверяется тот же путь, что бежит в бою.
///
/// ⚠ Журнал подменён <see cref="RecordingAudit"/>: модулю нужен интерфейс записи, а не
/// модуль журнала. Проверять, что событие записано, удобнее списком в памяти, чем запросом
/// по таблице.
/// </remarks>
public sealed class TokenDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("sender")
        .WithUsername("sender")
        .WithPassword("тест-не-секрет")
        .Build();

    private ServiceProvider? _services;

    /// <summary>Собранный модуль.</summary>
    public IServiceProvider Services => _services ?? throw new InvalidOperationException("Обвязка не поднята");


    /// <summary>Кто пришёл с запросом — задаёт тест.</summary>
    public TestActor Actor { get; } = new();

    /// <summary>Что записалось в журнал.</summary>
    public RecordingAudit Audit { get; } = new();

    /// <summary>Настройки, которыми управляет тест.</summary>
    public TestSettings Settings { get; } = new();


    private readonly byte[] _masterKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUser>(_ => Actor);

        // Конверт и подпись даёт ХОСТ: мастер-ключ его, а не модуля. Ключ здесь свой на
        // прогон и заведомо одноразовый.
        services.AddSingleton<EmailSslSender.Shared.Crypto.ISecretEnvelope>(_ => new EmailSslSender.Shared.Crypto.SecretEnvelope(
            EmailSslSender.Shared.Crypto.MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = _masterKey })));

        services.AddSingleton<EmailSslSender.Shared.Crypto.IIntegrityProtector>(
            _ => new EmailSslSender.Shared.Crypto.IntegrityProtector(
                EmailSslSender.Shared.Crypto.MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = _masterKey })));

        services.AddDbContext<SenderDbContext>(
            options => options.UseNpgsql(
                _postgres.GetConnectionString(),
                npgsql => npgsql
                    // ⚠ Повтор при обрыве включён ТАК ЖЕ, как у хоста, и это не косметика.
                    // Он запрещает транзакции, начатые вручную, — а модуль их начинает.
                    // Обвязка, настроенная не как бой, проверяет не то приложение.
                    .EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(2), null)
                    .MigrationsAssembly(typeof(DatabaseStartup).Assembly.FullName)),
            optionsLifetime: ServiceLifetime.Singleton);

        services.AddControllers()
            .AddIdentityModule()
            .AddTokensModule()
            .AddSettingsModule()
            .AddMailModule()
            // Таблицы модуля — ради совпадения модели с миграциями хоста; опрос не поднимается.
            .AddKeyManagerModule();

        // ⚠ ПОСЛЕ модулей, и порядок здесь несущий. С этапа 16.5 модуль настроек регистрирует
        // НАСТОЯЩИЙ читатель (база → конфигурация → умолчание), и он выигрывает у подделки,
        // если стоит позже. В этой обвязке базы настроек нет и конфигурации нет — сборка
        // читателя падает `Unable to resolve IConfiguration`, и падает у ВСЕХ тестов сьюта
        // сразу. Подделка обязана стоять последней.
        services.AddSingleton<ISettingsReader>(Settings);


        // ПОСЛЕ модулей: журнал подменяется на список в памяти, а побеждает последняя
        // регистрация. Поставь эту строку выше — и проверялся бы настоящий писатель.
        services.AddSingleton<IAuditWriter>(Audit);

        _services = services.BuildServiceProvider();

        await using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<SenderDbContext>().Database.MigrateAsync();
    }


    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    /// <summary>Область запроса: у каждого действия своя, как у настоящего обращения.</summary>
    public AsyncServiceScope Scope() => Services.CreateAsyncScope();

    /// <summary>Служба токенов в своей области.</summary>
    internal async Task<T> InTokensAsync<T>(Func<TokenService, Task<T>> action)
    {
        await using var scope = Scope();

        return await action(scope.ServiceProvider.GetRequiredService<TokenService>());
    }

    /// <summary>Прочитать что-нибудь из базы своей областью.</summary>
    internal async Task<T> ReadAsync<T>(Func<SenderDbContext, Task<T>> action)
    {
        await using var scope = Scope();

        return await action(scope.ServiceProvider.GetRequiredService<SenderDbContext>());
    }

}

/// <summary>Кто действует. Тест меняет это между проверками.</summary>
public sealed class TestActor : ICurrentUser
{
    public Guid? Id { get; set; } = Guid.CreateVersion7();

    public AuditActorType ActorType { get; set; } = AuditActorType.User;

    public Guid? SessionId { get; set; }

    /// <summary>Стать этим человеком с этой строкой выдачи.</summary>
    public TestActor Become(Guid id, Guid? token = null)
    {
        Id = id;
        SessionId = token;

        return this;
    }

    /// <summary>Стать администратором вне сессии — им заводятся люди в подготовке теста.</summary>
    public TestActor AsSuperuser()
    {
        SessionId = null;

        return this;
    }
}

/// <summary>Журнал списком в памяти.</summary>
public sealed class RecordingAudit : IAuditWriter
{
    private readonly List<AuditEntry> _entries = [];

    /// <summary>Что записано. Порядок сохранён.</summary>
    public IReadOnlyList<AuditEntry> Entries => _entries;

    public void Write(AuditEntry entry) => _entries.Add(entry);

    /// <summary>Записи про одну строку выдачи.</summary>
    /// <remarks>
    /// ⚠ Отбор по своему объекту обязателен: база и журнал-список одни на прогон, и
    /// утверждение «записей ровно две» врало бы, пока рядом лежат чужие.
    /// </remarks>
    public IReadOnlyList<AuditEntry> About(Guid tokenId) =>
        [.. _entries.Where(entry => entry.ResourceId == tokenId)];

    /// <summary>Забыть записанное — между проверками база одна на прогон.</summary>
    public void Clear() => _entries.Clear();
}

/// <summary>Настройки, которыми управляет тест. Не заданные берутся из объявления ключа.</summary>
public sealed class TestSettings : ISettingsReader
{
    /// <summary>Что подменено. Ключ — имя настройки.</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Что сделать перед тем, как отдать настройку. Ключ — имя настройки.
    /// </summary>
    /// <remarks>
    /// ⚠ Нужно гонкам, которые без него случаются «иногда»: чтение настройки стоит в коде между
    /// двумя шагами, и задержка на нём открывает окно между ними наверняка (ревью 2026-09-15, S2,
    /// refresh-1 — одновременные обмены одной половиной).
    /// </remarks>
    public Dictionary<string, Func<Task>> Перед { get; } = new(StringComparer.Ordinal);

    public async ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Перед.TryGetValue(key.Name, out var перед))
        {
            await перед();
        }

        return Values.TryGetValue(key.Name, out var value) && value is T typed ? typed : key.Default;
    }
}


/// <summary>
/// Коллекция тестов, работающих с поднятой базой.
/// </summary>
/// <remarks>
/// Коллекция, а не <c>IClassFixture</c> у каждого класса: второе поднимало бы свой
/// PostgreSQL на каждый файл. Плюс тесты правят общую личность, а внутри коллекции они идут
/// по очереди.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class TokenDatabaseCollection : ICollectionFixture<TokenDatabase>
{
    public const string Name = "token-database";
}
