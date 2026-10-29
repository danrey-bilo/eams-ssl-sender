using Modules.Core.Composition;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Схема установки: прежний набор хоста, наборы модулей, набор проекта.</summary>
/// <remarks>⚠ Новая установка и дотянутая 1.x обязаны прийти к одной схеме; весь проход — под одним замком.</remarks>
public static class SchemaMigrator
{
    /// <summary>Таблица истории прежних миграций хоста.</summary>
    public const string LegacyHistory = "__EFMigrationsHistory";

    /// <summary>Последняя прежняя миграция хоста; после неё прежний набор заморожен.</summary>
    public const string LegacyLast = "20261026200000_ModulesBridge";

    /// <summary>Номер замка прохода миграций в пространстве проекта.</summary>
    internal const ushort AdvisoryLockNumber = 1;

    /// <summary>Базовые миграции, чью схему установка 1.x уже несёт: модуль → базовая.</summary>
    /// <remarks>⚠ Закрытый список: новый модуль сюда не попадает — его таблиц в прежней схеме нет.</remarks>
    public static readonly IReadOnlyDictionary<string, string> LegacyBaselines = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["identity"] = "20261022231421_Baseline",
        ["tokens"] = "20261022231425_Baseline",
        ["settings"] = "20261022231432_Baseline",
        ["mail"] = "20261022231434_Baseline",
        ["keymanager"] = KeyManagerBaseline,
    };

    /// <summary>Базовая миграция набора Key Manager Plus.</summary>
    internal const string KeyManagerBaseline = "20261026183147_Baseline";

    /// <summary>Что с прежней историей хоста.</summary>
    public enum LegacyState
    {
        /// <summary>Новая установка.</summary>
        None,

        /// <summary>Отстала от последней миграции хоста.</summary>
        Behind,

        /// <summary>Дошла до последней миграции хоста.</summary>
        Complete,
    }

    /// <summary>Дотянуть прежний набор, отметить базовые, выполнить наборы по порядку.</summary>
    /// <param name="connection">Строка подключения владельца схемы.</param>
    /// <param name="schemas">Наборы в порядке применения.</param>
    /// <param name="locks">Пространство замков проекта.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Что сделано — строками «набор: миграция».</returns>
    public static async Task<IReadOnlyList<string>> MigrateAsync(
        string connection,
        IReadOnlyList<ModuleSchema> schemas,
        AdvisoryLockSpace locks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(locks);

        var сделано = new List<string>();

        // ⚠ Замок сессии на отдельном подключении: снимется сам, если процесс умрёт.
        await using var замок = new NpgsqlConnection(connection);
        await замок.OpenAsync(cancellationToken);
        await ВыполнитьAsync(замок, $"SELECT pg_advisory_lock({locks.Key(AdvisoryLockNumber)})", cancellationToken);

        try
        {
            var прежняя = await LegacyStateAsync(connection, cancellationToken);

            if (прежняя != LegacyState.None)
            {
                await using var legacy = Legacy(connection);

                сделано.AddRange((await legacy.Database.GetPendingMigrationsAsync(cancellationToken)).Select(м => $"прежний набор хоста: {м}"));
                await legacy.Database.MigrateAsync(cancellationToken);

                прежняя = await LegacyStateAsync(connection, cancellationToken);
            }

            foreach (var набор in schemas)
            {
                await using var db = набор.Create(connection);
                if (прежняя == LegacyState.Complete && await ОтметитьБазовуюAsync(db, набор, cancellationToken) is { } отмечена)
                {
                    сделано.Add($"{набор.Module}: {отмечена} (отмечена: схема уже есть)");
                }

                сделано.AddRange((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Select(м => $"{набор.Module}: {м}"));
                await db.Database.MigrateAsync(cancellationToken);
            }
        }
        finally
        {
            await ВыполнитьAsync(замок, $"SELECT pg_advisory_unlock({locks.Key(AdvisoryLockNumber)})", CancellationToken.None);
        }

        return сделано;
    }

    /// <summary>Что не применено — строками «набор: миграция»; пусто — схема на месте.</summary>
    public static async Task<IReadOnlyList<string>> PendingAsync(
        string connection, IReadOnlyList<ModuleSchema> schemas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        var недостаёт = new List<string>();

        if (await LegacyStateAsync(connection, cancellationToken) == LegacyState.Behind)
        {
            await using var legacy = Legacy(connection);

            недостаёт.AddRange((await legacy.Database.GetPendingMigrationsAsync(cancellationToken)).Select(м => $"прежний набор хоста: {м}"));
        }

        foreach (var набор in schemas)
        {
            await using var db = набор.Create(connection);

            недостаёт.AddRange((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Select(м => $"{набор.Module}: {м}"));
        }

        return недостаёт;
    }

    /// <summary>Состояние прежней истории хоста.</summary>
    public static async Task<LegacyState> LegacyStateAsync(string connection, CancellationToken cancellationToken = default)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(cancellationToken);

        await using var есть = new NpgsqlCommand($"SELECT to_regclass('public.\"{LegacyHistory}\"') IS NOT NULL", db);

        if (await есть.ExecuteScalarAsync(cancellationToken) is not true)
        {
            return LegacyState.None;
        }

        await using var последняя = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM public.\"{LegacyHistory}\" WHERE \"MigrationId\" = @id)", db);
        последняя.Parameters.AddWithValue("id", LegacyLast);

        return await последняя.ExecuteScalarAsync(cancellationToken) is true ? LegacyState.Complete : LegacyState.Behind;
    }

    /// <summary>Контекст прежнего набора хоста: пустая модель, миграции сборки хоста.</summary>
    internal static AppDbContext Legacy(string connection) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsAssembly(typeof(SchemaMigrator).Assembly))
            .ConfigureWarnings(предупреждения => предупреждения.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options,
        []);

    // Отметить базовую применённой, если она из списка и история пуста; вернуть её или null.
    private static async Task<string?> ОтметитьБазовуюAsync(DbContext db, ModuleSchema набор, CancellationToken cancellationToken)
    {
        if (!LegacyBaselines.TryGetValue(набор.Module, out var базовая))
        {
            return null;
        }

        var первая = db.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).FirstOrDefault();

        if (!string.Equals(первая, базовая, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Набор «{набор.Module}» начинается с {первая ?? "ничего"}, а существующая установка ждёт базовую {базовая}. "
                + "Базовая миграция модуля не меняется и не переименовывается (решение EAMS 0114).");
        }

        var история = db.GetService<IHistoryRepository>();

        await история.CreateIfNotExistsAsync(cancellationToken);

        if ((await история.GetAppliedMigrationsAsync(cancellationToken)).Count > 0)
        {
            return null;
        }

        var строка = история.GetInsertScript(new HistoryRow(базовая, ProductInfo.GetVersion()));

        await db.Database.ExecuteSqlRawAsync(строка, cancellationToken);

        return базовая;
    }

    private static async Task ВыполнитьAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
