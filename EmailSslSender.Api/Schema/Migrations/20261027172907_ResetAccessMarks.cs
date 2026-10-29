// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новая таблица отметок сброса доступа.
// ОБРАТИМОСТЬ: `Down` удаляет таблицу; сброс из окружения снова повторяется при каждом migrate.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Schema.Migrations
{
    /// <summary>Отметки применённого сброса доступа из окружения: HMAC пары «адрес + пароль», не сам пароль.</summary>
    public partial class ResetAccessMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE sender_reset_access_marks (
                    mark text PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS sender_reset_access_marks;");
        }
    }
}
