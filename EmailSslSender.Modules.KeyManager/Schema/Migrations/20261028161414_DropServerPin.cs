// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НАД ДАННЫМИ ПОДПИСОК НЕТ: убирается только строка настройки `keymanager.server_sha256`
// (закреплённый отпечаток сертификата Key Manager Plus) — с 2.5 её нет в коде, сертификат сервера проверяется
// цепочкой удостоверяющего центра. Таблица `system_settings` принадлежит модулю settings: её может не быть в базе
// тестов модуля, поэтому удаление — только при её наличии.
// ОБРАТИМОСТЬ: `Down` ничего не возвращает — прежнего значения нет, и код его больше не читает.
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
