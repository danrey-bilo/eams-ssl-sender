// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: подписка переходит с одного Common Name на список — прежнее имя становится
// первым и единственным элементом, отправленный серийный номер — отметкой этого имени.
// ОБРАТИМОСТЬ: `Down` возвращает одно имя — первое из списка; остальные сертификаты подписки теряются.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Modules.KeyManager.Schema.Migrations
{
    /// <summary>
    /// Подписка на несколько сертификатов (решение владельца 2026-10-27): список Common Name, отправленный
    /// серийный номер — по каждому имени, отдельная отметка полной отправки для расписания.
    /// </summary>
    public partial class SubscriptionCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE certificate_subscriptions
                    ADD COLUMN common_names text[],
                    ADD COLUMN sent_serials jsonb,
                    ADD COLUMN last_full_sent_at timestamptz;

                UPDATE certificate_subscriptions
                SET common_names = ARRAY[common_name],
                    sent_serials = CASE
                        WHEN last_sent_serial IS NULL THEN '{}'::jsonb
                        ELSE jsonb_build_object(common_name, last_sent_serial)
                    END,
                    last_full_sent_at = last_sent_at;

                ALTER TABLE certificate_subscriptions
                    ALTER COLUMN common_names SET NOT NULL,
                    ALTER COLUMN sent_serials SET NOT NULL,
                    ADD CONSTRAINT ck_certificate_subscriptions_common_names
                        CHECK (cardinality(common_names) BETWEEN 1 AND 50);

                DROP INDEX ix_certificate_subscriptions_common_name;

                ALTER TABLE certificate_subscriptions
                    DROP COLUMN common_name,
                    DROP COLUMN last_sent_serial;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE certificate_subscriptions
                    ADD COLUMN common_name text,
                    ADD COLUMN last_sent_serial text;

                UPDATE certificate_subscriptions
                SET common_name = common_names[1],
                    last_sent_serial = sent_serials ->> common_names[1];

                ALTER TABLE certificate_subscriptions
                    ALTER COLUMN common_name SET NOT NULL,
                    DROP CONSTRAINT ck_certificate_subscriptions_common_names,
                    DROP COLUMN common_names,
                    DROP COLUMN sent_serials,
                    DROP COLUMN last_full_sent_at;

                CREATE INDEX ix_certificate_subscriptions_common_name ON certificate_subscriptions (common_name);
                """);
        }
    }
}
