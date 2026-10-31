// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: значение настройки добавляется, если его ещё не задавали.
// ОБРАТИМОСТЬ: `Down` убирает значение, только если оно по-прежнему русский.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Api.Schema.Migrations
{
    /// <summary>Язык установки — русский, а не английский по умолчанию модуля: его видит, кто себе язык не выбрал.</summary>
    public partial class InterfaceLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO system_settings (key, value, updated_at)
                VALUES ('ui.locale', '"ru"'::jsonb, now())
                ON CONFLICT (key) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM system_settings WHERE key = 'ui.locale' AND value = '"ru"'::jsonb;
                """);
        }
    }
}
