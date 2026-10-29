using EmailSslSender.Modules.KeyManager.Contracts;
using System.Text;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Delivery;
using EmailSslSender.Modules.KeyManager.Export;
using EmailSslSender.Modules.KeyManager.Schema;
using EmailSslSender.Modules.KeyManager.Secrets;
using EmailSslSender.Modules.KeyManager.Storage;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Data;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Core.Errors;
using Modules.Crypto;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace EmailSslSender.Modules.KeyManager.Tests;

/// <summary>Настоящий PostgreSQL (блокировки и <c>ON CONFLICT</c> не подделать); почта и KMP — подставные.</summary>
public sealed class KeyManagerDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("sender")
        .WithUsername("sender_owner")
        .WithPassword("тест-не-секрет")
        .Build();

    private ServiceProvider? _services;

    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("Обвязка не поднята");

    /// <summary>Для второго соединения мимо контекста.</summary>
    public string Connection => _postgres.GetConnectionString();

    public ПодвижныеЧасы Часы { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public ПодставнаяЛичность Личность { get; } = new();

    public ФиктивныеНастройки Настройки { get; } = new();

    public ЗаписнойЖурнал Логи { get; } = new();

    public ЗаписнойАудит Аудит { get; } = new();

    public ПодставнаяОчередь Почта { get; } = new();

    public ПодвижныйКонверт Конверт { get; } = new();

    internal ПодставнойKmp Kmp { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();

        services.AddLogging(строитель => строитель.AddProvider(Логи));
        services.AddSingleton<IEntityConfigurationProvider, KeyManagerTables>();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(_postgres.GetConnectionString())
            .ReplaceService<IModelCacheKeyFactory, AppModelCacheKeyFactory>());
        services.AddSingleton<TimeProvider>(Часы);
        services.AddSingleton<ICurrentUser>(Личность);
        services.AddSingleton<ISecretEnvelope>(Конверт);
        services.AddSingleton<ISettingsReader>(Настройки);
        services.AddSingleton<IAuditWriter>(Аудит);
        services.AddSingleton<IMailQueue>(Почта);
        services.AddSingleton<IKeyManagerConnector>(Kmp);

        services.AddScoped<ApiKeyStore>();
        services.AddScoped<ApiKeyRewrap>();
        services.AddScoped<CertificateSnapshots>();
        services.AddScoped<CertificateDelivery>();
        services.AddScoped<CertificatePolling>();
        services.AddScoped<CertificateExport>();

        _services = services.BuildServiceProvider();

        // Базовой миграцией, а не EnsureCreated: так проверяется и она.
        await KeyManagerSchema.Declaration.MigrateAsync(_postgres.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public AsyncServiceScope Scope() => Services.CreateAsyncScope();

    public void Сброс()
    {
        Настройки.Значения.Clear();
        Логи.Строки.Clear();
        Аудит.Записи.Clear();
        Почта.Сброс();
        Kmp.Сброс();
        Конверт.Вернуть();
    }
}

[CollectionDefinition(Name)]
public sealed class KeyManagerDatabaseCollection : ICollectionFixture<KeyManagerDatabase>
{
    public const string Name = "keymanager-database";
}

public sealed class ПодвижныеЧасы(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _теперь = start;

    public override DateTimeOffset GetUtcNow() => _теперь;

    public void Вперёд(TimeSpan сколько) => _теперь = _теперь.Add(сколько);
}

public sealed class ПодставнаяЛичность : ICurrentUser
{
    public Guid? Id { get; set; } = Guid.CreateVersion7();

    public bool IsSuperuser => true;

    public AuditActorType ActorType { get; set; } = AuditActorType.User;

    public Guid? SessionId { get; set; }
}

/// <summary>Чего тест не задал — умолчание ключа.</summary>
public sealed class ФиктивныеНастройки : ISettingsReader
{
    public Dictionary<string, object?> Значения { get; } = new(StringComparer.Ordinal);

    public ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return ValueTask.FromResult(Значения.TryGetValue(key.Name, out var значение) ? (T)значение! : key.Default);
    }
}

public sealed class ЗаписнойЖурнал : ILoggerProvider
{
    public List<string> Строки { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Перо(Строки);

    public void Dispose()
    {
    }

    private sealed class Перо(List<string> строки) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (строки)
            {
                строки.Add(formatter(state, exception));
            }
        }
    }
}

public sealed class ЗаписнойАудит : IAuditWriter
{
    public List<AuditEntry> Записи { get; } = [];

    public void Write(AuditEntry entry)
    {
        lock (Записи)
        {
            Записи.Add(entry);
        }
    }
}

public sealed record ПринятоеПисьмо(
    string To, string Kind, string Locale, IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, MailTable> Tables, IReadOnlyList<MailAttachment> Attachments);

/// <summary>⚠ Запоминает постановку сразу, не с фиксацией: откат проверять по отметке подписки.</summary>
public sealed class ПодставнаяОчередь : IMailQueue
{
    public List<ПринятоеПисьмо> Письма { get; } = [];

    public List<Guid> Номера { get; } = [];

    /// <summary>Судьба писем по номеру; чего нет — «ждёт».</summary>
    public Dictionary<Guid, MailLetterStatus> Судьбы { get; } = [];

    public MailQueueResult Ответ { get; set; } = MailQueueResult.Queued;

    public void Сброс()
    {
        Письма.Clear();
        Номера.Clear();
        Судьбы.Clear();
        Отменённые.Clear();
        Ответ = MailQueueResult.Queued;
    }

    public Task<bool> CanSendAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Ответ != MailQueueResult.NotConfigured);

    public async Task<MailQueueResult> EnqueueAsync(
        string toAddress, string kind, string locale, IReadOnlyDictionary<string, string> values,
        DateTimeOffset? expiresAt = null, CancellationToken cancellationToken = default) =>
        (await EnqueueAsync(toAddress, kind, locale, values, [], expiresAt, cancellationToken)).Result;

    public Task<MailTicket> EnqueueAsync(
        string toAddress, string kind, string locale, IReadOnlyDictionary<string, string> values,
        IReadOnlyList<MailAttachment> attachments, DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(toAddress, kind, locale, values, new Dictionary<string, MailTable>(), attachments, expiresAt, cancellationToken);

    public Task<MailTicket> EnqueueAsync(
        string toAddress, string kind, string locale, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, MailTable> tables, IReadOnlyList<MailAttachment> attachments,
        DateTimeOffset? expiresAt = null, CancellationToken cancellationToken = default)
    {
        if (Ответ != MailQueueResult.Queued)
        {
            return Task.FromResult(new MailTicket(Ответ, null));
        }

        var номер = Guid.CreateVersion7();

        lock (Письма)
        {
            Письма.Add(new ПринятоеПисьмо(toAddress, kind, locale, values, tables, attachments));
            Номера.Add(номер);
        }

        return Task.FromResult(new MailTicket(MailQueueResult.Queued, номер));
    }

    public Task<IReadOnlyDictionary<Guid, MailLetterStatus>> StatusAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        lock (Письма)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, MailLetterStatus>>(ids
                .Where(Номера.Contains)
                .ToDictionary(
                    номер => номер,
                    номер => Судьбы.GetValueOrDefault(номер)
                        ?? new MailLetterStatus(MailLetterState.Pending, Письма[Номера.IndexOf(номер)].To, null)));
        }
    }

    public List<Guid> Отменённые { get; } = [];

    /// <summary>Как настоящая очередь: ждущее письмо умирает с причиной «Отменено: …».</summary>
    public Task<int> CancelAsync(
        IReadOnlyCollection<Guid> ids, string reason, CancellationToken cancellationToken = default)
    {
        lock (Письма)
        {
            var отменено = 0;

            foreach (var номер in ids.Where(Номера.Contains))
            {
                if (Судьбы.TryGetValue(номер, out var судьба) && судьба.State != MailLetterState.Pending)
                {
                    continue;
                }

                Судьбы[номер] = new MailLetterStatus(
                    MailLetterState.Dead, Письма[Номера.IndexOf(номер)].To, $"Отменено: {reason}");
                Отменённые.Add(номер);
                отменено++;
            }

            return Task.FromResult(отменено);
        }
    }
}

/// <summary>Key Manager Plus в памяти: и соединитель, и клиент.</summary>
internal sealed class ПодставнойKmp : IKeyManagerConnector, IKeyManagerClient
{
    public List<KmpCertificate> Сертификаты { get; } = [];

    public Dictionary<string, string?> Пароли { get; } = new(StringComparer.Ordinal);

    public KeyManagerConfiguration Настройки { get; set; } = Готово;

    public string? ПадатьНаФормате { get; set; }

    /// <summary>Даты выпуска по серийному номеру: для них PEM — настоящий сертификат.</summary>
    public Dictionary<string, DateTimeOffset> Выпуски { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Чем падать при открытии клиента (не открылся ключ API, сбой базы).</summary>
    public Exception? ОткрытиеПадает { get; set; }

    public string? ПадатьНаИмени { get; set; }

    public bool СписокПадает { get; set; }

    /// <summary>Файл по типу вместо «ТИП:серийный»; отдаётся копией — получатель вправе его затереть.</summary>
    public Dictionary<string, byte[]> Файлы { get; } = new(StringComparer.Ordinal);

    public bool ПарольПадает { get; set; }

    public int Выгрузок => _выгрузок;

    public int Списков => _списков;

    /// <summary>Задвижка: пока взведена, выгрузка ждёт.</summary>
    public TaskCompletionSource? Задвижка { get; private set; }

    /// <summary>Для проверок одновременности.</summary>
    public TaskCompletionSource ВыгрузкаНачалась { get; private set; } = Новый();

    private int _выгрузок;
    private int _списков;

    public static KeyManagerConfiguration Готово { get; } = new(true, "https://kmp.example.test:6565", true);

    public void Задержать() => Задвижка = Новый();

    public void Отпустить() => Задвижка?.TrySetResult();

    public void Сброс()
    {
        Сертификаты.Clear();
        Пароли.Clear();
        Файлы.Clear();
        Настройки = Готово;
        ПадатьНаФормате = null;
        ПадатьНаИмени = null;
        ОткрытиеПадает = null;
        Выпуски.Clear();
        СписокПадает = false;
        ПарольПадает = false;
        _выгрузок = 0;
        _списков = 0;
        Отпустить();
        Задвижка = null;
        ВыгрузкаНачалась = Новый();
    }

    public Task<KeyManagerConfiguration> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Настройки);

    /// <summary>Как настоящий соединитель: без адреса или ключа — «не настроено».</summary>
    public Task<IKeyManagerClient> OpenAsync(CancellationToken cancellationToken = default) =>
        ОткрытиеПадает is { } сбой ? throw сбой
        : Настройки.Ready
            ? Task.FromResult<IKeyManagerClient>(this)
            : throw new AppException(
                KeyManagerErrorCodes.NotConfigured,
                "Key Manager Plus не настроен (подставной).",
                System.Net.HttpStatusCode.Conflict);

    public Task<IReadOnlyList<KmpCertificate>> ListAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _списков);

        return СписокПадает
            ? throw new KeyManagerException("Key Manager Plus недоступен (подставной отказ).")
            : Task.FromResult<IReadOnlyList<KmpCertificate>>([.. Сертификаты]);
    }

    public async Task<KmpFile> DownloadAsync(
        string commonName, string serialNumber, string format, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _выгрузок);
        ВыгрузкаНачалась.TrySetResult();

        if (Задвижка is { } ждём)
        {
            await ждём.Task.WaitAsync(cancellationToken);
        }

        if (string.Equals(commonName, ПадатьНаИмени, StringComparison.Ordinal))
        {
            throw new KeyManagerException($"Key Manager Plus отказал в выгрузке {format} {commonName} (подставной отказ).");
        }

        if (string.Equals(format, ПадатьНаФормате, StringComparison.Ordinal))
        {
            throw new KeyManagerException($"Key Manager Plus отказал в выгрузке {format} (подставной отказ).");
        }

        // Настоящий сертификат с заданной датой выпуска — для защиты от отката.
        if (string.Equals(format, CertificateFormats.Pem, StringComparison.Ordinal)
            && Выпуски.TryGetValue(serialNumber, out var выпущен))
        {
            return new KmpFile(
                KeyManagerClient.ИмяПоУмолчанию(commonName, format),
                CertificateFormats.ContentType(format),
                Encoding.ASCII.GetBytes(Сертификат(commonName, serialNumber, выпущен)));
        }

        return new KmpFile(
            KeyManagerClient.ИмяПоУмолчанию(commonName, format),
            CertificateFormats.ContentType(format),
            Файлы.TryGetValue(format, out var своё) ? [.. своё] : Encoding.ASCII.GetBytes($"{format}:{serialNumber}"));
    }

    /// <summary>Самоподписанный сертификат PEM с этим серийным номером (hex) и датой выпуска.</summary>
    internal static string Сертификат(string commonName, string serialNumber, DateTimeOffset notBefore)
    {
        using var ключ = System.Security.Cryptography.ECDsa.Create();
        var запрос = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={commonName.Replace("*", "wildcard", StringComparison.Ordinal)}", ключ,
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var сертификат = запрос.Create(
            запрос.SubjectName,
            System.Security.Cryptography.X509Certificates.X509SignatureGenerator.CreateForECDsa(ключ),
            notBefore,
            notBefore.AddDays(90),
            Convert.FromHexString(serialNumber.Length % 2 == 0 ? serialNumber : "0" + serialNumber));

        return сертификат.ExportCertificatePem();
    }

    public Task<string?> PassphraseAsync(
        string commonName, string serialNumber, CancellationToken cancellationToken = default) =>
        ПарольПадает
            ? throw new KeyManagerException($"Key Manager Plus отказал (пароль ключа сертификата {commonName}): подставной отказ.")
            : Task.FromResult(Пароли.GetValueOrDefault(serialNumber));

    public void Dispose()
    {
        // Подделка живёт всю коллекцию.
    }

    private static TaskCompletionSource Новый() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Конверт, у которого можно подменить кольцо мастер-ключей — для проверки ротации.</summary>
public sealed class ПодвижныйКонверт : ISecretEnvelope
{
    private static readonly byte[] Первый = [.. Enumerable.Repeat((byte)0x11, MasterKeyRing.KeyBytes)];
    private static readonly byte[] Второй = [.. Enumerable.Repeat((byte)0x22, MasterKeyRing.KeyBytes)];

    private ISecretEnvelope _текущий = Кольцо(1);

    public void Повернуть() => _текущий = Кольцо(2);

    public void Вернуть() => _текущий = Кольцо(1);

    public WrappedDataKey CreateDataKey(int generation = 1) => _текущий.CreateDataKey(generation);

    public SealedSecret Seal(WrappedDataKey key, ReadOnlySpan<byte> plaintext, string context) =>
        _текущий.Seal(key, plaintext, context);

    public byte[] Open(WrappedDataKey key, SealedSecret secret, string context) => _текущий.Open(key, secret, context);

    public SealedSecret SealText(WrappedDataKey key, string plaintext, string context) =>
        _текущий.SealText(key, plaintext, context);

    public string OpenText(WrappedDataKey key, SealedSecret secret, string context) =>
        _текущий.OpenText(key, secret, context);

    public bool NeedsRewrap(WrappedDataKey key) => _текущий.NeedsRewrap(key);

    public WrappedDataKey Rewrap(WrappedDataKey key) => _текущий.Rewrap(key);

    public (byte[] Sealed, int KeyVersion) SealForDevice(WrappedDataKey key, DeviceSealTarget target, Guid vaultId) =>
        _текущий.SealForDevice(key, target, vaultId);

    private static SecretEnvelope Кольцо(int версий) => new(MasterKeyRing.FromKeys(
        версий == 1
            ? new Dictionary<int, byte[]> { [1] = Первый }
            : new Dictionary<int, byte[]> { [1] = Первый, [2] = Второй }));
}
