using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Modules.Tokens.Contracts;
using EmailSslSender.Modules.Tokens.Domain;
using EmailSslSender.Shared.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Tokens.Storage;

/// <summary>
/// Схема модуля: две таблицы.
/// </summary>
/// <remarks>
/// Общий контекст не знает ни одной сущности — он собирает то, что объявили модули
/// (<see cref="IEntityConfigurationProvider"/>). Отсюда и вся конфигурация здесь, а не в
/// <c>Shared</c>.
///
/// ⚠ <b>Внешнего ключа на таблицу людей нет, и это правило 1.</b> Таблица принадлежит
/// соседнему модулю; ключ на неё означал бы, что схема этого модуля не разворачивается без
/// него. Каскадное удаление, которое ключ давал прежде, заменено явным вызовом
/// <c>ITokenRevoker</c> при удалении человека — и это <b>не</b> ухудшение: удаление человека
/// в системе мягкое, то есть каскад всё равно не срабатывал.
/// </remarks>
internal sealed class AuthTokenEntities : IEntityConfigurationProvider
{
    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<AuthToken>(token =>
        {
            token.ToTable(TokenTables.Tokens, table => table.HasCheckConstraint(
                "ck_auth_tokens_kind",
                $"kind IN ({Значения(TokenKinds.All)})"));

            token.HasKey(row => row.Id).HasName("pk_auth_tokens");

            token.Property(row => row.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            token.Property(row => row.UserId).HasColumnName("user_id").IsRequired();
            token.Property(row => row.Kind).HasColumnName("kind").HasMaxLength(32).IsRequired();
            token.Property(row => row.Name).HasColumnName("name").HasMaxLength(512);
            token.Property(row => row.Address).HasColumnName("address").HasColumnType("inet");
            token.Property(row => row.StepUpAt).HasColumnName("step_up_at");
            token.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            token.Property(row => row.LastSeenAt).HasColumnName("last_seen_at").HasDefaultValueSql("now()");
            token.Property(row => row.ExpiresAt).HasColumnName("expires_at").IsRequired();
            token.Property(row => row.RevokedAt).HasColumnName("revoked_at");
            token.Property(row => row.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(64);

            // Список «где я вошёл» и массовый отзыв ходят по владельцу и виду; отозванные в
            // выдаче не участвуют, поэтому индекс частичный.
            token.HasIndex(row => new { row.UserId, row.Kind })
                .HasDatabaseName("ix_auth_tokens_user_kind")
                .HasFilter("revoked_at IS NULL");


            // ⚠ Индексы под уборку: выборка «что удалять» без индекса — полный проход по самой
            // растущей таблице модуля, и делается он ежедневно.
            token.HasIndex(row => row.ExpiresAt).HasDatabaseName("ix_auth_tokens_expires");
            token.HasIndex(row => row.RevokedAt).HasDatabaseName("ix_auth_tokens_revoked");
        });

        model.Entity<AuthTokenSecret>(secret =>
        {
            secret.ToTable(TokenTables.Secrets, table => table.HasCheckConstraint(
                "ck_auth_token_secrets_kind", $"kind IN ({Значения(SecretKinds.All)})"));

            secret.HasKey(row => row.Id).HasName("pk_auth_token_secrets");

            secret.Property(row => row.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            secret.Property(row => row.TokenId).HasColumnName("token_id").IsRequired();
            secret.Property(row => row.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
            secret.Property(row => row.Hash).HasColumnName("hash").IsRequired();
            secret.Property(row => row.IssuedAt).HasColumnName("issued_at").HasDefaultValueSql("now()");
            secret.Property(row => row.ExpiresAt).HasColumnName("expires_at").IsRequired();
            secret.Property(row => row.UsedAt).HasColumnName("used_at");

            // ⚠ Ключ СВОЙ — обе таблицы наши, и здесь он законен и нужен: секрет без своей
            // строки — мусор, который никто не уберёт.
            secret.HasOne<AuthToken>()
                .WithMany()
                .HasForeignKey(row => row.TokenId)
                .HasConstraintName("fk_auth_token_secrets_token_id_auth_tokens")
                .OnDelete(DeleteBehavior.Cascade);

            // ⚠ Уникальный, а не просто быстрый. Совпадение хешей двух разных секретов
            // означало бы, что один открывает чужой доступ; вероятность ничтожна, но
            // ограничение стоит ноль, а последствие непоправимо.
            secret.HasIndex(row => row.Hash).HasDatabaseName("ux_auth_token_secrets_hash").IsUnique();


            // Отзыв ходит по своей строке.
            secret.HasIndex(row => row.TokenId).HasDatabaseName("ix_auth_token_secrets_token");

            // Уборка — по сроку.
            secret.HasIndex(row => row.ExpiresAt).HasDatabaseName("ix_auth_token_secrets_expires");
        });
    }

    /// <summary>Список значений для <c>CHECK</c>: собирается из констант, а не пишется дважды.</summary>
    /// <remarks>
    /// ⚠ Ограничение в базе, повторяющее список руками, разъезжается с кодом молча: добавил
    /// вид в перечисление, забыл строку в миграции — и вставка отвергается базой на живой
    /// установке. Здесь список один, и миграция берёт его же.
    /// </remarks>
    private static string Значения(IEnumerable<string> значения) =>
        string.Join(", ", значения.Select(значение => $"'{значение}'"));
}
