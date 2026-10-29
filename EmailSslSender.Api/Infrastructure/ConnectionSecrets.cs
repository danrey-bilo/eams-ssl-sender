using Modules.Core.Errors;
using Npgsql;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Пароль к базе — файлом секрета: переменная окружения видна в <c>docker inspect</c>.</summary>
/// <remarks>Файла нет — строка подключения как есть (разработка задаёт пароль в ней).</remarks>
public static class ConnectionSecrets
{
    /// <summary>Переменная с путём к файлу пароля базы.</summary>
    public const string PostgresPasswordFile = "SENDER_POSTGRES_PASSWORD_FILE";

    /// <summary>Пароль базы прямо в переменной — для Portainer; файл главнее.</summary>
    public const string PostgresPassword = "SENDER_POSTGRES_PASSWORD";

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

        var пароль = Прочитать(configuration, PostgresPasswordFile)
            ?? (string.IsNullOrEmpty(configuration[PostgresPassword]) ? null : configuration[PostgresPassword]);

        if (пароль is null)
        {
            return строка;
        }

        // Строителем, не склейкой: пароль может содержать `;` и `=`.
        return new NpgsqlConnectionStringBuilder(строка) { Password = пароль }.ConnectionString;
    }

    /// <summary>Файл секрета из переменной <paramref name="variable"/> или <c>null</c>, если она не задана.</summary>
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
            // `echo пароль > файл` добавляет перевод строки.
            var значение = File.ReadAllText(путь).Trim();

            return значение.Length == 0
                ? throw new InvalidOperationException(
                    $"Файл {путь}, указанный в {переменная}, пуст. Пустой файл секрета обычно "
                    + "означает, что его забыли наполнить: ./scripts/gen-secrets.sh")
                : значение;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Путь не секрет; текст исключения может процитировать содержимое.
            throw new InvalidOperationException(
                $"Файл {путь}, указанный в {переменная}, не читается ({SafeError.Describe(error)}). "
                + "В контейнере он приезжает docker-секретом; создать: ./scripts/gen-secrets.sh");
        }
    }
}
