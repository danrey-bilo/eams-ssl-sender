using System.Security.Cryptography;
using EmailSslSender.Api.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Откуда установка берёт мастер-ключ и о чём при этом молчит.
/// </summary>
/// <remarks>
/// Без базы и без хоста: проверяется чтение настроек хоста, а поднимать ради него postgres
/// значило бы платить контейнером за разбор строки.
/// </remarks>
public sealed class MasterKeyTests
{
    [Fact]
    public void Без_ключа_приложение_не_стартует()
    {
        // ⚠ Отказ при старте, а не при первом секрете. Поднявшаяся без ключа система
        // работает ровно до того момента, когда кому-то понадобится показать пароль, — а к
        // тому времени она уже в бою и с людьми.
        var error = Should.Throw<InvalidOperationException>(() => MasterKeyFile.Load(Config()));

        // Отказ обязан говорить, что делать: оба способа задать ключ и чем создать файл.
        error.Message.ShouldContain(MasterKeyFile.FileKey);
        error.Message.ShouldContain(MasterKeyFile.InlineKey);
        error.Message.ShouldContain("gen-secrets.sh");
    }

    [Fact]
    public void Файл_идёт_вперёд_переменной()
    {
        // Так выглядит рабочая машина, где в окружении остался дев-ключ, а в контейнере уже
        // приехал docker-секрет. Побеждать должен файл, иначе установка тихо работает не на
        // том ключе — и обнаруживается это на первом же секрете, записанном не тем.
        var file = TempKey(out var fromFile);

        try
        {
            var source = MasterKeyFile.Load(Config(
                (MasterKeyFile.FileKey, file),
                (MasterKeyFile.InlineKey, Convert.ToBase64String(NewKey()))));

            source.FromEnvironment.ShouldBeFalse();
            source.Origin.ShouldBe(file);

            // Тот ли это ключ, проверяем делом: конверт на ринге из файла должен открывать
            // то, что запечатал конверт на ринге, собранном из того же ключа руками.
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

        // ⚠ Предупреждением, а не информацией. Установка, поднятая «на попробовать» и
        // оставшаяся жить, иначе так и останется с ключом, видным в `docker inspect`.
        log.Lines.ShouldHaveSingleItem().ShouldStartWith("Warning");
        log.Lines[0].ShouldContain("master_key_from_environment");
    }

    [Fact]
    public void Нечитаемый_файл_называет_путь_но_не_свои_подробности()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"нет-такого-{Guid.CreateVersion7():N}");

        var error = Should.Throw<InvalidOperationException>(
            () => MasterKeyFile.Load(Config((MasterKeyFile.FileKey, missing))));

        // Путь не секрет и без него не разобраться.
        error.Message.ShouldContain(missing);

        // ⚠ А вот текст исключения — не наше дело: правило 14, и здесь оно особенно к
        // месту, ведь речь о файле с мастер-ключом. Достаточно типа.
        error.Message.ShouldContain("FileNotFoundException");
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Объявление_не_печатает_ключ()
    {
        // Условие завершения этапа: секрет не появляется в логах. Строка запуска —
        // самое вероятное место, где он там оказался бы.
        var file = TempKey(out var key);

        try
        {
            var log = new Recording();

            MasterKeyFile.Load(Config((MasterKeyFile.FileKey, file))).Announce(log);

            var printed = string.Join('\n', log.Lines);

            printed.ShouldNotContain(Convert.ToBase64String(key));
            printed.ShouldNotContain(Convert.ToHexString(key));

            // И при этом остаётся полезной: путь и число версий — не секрет.
            printed.ShouldContain("master_key_loaded");
        }
        finally
        {
            File.Delete(file);
        }
    }

    // --- обвязка -----------------------------------------------------------------------------

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Временный файл секрета. Возвращает путь, ключ отдаёт наружу.</summary>
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

    /// <summary>Логгер, который всё запоминает — включая структурные поля.</summary>
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
            // И отформатированное сообщение, и само состояние: секрет мог бы уехать
            // структурным полем, которого в тексте не видно.
            Lines.Add($"{logLevel} {formatter(state, exception)} {state}");
    }
}
