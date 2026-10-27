// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новая таблица отметок применённого сброса доступа.
// ОБРАТИМОСТЬ: `Down` удаляет таблицу; после отката сброс из переменных окружения снова повторяется при каждом migrate.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Schema.Migrations
{
    /// <summary>
    /// Отметки применённого сброса доступа из переменных окружения (ревью безопасности 2026-10-27).
    /// </summary>
    /// <remarks>
    /// Установка через Portainer сбрасывает доступ переменными <c>SENDER_RESET_ACCESS_*</c>, а служба
    /// <c>migrate</c> выполняется при каждом обновлении стека: пока переменные забыты в стеке, каждое
    /// обновление снова меняло пароль, снимало аутентификатор и закрывало сессии — и знающий пароль из
    /// переменных входил первым и подключал свой аутентификатор. Теперь сброс с той же парой «адрес +
    /// пароль» применяется один раз: здесь — HMAC от пары под ключом из мастер-ключа, не сам пароль.
    /// </remarks>
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
