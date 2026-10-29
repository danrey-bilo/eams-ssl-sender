using Modules.Crypto;
using Modules.Core.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Откуда установка берёт мастер-ключ.</summary>
/// <param name="Ring">Разобранные ключи.</param>
/// <param name="Origin">Что прочитали — для лога, без содержимого.</param>
/// <param name="FromEnvironment">Из переменной окружения — только разработка.</param>
internal sealed record MasterKeySource(MasterKeyRing Ring, string Origin, bool FromEnvironment)
{
    /// <summary>Сказать в лог, чем работаем, без ключей.</summary>
    public void Announce(ILogger logger)
    {
        if (FromEnvironment)
        {
            // ⚠ Переменная видна в `docker inspect` — об этом предупреждаем вслух.
            logger.LogWarning(
                "master_key_from_environment {Origin} {Versions}",
                Origin, Ring.Count);

            return;
        }

        logger.LogInformation(
            "master_key_loaded {Origin} {Versions} {Current}",
            Origin, Ring.Count, Ring.CurrentVersion);
    }
}

/// <summary>Чтение мастер-ключа: файл главнее переменной, без ключа не стартуем.</summary>
internal static class MasterKeyFile
{
    /// <summary>Путь к файлу секрета.</summary>
    public const string FileKey = "SENDER_MASTER_KEK_FILE";

    /// <summary>Ключ прямо в переменной. Только разработка.</summary>
    public const string InlineKey = "SENDER_MASTER_KEK";

    /// <summary>Прочитать и разобрать мастер-ключ.</summary>
    /// <exception cref="InvalidOperationException">Ключа нет, файл не читается или формат не тот.</exception>
    public static MasterKeySource Load(IConfiguration configuration)
    {
        var (content, origin, fromEnvironment) = Content(configuration);

        return new MasterKeySource(MasterKeyRing.Parse(content, Hint), origin, fromEnvironment);
    }

    private const string Hint = $"{FileKey} (создать: ./scripts/gen-secrets.sh)";

    /// <summary>Содержимое источника как есть — только для <see cref="LegacySecrets"/>.</summary>
    internal static (string Content, string Origin, bool FromEnvironment) Content(IConfiguration configuration)
    {
        var path = configuration[FileKey];

        if (!string.IsNullOrWhiteSpace(path))
        {
            return (Read(path), path, false);
        }

        var inline = configuration[InlineKey];

        if (!string.IsNullOrWhiteSpace(inline))
        {
            return (inline, InlineKey, true);
        }

        throw new InvalidOperationException(
            $"Не задан мастер-ключ. Без него нечем расшифровать ни один секрет, поэтому "
            + $"запуск прерван. Задайте {FileKey} — путь к файлу секрета (в бою так), либо "
            + $"{InlineKey} — ключ строкой (только разработка: переменная видна в "
            + $"`docker inspect`). Создать файл: ./scripts/gen-secrets.sh");
    }

    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Путь не секрет; текст исключения может процитировать содержимое.
            throw new InvalidOperationException(
                $"Файл мастер-ключа {path} не читается ({SafeError.Describe(error)}). "
                + "В контейнере он приезжает docker-секретом; создать его: ./scripts/gen-secrets.sh");
        }
    }
}
