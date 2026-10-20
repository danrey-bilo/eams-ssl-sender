using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Shared.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Storage;

/// <summary>
/// Таблицы модуля в общей модели.
/// </summary>
/// <remarks>
/// Модуль объявляет своё хранение сам — общий контекст не знает ни одной сущности и
/// собирает конфигурации через этот контракт.
/// </remarks>
internal sealed class IdentityEntities : IEntityConfigurationProvider
{
    /// <summary>Имя таблицы пользователей. Публично внутри модуля — на неё смотрят миграция и тесты.</summary>
    public const string UsersTable = "users";

    /// <summary>Имя таблицы одноразовых ссылок.</summary>
    public const string TokensTable = "identity_tokens";

    /// <summary>Имя таблицы второго фактора.</summary>
    public const string TwoFactorTable = "user_two_factor";

    /// <summary>
    /// Частичный уникальный индекс по адресу.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Частичный, и это принципиально.</b> Обычный уникальный индекс не дал бы завести
    /// заново человека, которого однажды удалили: строка остаётся навсегда (удаление мягкое). Условие <c>deleted_at IS NULL</c> освобождает адрес вместе с
    /// удалением.
    /// </remarks>
    public const string EmailIndex = "ix_users_email_live";

    public void Configure(ModelBuilder model)
    {
        model.Entity<User>(user =>
        {
            user.ToTable(UsersTable);

            // Имена ограничений по соглашению проекта: pk_/ix_/ck_/fk_ плюс таблица — иначе
            // половина схемы называлась бы по-своему, а половина по-EF'ному.
            user.HasKey(row => row.Id).HasName("pk_users");

            user.Property(row => row.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");

            // `citext` вместо `text`: сравнение адресов регистронезависимо на уровне базы, а
            // не на уровне того, кто не забыл привести регистр. Расширение включает миграция.
            user.Property(row => row.Email).HasColumnName("email").HasColumnType("citext").IsRequired();

            user.Property(row => row.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            user.Property(row => row.PasswordHash).HasColumnName("password_hash").IsRequired();
            user.Property(row => row.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            user.Property(row => row.MustChangePassword).HasColumnName("must_change_password").HasDefaultValue(false);
            user.Property(row => row.PasswordChangedAt).HasColumnName("password_changed_at");
            user.Property(row => row.FailedLoginCount).HasColumnName("failed_login_count").HasDefaultValue(0);
            user.Property(row => row.LockedUntil).HasColumnName("locked_until");
            user.Property(row => row.ActivatedAt).HasColumnName("activated_at");
            user.Property(row => row.TwoFactorConfiguredAt).HasColumnName("two_factor_configured_at");
            user.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            user.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            user.Property(row => row.DeletedAt).HasColumnName("deleted_at");

            // ⚠ Обнуляемая колонка, и `null` — не «не заполнено», а третье состояние «как в
            // системе». Без него смена настройки установки не подействовала бы ни
            // на кого, у кого способ однажды записали.
            user.Property(row => row.SecondFactorMethod)
                .HasColumnName("second_factor_method").HasColumnType("text");

            user.HasIndex(row => row.Email)
                .HasDatabaseName(EmailIndex)
                .IsUnique()
                .HasFilter("deleted_at IS NULL");
        });

        model.Entity<IdentityToken>(token =>
        {
            // ⚠ Список назначений в БАЗЕ, а не только в коде: строка с чужим назначением никогда
            // не найдётся запросом и будет жить незамеченной. Строится из Purposes.All — новое
            // назначение не разойдётся с ограничением (разбор у TwoFactorTests).
            token.ToTable(TokensTable, table => table.HasCheckConstraint(
                "ck_identity_tokens_purpose",
                $"purpose IN ({string.Join(", ", Purposes.All.Select(p => $"'{p}'"))})"));

            token.HasKey(row => row.Id).HasName("pk_identity_tokens");

            // Внешний ключ есть: ссылка без владельца — мусор, а не исторический факт. Обе таблицы принадлежат одному модулю, так что связь
            // границ не нарушает.
            token.HasOne<User>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .HasConstraintName("fk_identity_tokens_user_id_users")
                .OnDelete(DeleteBehavior.Cascade);

            token.Property(row => row.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            token.Property(row => row.UserId).HasColumnName("user_id").IsRequired();
            token.Property(row => row.TokenHash).HasColumnName("token_hash").IsRequired();
            token.Property(row => row.Purpose).HasColumnName("purpose").IsRequired();
            token.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            token.Property(row => row.ExpiresAt).HasColumnName("expires_at").IsRequired();
            token.Property(row => row.UsedAt).HasColumnName("used_at");
            token.Property(row => row.Binding).HasColumnName("binding").HasMaxLength(128);

            // ⚠ Уникальный, и по одному хешу, а не по паре с назначением. Прежний индекс был
            // составным и неуникальным — то есть совпадение хешей двух ссылок база бы
            // пропустила, и одна ссылка открывала бы чужую учётную запись. Вероятность
            // ничтожна, ограничение стоит ноль, а последствие непоправимо.
            //
            // Поиск идёт по хешу и назначению сразу — уникальному индексу по хешу этого
            // достаточно: он отбирает одну строку, назначение проверяется по ней.
            token.HasIndex(row => row.TokenHash).HasDatabaseName("ix_identity_tokens_hash").IsUnique();

            // Гашение прежних ссылок при выдаче новой ходит по владельцу.
            token.HasIndex(row => row.UserId).HasDatabaseName("ix_identity_tokens_user");

            // Уборка просроченных ходит по сроку.
            token.HasIndex(row => row.ExpiresAt).HasDatabaseName("ix_identity_tokens_expires");
        });

        model.Entity<UserTwoFactor>(factor =>
        {
            factor.ToTable(TwoFactorTable);

            // Ключ — сам человек: фактор у него один, и вторая строка означала бы, что
            // непонятно, какой из двух секретов действует.
            factor.HasKey(row => row.UserId).HasName("pk_user_two_factor");

            factor.HasOne<User>()
                .WithOne()
                .HasForeignKey<UserTwoFactor>(row => row.UserId)
                .HasConstraintName("fk_user_two_factor_user_id_users")
                .OnDelete(DeleteBehavior.Cascade);

            factor.Property(row => row.UserId).HasColumnName("user_id");
            factor.Property(row => row.DataKey).HasColumnName("data_key").IsRequired();
            factor.Property(row => row.Secret).HasColumnName("secret").IsRequired();
            factor.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            factor.Property(row => row.ConfirmedAt).HasColumnName("confirmed_at");
            factor.Property(row => row.LastStep).HasColumnName("last_step").HasDefaultValue(-1L);
        });
    }
}
