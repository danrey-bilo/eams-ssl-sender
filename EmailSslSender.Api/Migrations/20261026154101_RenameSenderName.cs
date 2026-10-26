using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Migrations
{
    /// <summary>
    /// Имя отправителя писем — новое имя сервиса, если стояло прежнее умолчание.
    /// </summary>
    /// <remarks>
    /// Сервис называется «EAMS ssl sender» (решение владельца 2026-10-26). Прежнее умолчание
    /// «Email SSL Sender» на живых установках лежит в <c>system_settings</c> сохранённым значением —
    /// форма почты записывала его вместе с остальными полями. Меняется ровно оно: имя, заданное
    /// администратором руками, не трогается.
    /// </remarks>
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
