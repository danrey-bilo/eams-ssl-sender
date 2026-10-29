using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Migrations
{
    /// <summary>Имя отправителя — новое имя сервиса, если стояло прежнее умолчание; заданное руками не трогается.</summary>
    public partial class RenameSenderName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE system_settings SET value = '"EAMS ssl sender"'::jsonb, updated_at = now()
                WHERE key = 'mail.from_name' AND value = '"Email SSL Sender"'::jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE system_settings SET value = '"Email SSL Sender"'::jsonb, updated_at = now()
                WHERE key = 'mail.from_name' AND value = '"EAMS ssl sender"'::jsonb;
                """);
        }
    }
}
