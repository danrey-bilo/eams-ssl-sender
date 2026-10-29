// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: значение настройки добавляется, если его ещё не задавали.
// ОБРАТИМОСТЬ: `Down` убирает значение, только если оно по-прежнему десять минут.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Schema.Migrations
{
    /// <summary>Окно свежего второго фактора — десять минут, а не пять по умолчанию модуля.</summary>
    public partial class StepUpWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO system_settings (key, value, updated_at)
                VALUES ('auth.step_up_minutes', '10'::jsonb, now())
                ON CONFLICT (key) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM system_settings WHERE key = 'auth.step_up_minutes' AND value = '10'::jsonb;
                """);
        }
    }
}
