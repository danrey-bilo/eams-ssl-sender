using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Shared.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.KeyManager.Storage;

/// <summary>
/// Три таблицы модуля в общей модели.
/// </summary>
/// <remarks>
/// ⚠ <b>Внешних ключей на чужие таблицы нет</b> (<c>created_by</c>, <c>updated_by</c> →
/// <c>users</c>): прецедент тот же, что у <c>system_secrets.updated_by</c> (правило 1).
///
/// ⚠ <b><c>default now()</c> задаётся ЯВНО</b> — EF Core серверных умолчаний сам не пишет.
/// </remarks>
internal sealed class KeyManagerTables : IEntityConfigurationProvider
{
    internal const string StateTable = "keymanager_state";

    internal const string CertificatesTable = "keymanager_certificates";

    internal const string SubscriptionsTable = "certificate_subscriptions";

    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<KeyManagerState>(строка =>
        {
            строка.ToTable(StateTable, таблица => таблица.HasCheckConstraint(
                $"ck_{StateTable}_singleton", $"id = {KeyManagerState.SingletonId}"));

            строка.HasKey(с => с.Id).HasName($"pk_{StateTable}");
            строка.Property(с => с.Id).HasColumnName("id").ValueGeneratedNever();
            строка.Property(с => с.ApiKey).HasColumnName("api_key").HasColumnType("bytea");
            строка.Property(с => с.DataKey).HasColumnName("data_key").HasColumnType("bytea");
            строка.Property(с => с.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            строка.Property(с => с.UpdatedBy).HasColumnName("updated_by");
            строка.Property(с => с.LastPollAt).HasColumnName("last_poll_at").HasColumnType("timestamptz");
            строка.Property(с => с.LastPollError).HasColumnName("last_poll_error").HasColumnType("text");
            строка.Property(с => с.LastPollCount).HasColumnName("last_poll_count");
        });

        model.Entity<KnownCertificate>(строка =>
        {
            строка.ToTable(CertificatesTable);

            строка.HasKey(с => new { с.CommonName, с.SerialNumber }).HasName($"pk_{CertificatesTable}");

            строка.Property(с => с.CommonName).HasColumnName("common_name").HasColumnType("text").IsRequired();
            строка.Property(с => с.SerialNumber).HasColumnName("serial_number").HasColumnType("text").IsRequired();
            строка.Property(с => с.ExpiresOn).HasColumnName("expires_on").HasColumnType("date");

            строка.Property(с => с.FirstSeenAt)
                .HasColumnName("first_seen_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");

            строка.Property(с => с.LastSeenAt)
                .HasColumnName("last_seen_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
        });

        model.Entity<CertificateSubscription>(строка =>
        {
            строка.ToTable(SubscriptionsTable, таблица =>
            {
                таблица.HasCheckConstraint(
                    $"ck_{SubscriptionsTable}_interval", "interval_days IS NULL OR interval_days BETWEEN 1 AND 365");
                таблица.HasCheckConstraint(
                    $"ck_{SubscriptionsTable}_recipients", "cardinality(recipients) >= 1");
                таблица.HasCheckConstraint(
                    $"ck_{SubscriptionsTable}_formats", "cardinality(formats) >= 1");
            });

            строка.HasKey(п => п.Id).HasName($"pk_{SubscriptionsTable}");
            строка.Property(п => п.Id).HasColumnName("id").ValueGeneratedNever();

            строка.Property(п => п.CommonName).HasColumnName("common_name").HasColumnType("text").IsRequired();
            строка.Property(п => п.Recipients).HasColumnName("recipients").HasColumnType("text[]").IsRequired();
            строка.Property(п => п.Formats).HasColumnName("formats").HasColumnType("text[]").IsRequired();
            строка.Property(п => п.IncludePassphrase).HasColumnName("include_passphrase").IsRequired();
            строка.Property(п => п.OnChange).HasColumnName("on_change").IsRequired();
            строка.Property(п => п.IntervalDays).HasColumnName("interval_days");
            строка.Property(п => п.Locale).HasColumnName("locale").HasColumnType("text").IsRequired();
            строка.Property(п => п.Enabled).HasColumnName("enabled").IsRequired();

            строка.Property(п => п.CreatedAt)
                .HasColumnName("created_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
            строка.Property(п => п.CreatedBy).HasColumnName("created_by");

            строка.Property(п => п.UpdatedAt)
                .HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
            строка.Property(п => п.UpdatedBy).HasColumnName("updated_by");

            строка.Property(п => п.LastSentSerial).HasColumnName("last_sent_serial").HasColumnType("text");
            строка.Property(п => п.LastSentAt).HasColumnName("last_sent_at").HasColumnType("timestamptz");
            строка.Property(п => п.LastAttemptAt).HasColumnName("last_attempt_at").HasColumnType("timestamptz");
            строка.Property(п => п.LastError).HasColumnName("last_error").HasColumnType("text");
            строка.Property(п => п.PendingMailIds).HasColumnName("pending_mail_ids").HasColumnType("uuid[]");

            // Опрос ищет подписки по Common Name сертификата.
            строка.HasIndex(п => п.CommonName).HasDatabaseName($"ix_{SubscriptionsTable}_common_name");
        });
    }
}
