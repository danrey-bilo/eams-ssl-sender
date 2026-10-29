// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НАД ДАННЫМИ ПОДПИСОК НЕТ: удаляется настройка `keymanager.server_sha256`, если таблица есть.
// ОБРАТИМОСТЬ: `Down` ничего не возвращает — код значение больше не читает.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Modules.KeyManager.Schema.Migrations
{
    /// <inheritdoc />
    public partial class DropServerPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('system_settings') IS NOT NULL THEN
                        DELETE FROM system_settings WHERE key = 'keymanager.server_sha256';
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
