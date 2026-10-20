using EmailSslSender.Modules.Mail.Domain;
using EmailSslSender.Shared.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Mail.Storage;

/// <summary>
/// Две таблицы почты в общей модели.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Индекс очереди ЧАСТИЧНЫЙ — <c>WHERE sent_at IS NULL</c>.</b> По нему ходит насос
/// каждые пятнадцать секунд, и без частичности индекс растёт вместе с историей отправленного:
/// через год в нём будут сотни тысяч строк, из которых интересны единицы. Полный индекс платил
/// бы размером и записью на каждой вставке за пользу, которой нет.
///
/// ⚠ <b>Внешних ключей нет ни одного.</b> <c>system_secrets.updated_by</c> в эталоне ссылается
/// на <c>users</c>; здесь <c>users</c> — чужая таблица, и прецедент тот же, что у
/// <c>audit_log.actor_id</c> (правило 1).
///
/// ⚠ <b><c>default now()</c> задаётся ЯВНО.</b> EF Core серверных умолчаний сам не пишет; без
/// этой строки колонка получит умолчание приложения, а строка, вставленная мимо приложения
/// (миграция, psql), останется без времени.
/// </remarks>
internal sealed class MailEntities : IEntityConfigurationProvider
{
    /// <summary>Таблица очереди писем.</summary>
    internal const string OutboxTable = "mail_outbox";

    /// <summary>Таблица секретов установки.</summary>
    internal const string SecretsTable = "system_secrets";

    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<MailOutbox>(строка =>
        {
            строка.ToTable(OutboxTable);

            строка.HasKey(п => п.Id).HasName($"pk_{OutboxTable}");

            строка.Property(п => п.Id).HasColumnName("id").ValueGeneratedNever();

            строка.Property(п => п.ToAddress)
                .HasColumnName("to_address").HasColumnType("text").IsRequired();

            строка.Property(п => п.Subject)
                .HasColumnName("subject").HasColumnType("text").IsRequired();

            строка.Property(п => п.SealedBody)
                .HasColumnName("body").HasColumnType("bytea").IsRequired();

            строка.Property(п => п.DataKey)
                .HasColumnName("data_key").HasColumnType("bytea").IsRequired();

            строка.Property(п => п.Kind)
                .HasColumnName("kind").HasColumnType("text").IsRequired();

            строка.Property(п => п.CreatedAt)
                .HasColumnName("created_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");

            строка.Property(п => п.NextAttemptAt)
                .HasColumnName("next_attempt_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");

            строка.Property(п => п.Attempts)
                .HasColumnName("attempts").IsRequired().HasDefaultValue(0);

            строка.Property(п => п.SentAt).HasColumnName("sent_at").HasColumnType("timestamptz");

            строка.Property(п => п.LastError).HasColumnName("last_error").HasColumnType("text");

            // ⚠ Тот самый частичный индекс. `HasFilter` — реляционный API EF, а не
            // провайдерский: ссылки на Npgsql от него не появляется.
            строка.HasIndex(п => п.NextAttemptAt)
                .HasDatabaseName($"ix_{OutboxTable}_pending")
                .HasFilter("sent_at IS NULL");

            // Считалка предела частоты: «сколько писем этого вида ушло на этот адрес за час».
            строка.HasIndex(п => new { п.ToAddress, п.Kind, п.CreatedAt })
                .HasDatabaseName($"ix_{OutboxTable}_rate");
        });

        model.Entity<SystemSecret>(строка =>
        {
            строка.ToTable(SecretsTable);

            строка.HasKey(с => с.Key).HasName($"pk_{SecretsTable}");

            строка.Property(с => с.Key).HasColumnName("key").HasColumnType("text").IsRequired();

            строка.Property(с => с.Sealed)
                .HasColumnName("sealed").HasColumnType("bytea").IsRequired();

            строка.Property(с => с.DataKey)
                .HasColumnName("data_key").HasColumnType("bytea").IsRequired();

            строка.Property(с => с.UpdatedAt)
                .HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");

            // ⚠ Без внешнего ключа: `users` — чужая таблица (правило 1).
            строка.Property(с => с.UpdatedBy).HasColumnName("updated_by");
        });
    }
}
