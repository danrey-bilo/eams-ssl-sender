// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: добавляется значение настройки, если администратор его ещё не задавал.
// ОБРАТИМОСТЬ: `Down` убирает значение, только если оно по-прежнему десять минут; заданное руками не трогается.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Schema.Migrations
{
    /// <summary>
    /// Окно свежего второго фактора — десять минут (решение владельца), а не пять по умолчанию модуля.
    /// </summary>
    /// <remarks>
    /// Выгрузка сертификата, ключ API, пароль SMTP, подписки и пользователи спрашивают код, если он
    /// вводился раньше окна. Десять минут прежней версии сохраняются и на новой установке: набор проекта
    /// выполняется на обеих дорогах. Значение остаётся настройкой — администратор меняет его на экране.
    /// </remarks>
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
