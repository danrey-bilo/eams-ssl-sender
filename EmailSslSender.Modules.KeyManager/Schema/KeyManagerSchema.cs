using EmailSslSender.Modules.KeyManager.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Modules.Core.Data;

namespace EmailSslSender.Modules.KeyManager.Schema;

/// <summary>Набор миграций таблиц Key Manager Plus.</summary>
/// <remarks>⚠ Базовая миграция повторяет прежнюю схему хоста (установка 1.x её отмечает) — не менять.</remarks>
internal sealed class KeyManagerSchema(DbContextOptions<KeyManagerSchema> options)
    : ModuleSchemaContext(options, [new KeyManagerTables()])
{
    /// <summary>Имя набора — оно же в имени таблицы истории.</summary>
    public const string Module = "keymanager";

    public static ModuleSchema Declaration { get; } = new(Module, typeof(KeyManagerSchema), connection => Create(connection));

    public static KeyManagerSchema Create(string connection) => new(
        new DbContextOptionsBuilder<KeyManagerSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(KeyManagerSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Для <c>dotnet ef migrations add</c>.</summary>
internal sealed class KeyManagerSchemaFactory : IDesignTimeDbContextFactory<KeyManagerSchema>
{
    /// <inheritdoc />
    public KeyManagerSchema CreateDbContext(string[] args) => KeyManagerSchema.Create("Host=design-time;Database=design-time");
}
