using EmailSslSender.Shared.Errors;
using Npgsql;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Пароль к базе приезжает <b>файлом</b>, а не переменной окружения.
/// </summary>
/// <remarks>
/// <b>Причина ровно та же, что у мастер-ключа</b> (правило 14, <see cref="MasterKeyFile"/>):
/// переменная окружения видна в <c>docker inspect</c> и в <c>/proc/&lt;pid&gt;/environ</c>,
/// файл секрета — только процессу. Разница не теоретическая: <c>docker inspect</c> зовут при
/// первом же разборе неполадки, и вывод его пересылают в тикет целиком.
///
/// <b>Как это выглядит в конфигурации.</b> Строка подключения задаётся <b>без пароля</b>, а
/// путь к файлу — отдельной переменной:
/// <code>
/// ConnectionStrings__Postgres = "Host=postgres;Database=sender;Username=sender"
/// SENDER_POSTGRES_PASSWORD_FILE = "/run/secrets/sender_postgres_password"
/// </code>
///
/// ⚠ <b>Файла нет — стартуем без пароля, и это НЕ послабление.</b> Разработческая установка
/// задаёт пароль прямо в строке подключения; требовать там файл значило бы заводить его ради
/// обхода. Отказ стоит там, где ему место: боевой <c>compose</c> подключает файл секрета, и
/// без файла не поднимается сам <c>compose</c>, не дожидаясь приложения.
/// </remarks>
public static class ConnectionSecrets
{
    /// <summary>Переменная с путём к файлу пароля базы.</summary>
    public const string PostgresPasswordFile = "SENDER_POSTGRES_PASSWORD_FILE";




    /// <summary>Строка подключения к базе — с паролем из файла, если он задан.</summary>
    /// <exception cref="InvalidOperationException">Строки подключения нет вовсе.</exception>
    public static string Postgres(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var строка = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "Не задана строка подключения ConnectionStrings:Postgres. В контейнере её "
                + "задаёт docker-compose; пароль при этом приезжает отдельным файлом секрета, "
                + $"путь к нему — в {PostgresPasswordFile}.");

        if (Прочитать(configuration, PostgresPasswordFile) is not { } пароль)
        {
            return строка;
        }

        // Через строитель, а не склейкой: пароль может содержать `;` и `=`, и склеенная
        // строка тогда разберётся не так, как задумано, — молча и в самом неудобном месте.
        return new NpgsqlConnectionStringBuilder(строка) { Password = пароль }.ConnectionString;
    }

    /// <summary>
    /// Значение файла секрета, путь к которому в переменной <paramref name="variable"/>, или
    /// <c>null</c>, если переменная не задана.
    /// </summary>
    public static string? ReadSecretFile(IConfiguration configuration, string variable)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Прочитать(configuration, variable);
    }

    private static string? Прочитать(IConfiguration configuration, string переменная)
    {
        var путь = configuration[переменная];

        if (string.IsNullOrWhiteSpace(путь))
        {
            return null;
        }

        try
        {
            // Обрезаем перевод строки: `echo пароль > файл` добавляет его, и пароль с
            // хвостовым \n не совпадёт с тем, что задали хранилищу.
            var значение = File.ReadAllText(путь).Trim();

            return значение.Length == 0
                ? throw new InvalidOperationException(
                    $"Файл {путь}, указанный в {переменная}, пуст. Пустой файл секрета обычно "
                    + "означает, что его забыли наполнить: ./scripts/gen-secrets.sh")
                : значение;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Путь называем — он не секрет и без него не разобраться. Текст исключения — нет
            // — сообщение файловой системы может процитировать содержимое.
            throw new InvalidOperationException(
                $"Файл {путь}, указанный в {переменная}, не читается ({SafeError.Describe(error)}). "
                + "В контейнере он приезжает docker-секретом; создать: ./scripts/gen-secrets.sh");
        }
    }
}
