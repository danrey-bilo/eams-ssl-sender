using EmailSslSender.Modules.Tokens;
using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Modules.Mail;
using EmailSslSender.Modules.Settings;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Настоящий PostgreSQL и собранный модуль над ним.
/// </summary>
/// <remarks>
/// Схема накатывается <b>миграциями хоста</b>, а не собирается по модели. Разница
/// принципиальная: <c>EnsureCreated</c> и генерация скрипта по модели дают <b>другую</b>
/// схему, чем та, что уезжает в бой, — и расхождение обнаруживается не тестом, а
/// пользователем. Здесь тестовая база совпадает с боевой байт в байт, включая расширение
/// <c>citext</c>, частичный уникальный индекс и ограничение на назначение ссылки.
///
/// Отсюда и ссылка тестового проекта на хост: миграции живут там, потому что модуль не
/// знает провайдера базы. Архитектурный сторож это разрешает — он проверяет проекты
/// модулей, а не их тесты.
///
/// ⚠ Журнал подменён <see cref="RecordingAudit"/>: модулю личностей нужен интерфейс записи,
/// а не модуль журнала. Проверять, что событие записано, удобнее списком в памяти, чем
/// запросом по таблице, — и заодно тесты не зависят от чужого модуля.
/// </remarks>
public sealed class IdentityDatabase : IAsyncLifetime
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

    /// <summary>Подставная очередь писем: что просили поставить и чем отвечать.</summary>
    public БезПочты Почта { get; } = new();

    /// <summary>Язык получателя — задаёт тест.</summary>
    public ЯзыкПоУмолчанию Язык { get; } = new();

    /// <summary>Внешний адрес установки — задаёт тест.</summary>
    public ВнешнийАдрес Адрес { get; } = new();


    /// <summary>Настоящий хешер под наблюдением: тест узнаёт, что творится в базе на холостой проверке.</summary>
    internal СледящийХешер Хешер { get; } = new(new PasswordHasher());

    /// <summary>Строка подключения — тестам, которые смотрят в базу мимо модуля.</summary>
    public string ConnectionString => _postgres.GetConnectionString();


    /// <summary>
    /// Ключ первой версии — тот, которым обвязка запечатывает.
    /// </summary>
    /// <remarks>
    /// Нужен проверке перекладывания обёрток: она строит своё кольцо из этого ключа плюс
    /// второй версии, иначе развернуть запечатанное обвязкой было бы нечем.
    /// </remarks>
    public byte[] MasterKey { get; } = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUser>(_ => Actor);

        // Конверт секретов даёт ХОСТ: мастер-ключ его, а не модуля. Ключ здесь свой на
        // прогон и заведомо одноразовый — подсовывать боевой файл секрета значило бы однажды
        // его и зашифровать.
        services.AddSingleton<EmailSslSender.Shared.Crypto.ISecretEnvelope>(_ => new EmailSslSender.Shared.Crypto.SecretEnvelope(
            EmailSslSender.Shared.Crypto.MasterKeyRing.FromKeys(new Dictionary<int, byte[]>
            {
                [1] = MasterKey,
            })));

        // ⚠ Подпись недоверенных хранилищ — тоже от хоста, и на ТОМ ЖЕ мастер-ключе:
        // отдельный ключ здесь означал бы, что тестовая сборка расходится с боевой в том
        // единственном месте, где расхождение незаметно.
        services.AddSingleton<EmailSslSender.Shared.Crypto.IIntegrityProtector>(
            _ => new EmailSslSender.Shared.Crypto.IntegrityProtector(
                EmailSslSender.Shared.Crypto.MasterKeyRing.FromKeys(new Dictionary<int, byte[]>
                {
                    [1] = MasterKey,
                })));

        services.AddDbContext<SenderDbContext>(
            options => options.UseNpgsql(
                _postgres.GetConnectionString(),
                npgsql => npgsql
                    // ⚠ Повтор при обрыве включён ТАК ЖЕ, как у хоста, и это не косметика.
                    // Он запрещает транзакции, начатые вручную, — а модуль их начинает.
                    // Первая редакция обвязки повтор не включала, тесты модуля были зелёные,
                    // и пятисотку на активации нашёл только интеграционный тест. Обвязка,
                    // настроенная не как бой, проверяет не то приложение.
                    .EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(2), null)
                    .MigrationsAssembly(typeof(DatabaseStartup).Assembly.FullName)),
            optionsLifetime: ServiceLifetime.Singleton);

        // ⚠ Все модули хоста, хотя проверяется один. Причина не в удобстве: схема накатывается
        // миграциями хоста, а те собраны по модели ВСЕХ модулей — подключи один, и EF
        // остановит прогон словами «модель содержит неприменённые изменения». Это верное
        // поведение: тестовая база обязана совпадать с боевой целиком, а не наполовину.
        //
        // Модули подключаются своими точками сборки: копия регистраций разошлась бы с
        // настоящей, и тест проверял бы не то, что бежит.
        services.AddControllers()
            // ⚠ Предел попыток входа поднят: тесты блокировки входят одной почтой десятки раз
            // подряд, и упирались бы в предел раньше, чем в блокировку. Сам предел держат
            // `SignInAttemptsTests` и `IntegrationTests/AuthApi`.
            .AddIdentityModule(options => options.SignInAttemptsPerMinute = 100_000)
            .AddTokensModule()
            .AddSettingsModule()
            .AddMailModule();

        // ⚠ ПОСЛЕ модулей, и порядок здесь несущий. С этапа 16.5 модуль настроек регистрирует
        // НАСТОЯЩИЙ читатель (база → конфигурация → умолчание), и он выигрывает у подделки,
        // если стоит позже. В этой обвязке базы настроек нет и конфигурации нет — сборка
        // читателя падает `Unable to resolve IConfiguration`, и падает у ВСЕХ тестов сьюта
        // сразу. Подделка обязана стоять последней.
        services.AddSingleton<ISettingsReader>(Settings);

        // ⚠ Почта — соседний модуль, и здесь она подставная: проверяется, ЧТО модуль личностей
        // просит поставить письмо, а не как оно уходит.
        services.AddSingleton<IMailQueue>(Почта);
        services.AddSingleton<IUserLocale>(Язык);
        services.AddScoped<IPublicOrigin>(_ => Адрес);


        // ПОСЛЕ модулей: журнал подменяется на список в памяти, а побеждает последняя
        // регистрация. Поставь эту строку выше — и проверялся бы настоящий писатель.
        services.AddSingleton<IAuditWriter>(Audit);


        // Хешер — настоящий, но под наблюдением; ПОСЛЕ модулей по той же причине.
        services.AddSingleton<IPasswordHasher>(Хешер);

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

    /// <summary>Служба учётных записей в своей области.</summary>
    internal async Task<T> InScopeAsync<T>(Func<UserService, Task<T>> action)
    {
        await using var scope = Scope();

        return await action(scope.ServiceProvider.GetRequiredService<UserService>());
    }

    /// <summary>То же, но без результата.</summary>
    internal async Task InScopeAsync(Func<UserService, Task> action)
    {
        await using var scope = Scope();

        await action(scope.ServiceProvider.GetRequiredService<UserService>());
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

    /// <summary>Стать другим администратором — прав в установке нет, все равны.</summary>
    public TestActor AsAdministrator()
    {
        Id = Guid.CreateVersion7();
        SessionId = null;

        return this;
    }

    /// <summary>Какой сессией пришли. Нужно тем действиям, что различают «эту» и «прочие».</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Когда личность подтверждали на этом токене.</summary>
    public DateTimeOffset? StepUpAt { get; set; }

    /// <summary>Стать конкретным человеком — например тем, кто меняет себе пароль.</summary>
    public TestActor Become(Guid id, Guid? session = null)
    {
        Id = id;
        SessionId = session;

        return this;
    }

    /// <summary>Стать администратором вне сессии — им заводятся люди в подготовке теста.</summary>
    public TestActor AsSuperuser()
    {
        Id = Guid.CreateVersion7();
        SessionId = null;

        return this;
    }
}

/// <summary>Журнал в памяти: что записали, то и проверяем.</summary>
public sealed class RecordingAudit : IAuditWriter
{
    private readonly List<AuditEntry> _entries = [];

    /// <summary>Всё, что записано с начала прогона.</summary>
    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public void Write(AuditEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>Записи об этом объекте — фильтр по своему, а не по всей истории прогона.</summary>
    public IReadOnlyList<AuditEntry> About(Guid resourceId) =>
        [.. Entries.Where(entry => entry.ResourceId == resourceId)];
}

/// <summary>Настройки, которыми управляет тест.</summary>
public sealed class TestSettings : ISettingsReader
{
    /// <summary>Заданные значения. Чего нет — берётся из объявления ключа.</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    public ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Values.TryGetValue(key.Name, out var value) ? (T)value! : key.Default);
}

/// <summary>Коллекция: один контейнер на прогон, тесты по очереди.</summary>
[CollectionDefinition(Name)]
public sealed class IdentityDatabaseCollection : ICollectionFixture<IdentityDatabase>
{
    public const string Name = "identity-database";
}


/// <summary>
/// Настоящий хешер, перед холостой проверкой зовущий крючок теста.
/// </summary>
/// <remarks>
/// Холостая проверка — сотни миллисекунд Argon2. Крючок спрашивает базу, что в эту минуту держит
/// служба: проверяется не намерение кода, а соединение, занятое на время хеша.
/// </remarks>
internal sealed class СледящийХешер(IPasswordHasher настоящий) : IPasswordHasher
{
    /// <summary>Что сделать перед холостой проверкой. <c>null</c> — ничего.</summary>
    public Func<Task>? ПередХолостой { get; set; }

    public Task<string> HashAsync(string password, CancellationToken cancellationToken = default) =>
        настоящий.HashAsync(password, cancellationToken);

    public Task<bool> VerifyAsync(string? encoded, string password, CancellationToken cancellationToken = default) =>
        настоящий.VerifyAsync(encoded, password, cancellationToken);

    public bool NeedsRehash(string? encoded) => настоящий.NeedsRehash(encoded);

    public async Task WasteVerificationTimeAsync(CancellationToken cancellationToken = default)
    {
        if (ПередХолостой is { } крючок)
        {
            await крючок();
        }

        await настоящий.WasteVerificationTimeAsync(cancellationToken);
    }
}
