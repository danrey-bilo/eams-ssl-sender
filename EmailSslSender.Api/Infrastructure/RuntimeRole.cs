using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Узкая роль, которой ходит работающий сервис: только чтение и запись строк, без DDL.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Зачем.</b> Владелец схемы в образе <c>postgres</c> — суперпользователь. Ходи сервис
/// им, любая уязвимость в приложении становилась бы правами суперпользователя базы:
/// <c>COPY … TO PROGRAM</c> — это выполнение команд в контейнере базы, а <c>ALTER</c> и
/// <c>DROP</c> — порча схемы. Роль сервиса умеет только <c>SELECT/INSERT/UPDATE/DELETE</c>
/// в схеме <c>public</c>; схему меняет одна команда <c>migrate</c>, и она ходит владельцем из
/// отдельной службы, у которой есть его пароль (у службы <c>api</c> его нет).
///
/// ⚠ <b>Заводится и обновляется каждой командой <c>migrate</c></b>, идемпотентно: роль
/// создаётся или приводится к нужным атрибутам, пароль берётся из файла секрета (сменили файл —
/// следующий <c>migrate</c> сменил пароль), права выдаются на всё, что есть, и по умолчанию —
/// на всё, что создадут будущие миграции того же владельца.
///
/// ⚠ <b>Текст DDL собирает сам PostgreSQL</b> (<c>format</c> с <c>%I</c> и <c>%L</c>):
/// параметром в <c>CREATE ROLE</c> имя и пароль не передать, а склейка в C# — это ровно та
/// инъекция, от которой роль и защищает.
///
/// ⚠ <b>Пароль уходит проверочным значением SCRAM-SHA-256, а не текстом</b> (ревью безопасности
/// 2026-10-20): текст команды PostgreSQL пишет в журнал при ошибке или при
/// <c>log_statement</c>, и открытый пароль роли остался бы там.
///
/// ⚠ <b>Журнал миграций роль только читает</b>: старт проверяет по нему схему, а писать в него
/// может лишь <c>migrate</c>.
/// </remarks>
public static partial class RuntimeRole
{
    /// <summary>Переменная с именем роли сервиса.</summary>
    public const string RoleVariable = "SENDER_APP_ROLE";

    /// <summary>Переменная с путём к файлу пароля роли сервиса.</summary>
    public const string PasswordFileVariable = "SENDER_APP_PASSWORD_FILE";

    /// <summary>Атрибуты роли: вход и ничего сверх.</summary>
    private const string Атрибуты = "LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT";

    /// <summary>
    /// Права, по одной команде. <c>%1$I</c> — роль, <c>%2$I</c> — база.
    /// </summary>
    /// <remarks>
    /// ⚠ Последовательности — ради столбцов <c>IDENTITY</c>: без <c>USAGE</c> на них вставка
    /// строки с автономером падает. <c>ALTER DEFAULT PRIVILEGES</c> действует на объекты,
    /// которые создаст текущая роль, — владелец, который и накатывает миграции.
    /// </remarks>
    private static readonly string[] Права =
    [
        "GRANT CONNECT ON DATABASE %2$I TO %1$I",
        "GRANT USAGE ON SCHEMA public TO %1$I",
        "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %1$I",
        "GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO %1$I",
        "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %1$I",
        "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %1$I",
    ];

    /// <summary>
    /// Имя и пароль роли сервиса из конфигурации или <c>null</c>, если роль не задана
    /// (разработка, тесты — там сервис ходит владельцем).
    /// </summary>
    public static (string Role, string Password)? FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var роль = configuration[RoleVariable]?.Trim();

        if (string.IsNullOrEmpty(роль))
        {
            return null;
        }

        var пароль = ConnectionSecrets.ReadSecretFile(configuration, PasswordFileVariable)
            ?? throw new InvalidOperationException(
                $"Задана роль сервиса {RoleVariable}={роль}, но нет пароля: {PasswordFileVariable} должен "
                + "указывать на файл секрета (./scripts/gen-secrets.sh создаёт secrets/sender_app_password).");

        return (роль, пароль);
    }

    /// <summary>Завести или обновить роль сервиса и выдать ей права. Ходит владельцем схемы.</summary>
    /// <param name="connection">Открытое подключение владельца схемы.</param>
    /// <param name="role">Имя роли сервиса.</param>
    /// <param name="password">Её пароль.</param>
    /// <param name="cancellationToken">Отмена.</param>
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

        // ⚠ Сервис, ходящий владельцем, — ровно то, от чего роль защищает; выдать «роли сервиса»
        // права самой себе и отрапортовать успех было бы тихим провалом всей затеи.
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

        if (await ЗначениеAsync<bool>(
                connection, транзакция, "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL", cancellationToken))
        {
            await ВыполнитьСобраннуюAsync(
                connection, транзакция,
                "SELECT format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public.%I FROM %I', '__EFMigrationsHistory', @r::text)",
                cancellationToken, ("r", role));
        }

        // В PostgreSQL 15+ так уже по умолчанию; явно — на случай базы, обновлённой со старой версии.
        await using (var отзыв = new NpgsqlCommand("REVOKE CREATE ON SCHEMA public FROM PUBLIC", connection, транзакция))
        {
            await отзыв.ExecuteNonQueryAsync(cancellationToken);
        }

        await транзакция.CommitAsync(cancellationToken);
    }

    /// <summary>Собрать команду запросом <c>format</c> и выполнить собранное.</summary>
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

    /// <summary>
    /// Проверочное значение SCRAM-SHA-256 в формате PostgreSQL:
    /// <c>SCRAM-SHA-256$итерации:соль$StoredKey:ServerKey</c> (RFC 5802, RFC 7677).
    /// </summary>
    /// <remarks>
    /// ⚠ Пароль приводится к NFKC — так его нормализует SASLprep на стороне PostgreSQL; для
    /// паролей из <c>gen-secrets.sh</c> (латиница, цифры, «-», «_») это ничего не меняет.
    /// </remarks>
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

    /// <summary>Имя роли: обычный идентификатор PostgreSQL без кавычек.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex Имя();
}
