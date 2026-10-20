using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Migrations
{
    /// <inheritdoc />
    public partial class KeyManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "attachments",
                table: "mail_outbox",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "certificate_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    common_name = table.Column<string>(type: "text", nullable: false),
                    recipients = table.Column<string[]>(type: "text[]", nullable: false),
                    formats = table.Column<string[]>(type: "text[]", nullable: false),
                    include_passphrase = table.Column<bool>(type: "boolean", nullable: false),
                    on_change = table.Column<bool>(type: "boolean", nullable: false),
                    interval_days = table.Column<int>(type: "integer", nullable: true),
                    locale = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_sent_serial = table.Column<string>(type: "text", nullable: true),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_subscriptions", x => x.id);
                    table.CheckConstraint("ck_certificate_subscriptions_formats", "cardinality(formats) >= 1");
                    table.CheckConstraint("ck_certificate_subscriptions_interval", "interval_days IS NULL OR interval_days BETWEEN 1 AND 365");
                    table.CheckConstraint("ck_certificate_subscriptions_recipients", "cardinality(recipients) >= 1");
                });

            migrationBuilder.CreateTable(
                name: "keymanager_certificates",
                columns: table => new
                {
                    common_name = table.Column<string>(type: "text", nullable: false),
                    serial_number = table.Column<string>(type: "text", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_keymanager_certificates", x => new { x.common_name, x.serial_number });
                });

            migrationBuilder.CreateTable(
                name: "keymanager_state",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    api_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    data_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_poll_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_poll_error = table.Column<string>(type: "text", nullable: true),
                    last_poll_count = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_keymanager_state", x => x.id);
                    table.CheckConstraint("ck_keymanager_state_singleton", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_subscriptions_common_name",
                table: "certificate_subscriptions",
                column: "common_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certificate_subscriptions");

            migrationBuilder.DropTable(
                name: "keymanager_certificates");

            migrationBuilder.DropTable(
                name: "keymanager_state");

            migrationBuilder.DropColumn(
                name: "attachments",
                table: "mail_outbox");
        }
    }
}
