using EmailSslSender.Modules.KeyManager.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Modules.Core.Data;

namespace EmailSslSender.Modules.KeyManager.Schema;

/// <summary>
/// Набор миграций модуля: таблицы Key Manager Plus (решение EAMS 0114 — схему несёт модуль).
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Базовая миграция повторяет схему, созданную прежними миграциями хоста</b> (до перехода на
/// модули EAMS). Существующая установка её не выполняет, а отмечает применённой — разбор у
/// <c>SchemaMigrator</c> хоста; новая выполняет. Базовая миграция не меняется и не переименовывается.
/// </remarks>
internal sealed class KeyManagerSchema(DbContextOptions<KeyManagerSchema> options)
    : ModuleSchemaContext(options, [new KeyManagerTables()])
{
    /// <summary>Имя набора — оно же в имени таблицы истории.</summary>
    public const string Module = "keymanager";

    /// <summary>Объявление набора для команды миграций хоста.</summary>
    public static ModuleSchema Declaration { get; } = new(Module, typeof(KeyManagerSchema), connection => Create(connection));

    /// <summary>Контекст набора по строке подключения.</summary>
    public static KeyManagerSchema Create(string connection) => new(
        new DbContextOptionsBuilder<KeyManagerSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(KeyManagerSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Контекст набора для инструментов EF (<c>dotnet ef migrations add</c>).</summary>
internal sealed class KeyManagerSchemaFactory : IDesignTimeDbContextFactory<KeyManagerSchema>
{
    /// <inheritdoc />
    public KeyManagerSchema CreateDbContext(string[] args) => KeyManagerSchema.Create("Host=design-time;Database=design-time");
}
