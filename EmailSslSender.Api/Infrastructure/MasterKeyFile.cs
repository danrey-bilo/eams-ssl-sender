using Modules.Crypto;
using Modules.Core.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Откуда установка берёт мастер-ключ.
/// </summary>
/// <param name="Ring">Разобранные ключи.</param>
/// <param name="Origin">Что именно прочитали — для строки в логе, без содержимого.</param>
/// <param name="FromEnvironment">
/// Прочитано из переменной окружения. Годится только для разработки, и об этом обязаны
/// сказать вслух.
/// </param>
internal sealed record MasterKeySource(MasterKeyRing Ring, string Origin, bool FromEnvironment)
{
    /// <summary>Сказать в лог, чем работаем. Ключей в записи нет и быть не может.</summary>
    public void Announce(ILogger logger)
    {
        if (FromEnvironment)
        {
            // ⚠ Не «неудобство», а разница в модели угроз. Переменная окружения видна в
            // `docker inspect`, в `/proc/<pid>/environ` и уезжает в дампы окружения целиком;
            // файл секрета видит только процесс. Молчать об этом нельзя: установка,
            // поднятая «на попробовать» и оставшаяся жить, иначе так и останется такой.
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

/// <summary>
/// Чтение мастер-ключа установки.
/// </summary>
/// <remarks>
/// <b>Файл вперёд переменной, и без ключа не стартуем.</b> Второе важнее первого: система,
/// поднявшаяся без мастер-ключа, работает ровно до первого секрета, а к тому моменту она уже
/// в бою и с людьми. Отказ при старте стоит минуту, отказ при первом показе пароля —
/// вечер.
///
/// Формат файла — тот же, что у <c>scripts/gen-secrets.sh</c> Python-версии, и это тоже не
/// случайность: два формата означали бы два генератора и два способа ошибиться.
/// </remarks>
internal static class MasterKeyFile
{
    /// <summary>Путь к файлу секрета. Так его отдаёт docker.</summary>
    public const string FileKey = "SENDER_MASTER_KEK_FILE";

    /// <summary>Ключ прямо в переменной. Только разработка.</summary>
    public const string InlineKey = "SENDER_MASTER_KEK";

    /// <summary>Прочитать и разобрать. Бросает, если ключа нет или он негоден.</summary>
    /// <exception cref="InvalidOperationException">Ключа нет, файл не читается или формат не тот.</exception>
    public static MasterKeySource Load(IConfiguration configuration)
    {
        var (content, origin, fromEnvironment) = Content(configuration);

        return new MasterKeySource(MasterKeyRing.Parse(content, Hint), origin, fromEnvironment);
    }

    /// <summary>Подсказка для текста отказа: откуда ключ и чем создать файл.</summary>
    private const string Hint = $"{FileKey} (создать: ./scripts/gen-secrets.sh)";

    /// <summary>Содержимое источника мастер-ключа как есть.</summary>
    /// <remarks>
    /// ⚠ Только для разового перехода на конверт модулей EAMS (<see cref="LegacySecrets"/>): связка
    /// модулей ключей наружу не отдаёт, а открыть прежний конверт «sender:» без них нечем.
    /// </remarks>
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

    /// <summary>Прочитать файл, не проговариваясь о его содержимом.</summary>
    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Путь называем — он не секрет и без него не разобраться. Текст исключения — нет:
            // правило 14, и здесь оно особенно к месту, ведь речь о файле с мастер-ключом.
            throw new InvalidOperationException(
                $"Файл мастер-ключа {path} не читается ({SafeError.Describe(error)}). "
                + "В контейнере он приезжает docker-секретом; создать его: ./scripts/gen-secrets.sh");
        }
    }
}
