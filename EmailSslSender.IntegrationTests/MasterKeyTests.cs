using System.Security.Cryptography;
using EmailSslSender.Api.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.IntegrationTests;

/// <summary>Откуда берётся мастер-ключ и что о нём попадает в лог; без базы и хоста.</summary>
public sealed class MasterKeyTests
{
    [Fact]
    public void Без_ключа_приложение_не_стартует()
    {
        // ⚠ Отказ при старте, а не при первом секрете.
        var error = Should.Throw<InvalidOperationException>(() => MasterKeyFile.Load(Config()));

        // Отказ говорит, что делать.
        error.Message.ShouldContain(MasterKeyFile.FileKey);
        error.Message.ShouldContain(MasterKeyFile.InlineKey);
        error.Message.ShouldContain("gen-secrets.sh");
    }

    [Fact]
    public void Файл_идёт_вперёд_переменной()
    {
        // В окружении остался дев-ключ, а docker-секрет уже приехал: побеждает файл.
        var file = TempKey(out var fromFile);

        try
        {
            var source = MasterKeyFile.Load(Config(
                (MasterKeyFile.FileKey, file),
                (MasterKeyFile.InlineKey, Convert.ToBase64String(NewKey()))));

            source.FromEnvironment.ShouldBeFalse();
            source.Origin.ShouldBe(file);

            Same(source, fromFile).ShouldBeTrue();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Переменная_помечается_как_только_для_разработки()
    {
        var source = MasterKeyFile.Load(Config((MasterKeyFile.InlineKey, Convert.ToBase64String(NewKey()))));

        source.FromEnvironment.ShouldBeTrue();

        var log = new Recording();
        source.Announce(log);

        // ⚠ Предупреждением: ключ в окружении виден в `docker inspect`.
        log.Lines.ShouldHaveSingleItem().ShouldStartWith("Warning");
        log.Lines[0].ShouldContain("master_key_from_environment");
    }

    [Fact]
    public void Нечитаемый_файл_называет_путь_но_не_свои_подробности()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"нет-такого-{Guid.CreateVersion7():N}");

        var error = Should.Throw<InvalidOperationException>(
            () => MasterKeyFile.Load(Config((MasterKeyFile.FileKey, missing))));

        error.Message.ShouldContain(missing);

        // ⚠ Текст исключения не выводится — только тип.
        error.Message.ShouldContain("FileNotFoundException");
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Объявление_не_печатает_ключ()
    {
        var file = TempKey(out var key);

        try
        {
            var log = new Recording();

            MasterKeyFile.Load(Config((MasterKeyFile.FileKey, file))).Announce(log);

            var printed = string.Join('\n', log.Lines);

            printed.ShouldNotContain(Convert.ToBase64String(key));
            printed.ShouldNotContain(Convert.ToHexString(key));

            printed.ShouldContain("master_key_loaded");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Временный файл ключа; возвращает путь.</summary>
    private static string TempKey(out byte[] key)
    {
        key = NewKey();

        var path = Path.Combine(Path.GetTempPath(), $"sender-kek-{Guid.CreateVersion7():N}");

        File.WriteAllText(path, Convert.ToBase64String(key));

        return path;
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    /// <summary>Тот же ли ключ: запечатать одним рингом, открыть другим.</summary>
    private static bool Same(MasterKeySource source, byte[] expected)
    {
        var mine = new global::Modules.Crypto.SecretEnvelope(
            global::Modules.Crypto.MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = expected }));

        var theirs = new global::Modules.Crypto.SecretEnvelope(source.Ring);

        var wrapped = mine.CreateDataKey();
        var secret = mine.Seal(wrapped, [1, 2, 3], "тест:ключ");

        try
        {
            return theirs.Open(wrapped, secret, "тест:ключ").SequenceEqual<byte>([1, 2, 3]);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Логгер, запоминающий текст и состояние.</summary>
    private sealed class Recording : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            // Состояние тоже: секрет мог бы уехать структурным полем.
            Lines.Add($"{logLevel} {formatter(state, exception)} {state}");
    }
}
