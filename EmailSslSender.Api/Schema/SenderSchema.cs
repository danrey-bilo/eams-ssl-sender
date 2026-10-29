using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Modules.Core.Data;

namespace EmailSslSender.Api.Schema;

/// <summary>Набор миграций проекта: значения поверх таблиц модулей.</summary>
/// <remarks>⚠ Модель пуста намеренно: таблицы создают наборы модулей; набор идёт последним.</remarks>
internal sealed class SenderSchema(DbContextOptions<SenderSchema> options) : ModuleSchemaContext(options, [])
{
    /// <summary>Имя набора — оно же в имени таблицы истории.</summary>
    public const string Module = "sender";

    public static ModuleSchema Declaration { get; } = new(Module, typeof(SenderSchema), connection => Create(connection));

    /// <summary>Контекст набора по строке подключения.</summary>
    public static SenderSchema Create(string connection) => new(
        new DbContextOptionsBuilder<SenderSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(SenderSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Контекст набора проекта для инструментов EF.</summary>
internal sealed class SenderSchemaFactory : IDesignTimeDbContextFactory<SenderSchema>
{
    /// <inheritdoc />
    public SenderSchema CreateDbContext(string[] args) => SenderSchema.Create("Host=design-time;Database=design-time");
}
