using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Modules.Audit.Contracts;
using Npgsql;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Узкая роль работающего сервиса: чтение и запись строк, без DDL.</summary>
/// <remarks>⚠ DDL собирает PostgreSQL (<c>format</c> с <c>%I</c>/<c>%L</c>), пароль уходит проверочным значением SCRAM.</remarks>
public static partial class RuntimeRole
{
    /// <summary>Переменная с именем роли сервиса.</summary>
    public const string RoleVariable = "SENDER_APP_ROLE";

    /// <summary>Переменная с путём к файлу пароля роли сервиса.</summary>
    public const string PasswordFileVariable = "SENDER_APP_PASSWORD_FILE";

    /// <summary>Пароль роли прямо в переменной — для Portainer; файл главнее.</summary>
    public const string PasswordVariable = "SENDER_APP_PASSWORD";

    private const string Атрибуты = "LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT";

    // %1$I — роль, %2$I — база. Последовательности — ради столбцов IDENTITY.
    private static readonly string[] Права =
    [
        "GRANT CONNECT ON DATABASE %2$I TO %1$I",
        "GRANT USAGE ON SCHEMA public TO %1$I",
        "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %1$I",
        "GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO %1$I",
        "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %1$I",
        "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %1$I",
    ];

    /// <summary>Имя и пароль роли сервиса или <c>null</c>, если роль не задана (разработка, тесты).</summary>
    public static (string Role, string Password)? FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var роль = configuration[RoleVariable]?.Trim();

        if (string.IsNullOrEmpty(роль))
        {
            return null;
        }

        var пароль = ConnectionSecrets.ReadSecretFile(configuration, PasswordFileVariable)
            ?? (string.IsNullOrEmpty(configuration[PasswordVariable]) ? null : configuration[PasswordVariable])
            ?? throw new InvalidOperationException(
                $"Задана роль сервиса {RoleVariable}={роль}, но нет пароля: задайте {PasswordFileVariable} "
                + $"(файл секрета, ./scripts/gen-secrets.sh) или {PasswordVariable} (установка через Portainer).");

        return (роль, пароль);
    }

    /// <summary>Завести или обновить роль сервиса и выдать ей права; ходит владельцем схемы.</summary>
    public static async Task EnsureAsync(
        NpgsqlConnection connection, string role, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(password);

        if (!Имя().IsMatch(role))
        {
            throw new InvalidOperationException(
                $"Имя роли сервиса «{role}» недопустимо: строчные латинские буквы, цифры и «_», "
                + "начиная с буквы, до 63 знаков.");
        }

        await using var транзакция = await connection.BeginTransactionAsync(cancellationToken);

        var владелец = await ЗначениеAsync<string>(connection, транзакция, "SELECT current_user", cancellationToken);

        // ⚠ Сервис, ходящий владельцем, — ровно то, от чего роль защищает.
        if (string.Equals(владелец, role, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Роль сервиса «{role}» совпадает с владельцем схемы: migrate должен ходить владельцем, "
                + $"а сервис — отдельной ролью ({RoleVariable}).");
        }

        var есть = await ЗначениеAsync<bool>(
            connection, транзакция, "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @r)", cancellationToken,
            ("r", role));

        var команда = (есть ? "ALTER" : "CREATE") + " ROLE %I WITH " + Атрибуты + " PASSWORD %L";

        await ВыполнитьСобраннуюAsync(connection, транзакция, $"SELECT format('{команда}', @r, @p)", cancellationToken,
            ("r", role), ("p", Scram(password)));

        foreach (var право in Права)
        {
            await ВыполнитьСобраннуюAsync(
                connection, транзакция, $"SELECT format('{право}', @r::text, current_database()::text)", cancellationToken,
                ("r", role));
        }

        // Историю миграций сервис только читает — проверка схемы при старте.
        var истории = new List<string>();

        await using (var список = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' "
            + "AND (tablename = '__EFMigrationsHistory' OR starts_with(tablename, '__ef_history_'))",
            connection, транзакция))
        await using (var читатель = await список.ExecuteReaderAsync(cancellationToken))
        {
            while (await читатель.ReadAsync(cancellationToken))
            {
                истории.Add(читатель.GetString(0));
            }
        }

        foreach (var история in истории)
        {
            await ВыполнитьСобраннуюAsync(
                connection, транзакция,
                "SELECT format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public.%I FROM %I', @t::text, @r::text)",
                cancellationToken, ("t", история), ("r", role));
        }

        // ⚠ Права журнала — ПОСЛЕ общей раздачи: список модуля начинается с изъятий; удаление партиций отбирается.
        foreach (var правоЖурнала in AuditPrivileges.Grants(role, role))
        {
            await using var выдача = new NpgsqlCommand(правоЖурнала, connection, транзакция);
            await выдача.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var отзыв = new NpgsqlCommand(
            $"REVOKE EXECUTE ON FUNCTION {AuditPrivileges.DropPartition}(text) FROM {role}", connection, транзакция))
        {
            await отзыв.ExecuteNonQueryAsync(cancellationToken);
        }

        // Отметки сброса доступа — только у migrate.
        await using (var отзыв = new NpgsqlCommand(
            $"REVOKE ALL ON sender_reset_access_marks FROM {role}", connection, транзакция))
        {
            await отзыв.ExecuteNonQueryAsync(cancellationToken);
        }

        // В PostgreSQL 15+ так по умолчанию; явно — для базы, обновлённой со старой версии.
        await using (var отзыв = new NpgsqlCommand("REVOKE CREATE ON SCHEMA public FROM PUBLIC", connection, транзакция))
        {
            await отзыв.ExecuteNonQueryAsync(cancellationToken);
        }

        await транзакция.CommitAsync(cancellationToken);
    }

    private static async Task ВыполнитьСобраннуюAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction транзакция,
        string сборка,
        CancellationToken cancellationToken,
        params (string Имя, string Значение)[] параметры)
    {
        var текст = await ЗначениеAsync<string>(connection, транзакция, сборка, cancellationToken, параметры);

        await using var команда = new NpgsqlCommand(текст, connection, транзакция);
        await команда.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ЗначениеAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction транзакция,
        string запрос,
        CancellationToken cancellationToken,
        params (string Имя, string Значение)[] параметры)
    {
        await using var команда = new NpgsqlCommand(запрос, connection, транзакция);

        foreach (var (имя, значение) in параметры)
        {
            команда.Parameters.AddWithValue(имя, значение);
        }

        return (T)(await команда.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL не вернул значения: " + запрос));
    }

    /// <summary>Проверочное значение SCRAM-SHA-256 в формате PostgreSQL (NFKC — как SASLprep).</summary>
    internal static string Scram(string password)
    {
        const int итераций = 4096;

        var соль = RandomNumberGenerator.GetBytes(16);
        var солёный = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC)), соль, итераций, HashAlgorithmName.SHA256, 32);
        var хранимый = SHA256.HashData(HMACSHA256.HashData(солёный, "Client Key"u8));
        var серверный = HMACSHA256.HashData(солёный, "Server Key"u8);

        CryptographicOperations.ZeroMemory(солёный);

        return $"SCRAM-SHA-256${итераций}:{Convert.ToBase64String(соль)}"
            + $"${Convert.ToBase64String(хранимый)}:{Convert.ToBase64String(серверный)}";
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex Имя();
}
