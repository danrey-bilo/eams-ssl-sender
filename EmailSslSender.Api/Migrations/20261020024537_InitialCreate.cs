using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Migrations
{
    /// <summary>Начальная схема 1.x: учётные записи, сессии, настройки, секреты, очередь писем.</summary>
    /// <remarks>⚠ <c>Down</c> сносит все таблицы — откат только из копии.</remarks>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Тип citext должен существовать до создания таблицы.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS citext");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "auth_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    address = table.Column<IPAddress>(type: "inet", nullable: true),
                    step_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_tokens", x => x.id);
                    table.CheckConstraint("ck_auth_tokens_kind", "kind IN ('session')");
                });

            migrationBuilder.CreateTable(
                name: "mail_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_address = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<byte[]>(type: "bytea", nullable: false),
                    data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_secrets",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    @sealed = table.Column<byte[]>(name: "sealed", type: "bytea", nullable: false),
                    data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_secrets", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    email = table.Column<string>(type: "citext", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    password_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    two_factor_configured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    second_factor_method = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "auth_token_secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_token_secrets", x => x.id);
                    table.CheckConstraint("ck_auth_token_secrets_kind", "kind IN ('access', 'refresh')");
                    table.ForeignKey(
                        name: "fk_auth_token_secrets_token_id_auth_tokens",
                        column: x => x.token_id,
                        principalTable: "auth_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    binding = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_tokens", x => x.id);
                    table.CheckConstraint("ck_identity_tokens_purpose", "purpose IN ('activation', 'reset', 'two_factor', 'login_code', 'step_up_code')");
                    table.ForeignKey(
                        name: "fk_identity_tokens_user_id_users",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_two_factor",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_step = table.Column<long>(type: "bigint", nullable: false, defaultValue: -1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_two_factor", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_user_two_factor_user_id_users",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_token_secrets_expires",
                table: "auth_token_secrets",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_token_secrets_token",
                table: "auth_token_secrets",
                column: "token_id");

            migrationBuilder.CreateIndex(
                name: "ux_auth_token_secrets_hash",
                table: "auth_token_secrets",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_expires",
                table: "auth_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_revoked",
                table: "auth_tokens",
                column: "revoked_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_user_kind",
                table: "auth_tokens",
                columns: new[] { "user_id", "kind" },
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_identity_tokens_expires",
                table: "identity_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_identity_tokens_hash",
                table: "identity_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_tokens_user",
                table: "identity_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_mail_outbox_pending",
                table: "mail_outbox",
                column: "next_attempt_at",
                filter: "sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mail_outbox_rate",
                table: "mail_outbox",
                columns: new[] { "to_address", "kind", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_users_email_live",
                table: "users",
                column: "email",
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_token_secrets");

            migrationBuilder.DropTable(
                name: "identity_tokens");

            migrationBuilder.DropTable(
                name: "mail_outbox");

            migrationBuilder.DropTable(
                name: "system_secrets");

            migrationBuilder.DropTable(
                name: "system_settings");

            migrationBuilder.DropTable(
                name: "user_two_factor");

            migrationBuilder.DropTable(
                name: "auth_tokens");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
