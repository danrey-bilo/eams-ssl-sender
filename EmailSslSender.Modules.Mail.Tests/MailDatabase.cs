using EmailSslSender.Modules.Mail.Secrets;
using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Modules.Mail.Templates;
using EmailSslSender.Modules.Mail.Storage;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Data;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Настоящий PostgreSQL на прогон и схема модуля над ним.
/// </summary>
/// <remarks>
/// ⚠ <b>Проверяется поведение БАЗЫ, а не кода.</b> Половина того, что закладывает модуль, —
/// ограничения схемы: частичный индекс очереди, атомарность захвата порции, условное обновление
/// при ротации ключей. На подделке они не проверяются вовсе.
///
/// ⚠ <b>Мастер-ключей ДВА, и это не запас.</b> Ротация проверяется только так: строка,
/// запечатанная под первым, обязана переложиться под второй — а «переложилась» нельзя доказать,
/// имея один ключ.
/// </remarks>
public sealed class MailDatabase : IAsyncLifetime
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

    /// <summary>Часы, которые двигает тест.</summary>
    public ПодвижныеЧасы Часы { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Кто пришёл. Задаёт тест — им проверяется `updated_by`.</summary>
    public ПодставнаяЛичность Личность { get; } = new();

    /// <summary>Настройки: умолчания из объявлений, пока тест не подменил.</summary>
    public ФиктивныеНастройки Настройки { get; } = new();

    /// <summary>Что уехало в лог. Им проверяется, что адрес получателя туда НЕ уехал.</summary>
    public ЗаписнойЖурнал Логи { get; } = new();

    /// <summary>Подставной отправитель: запоминает ушедшее и умеет падать по команде.</summary>
    internal ПодставнойОтправитель Отправитель { get; } = new();

    /// <summary>
    /// Конверт с переключаемым кольцом мастер-ключей.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Колец ДВА, а не одно с двумя ключами, и разница несущая.</b> Ротация проверяется
    /// только так: сначала система живёт на кольце {1}, потом ей подкладывают кольцо {1,2} —
    /// ровно как в бою, где старая версия обязана открываться до конца перекладывания, а новое
    /// оборачивается уже второй.
    ///
    /// Кольцо неизменяемо по устройству (`sealed`, приватный конструктор), поэтому
    /// переключается не оно, а конверт поверх него.
    /// </remarks>
    public ПодвижныйКонверт Конверт { get; } = new();

    /// <summary>
    /// Вклинивание между чтением порции и записью — им проверяется условное обновление.
    /// </summary>
    /// <remarks>
    /// ⚠ Взводится тестом на один раз. Без него мутация «убрать условие из обновления»
    /// переживает весь сьют: снаружи эту гонку не устроить, она живёт внутри транзакции.
    /// </remarks>
    public Гонка Гонка { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();

        services.AddLogging(строитель => строитель.AddProvider(Логи));
        services.AddSingleton<IEntityConfigurationProvider, MailEntities>();
        Гонка = new Гонка(_postgres.GetConnectionString());

        services.AddDbContext<SenderDbContext>(
            options => options.UseNpgsql(_postgres.GetConnectionString()).AddInterceptors(Гонка));
        services.AddSingleton<TimeProvider>(Часы);
        services.AddSingleton<ICurrentUser>(Личность);
        services.AddSingleton<ISecretEnvelope>(Конверт);
        services.AddScoped<SystemSecrets>();
        services.AddScoped<IRewrapTarget, SystemSecretsRewrap>();
        services.AddScoped<SystemSecretsRewrap>();

        // Справочник писем и очередь — как в хосте.
        services.AddSingleton<IMailTemplateProvider, MailOwnTemplates>();
        services.AddSingleton<IMailTemplateProvider, ПисьмаБезПредела>();
        services.AddSingleton<MailTemplates>();
        services.AddSingleton<ISettingsReader>(Настройки);
        services.AddSingleton<MailWakeup>();
        services.AddScoped<IMailQueue, MailQueue>();
        services.AddSingleton<IMailSender>(Отправитель);
        services.AddScoped<MailDelivery>();

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
public sealed class MailDatabaseCollection : ICollectionFixture<MailDatabase>
{
    public const string Name = "mail-database";
}

/// <summary>Часы, которые двигает тест.</summary>
public sealed class ПодвижныеЧасы(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _теперь = start;

    public override DateTimeOffset GetUtcNow() => _теперь;

    /// <summary>Сдвинуть время вперёд.</summary>
    public void Вперёд(TimeSpan сколько) => _теперь = _теперь.Add(сколько);
}

/// <summary>Кто пришёл — задаёт тест.</summary>
public sealed class ПодставнаяЛичность : ICurrentUser
{
    public Guid? Id { get; set; } = Guid.CreateVersion7();

    public AuditActorType ActorType { get; set; } = AuditActorType.User;

    public Guid? SessionId { get; set; }
}

/// <summary>
/// Конверт, у которого можно подменить кольцо мастер-ключей.
/// </summary>
/// <remarks>
/// ⚠ Ротация в бою выглядит так: к файлу секрета дописывается вторая строка, процесс
/// перезапускается с кольцом из двух ключей, действующей становится вторая версия. Здесь
/// то же самое без перезапуска — подменой кольца под конвертом.
/// </remarks>
public sealed class ПодвижныйКонверт : ISecretEnvelope
{
    private static readonly byte[] Первый = [.. Enumerable.Repeat((byte)0x11, MasterKeyRing.KeyBytes)];
    private static readonly byte[] Второй = [.. Enumerable.Repeat((byte)0x22, MasterKeyRing.KeyBytes)];

    private ISecretEnvelope _текущий = new SecretEnvelope(
        MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = Первый }));

    /// <summary>Подложить второй ключ: действующей становится версия 2, первая ещё открывает.</summary>
    public void Повернуть() => _текущий = new SecretEnvelope(
        MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = Первый, [2] = Второй }));

    /// <summary>Вернуть кольцо к одной версии — между тестами.</summary>
    public void Вернуть() => _текущий = new SecretEnvelope(
        MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = Первый }));

    public WrappedDataKey CreateDataKey(int generation = 1) => _текущий.CreateDataKey(generation);

    public SealedSecret Seal(WrappedDataKey key, ReadOnlySpan<byte> plaintext, string context) =>
        _текущий.Seal(key, plaintext, context);

    public byte[] Open(WrappedDataKey key, SealedSecret secret, string context) =>
        _текущий.Open(key, secret, context);

    public SealedSecret SealText(WrappedDataKey key, string plaintext, string context) =>
        _текущий.SealText(key, plaintext, context);

    public string OpenText(WrappedDataKey key, SealedSecret secret, string context) =>
        _текущий.OpenText(key, secret, context);

    public bool NeedsRewrap(WrappedDataKey key) => _текущий.NeedsRewrap(key);

    public WrappedDataKey Rewrap(WrappedDataKey key) => _текущий.Rewrap(key);
}

/// <summary>
/// Настройки, которые задаёт тест. Чего не задал — умолчание из объявления ключа.
/// </summary>
/// <remarks>
/// ⚠ Умолчание берётся из <b>ключа</b>, а не из копии здесь: подделка со своими умолчаниями
/// проверяла бы не то приложение, которое бежит.
/// </remarks>
public sealed class ФиктивныеНастройки : ISettingsReader
{
    /// <summary>Заданные значения по имени ключа.</summary>
    public Dictionary<string, object?> Значения { get; } = new(StringComparer.Ordinal);

    public ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return ValueTask.FromResult(
            Значения.TryGetValue(key.Name, out var значение) ? (T)значение! : key.Default);
    }
}

/// <summary>
/// Отправитель, который никуда не ходит.
/// </summary>
/// <remarks>
/// ⚠ Настоящий SMTP в тестах насоса означал бы сеть или контейнер почтовика ради проверки
/// цикла, который про SMTP ничего не знает. А «одно упавшее письмо не уносит пачку» иначе не
/// проверить вовсе: падение надо уметь устроить.
/// </remarks>
// ⚠ `internal`, а не `public`: `IMailSender`, `MailPolicy` и `OutgoingMail` — внутренние типы
// модуля (правило 1: наружу торчит только `Contracts/`), и публичный класс с ними в сигнатуре
// не компилируется. Тестам это не мешает — они в той же сборке.
internal sealed class ПодставнойОтправитель : IMailSender, IMailSession
{
    /// <summary>Что ушло — в порядке отправки.</summary>
    public List<OutgoingMail> Ушедшие { get; } = [];

    /// <summary>Сколько раз открывали транспорт.</summary>
    /// <remarks>
    /// ⚠ ⚠ Ради этого числа подделка и стала сессией (этап 60.2): «одно соединение на порцию»
    /// иначе не проверяется вовсе. Двадцать открытий на двадцать писем — это ровно тот дефект,
    /// который чинился: рукопожатие, TLS и вход на каждое письмо.
    /// </remarks>
    public int Открытий { get; private set; }

    /// <summary>Сколько раз закрывали. Должно совпадать с открытиями.</summary>
    public int Закрытий { get; private set; }

    /// <summary>На каком адресе падать. `null` — не падать.</summary>
    public string? ПадатьНа { get; set; }

    /// <summary>
    /// Задвижка: пока взведена, отправка ждёт на первом же письме.
    /// </summary>
    /// <remarks>
    /// ⚠ Ею проверяется, что второй насос не встаёт за первым в очередь на блокировке строк.
    /// Без задвижки одновременность не устроить: подставная отправка возвращается мгновенно, и
    /// два «параллельных» прогона на деле идут один за другим.
    /// </remarks>
    public TaskCompletionSource? Задвижка { get; private set; }

    /// <summary>Взвести задвижку.</summary>
    public void Задержать() =>
        Задвижка = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Отпустить.</summary>
    public void Отпустить() => Задвижка?.TrySetResult();

    /// <summary>Отправка дошла до задвижки — насос сейчас держит строку письма.</summary>
    public TaskCompletionSource УЗадвижки { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Что сделать сразу после «принятого» письма — например, остановить процесс.</summary>
    public Action? ПослеОтправки { get; set; }

    /// <summary>Забыть всё — между тестами.</summary>
    public void Сброс()
    {
        ПослеОтправки = null;
        Ушедшие.Clear();
        ПадатьНа = null;
        Открытий = 0;
        Закрытий = 0;
        Отпустить();
        Задвижка = null;
        УЗадвижки = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Подделка сама себе сессия: считать открытия проще, чем плодить второй тип.</summary>
    public IMailSession Open(MailPolicy policy)
    {
        Открытий++;

        return this;
    }

    public ValueTask DisposeAsync()
    {
        Закрытий++;

        return ValueTask.CompletedTask;
    }

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        if (Задвижка is { } ждём)
        {
            УЗадвижки.TrySetResult();
            await ждём.Task.WaitAsync(cancellationToken);
        }

        if (ПадатьНа is { } адрес && string.Equals(адрес, mail.ToAddress, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("подставной отказ отправки");
        }

        lock (Ушедшие)
        {
            Ушедшие.Add(mail);
        }

        ПослеОтправки?.Invoke();
    }
}
