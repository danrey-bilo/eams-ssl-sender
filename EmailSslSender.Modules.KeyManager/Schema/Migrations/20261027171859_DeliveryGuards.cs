// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: четыре новые колонки подписки с умолчаниями — защита от отката (sent_issued) и
// автоповтор, когда не дошло ни одно письмо (pending_serials, pending_full, auto_retries). У подписок до этой
// миграции даты выпуска нет — защита от отката включится с их следующей отправкой.
// ОБРАТИМОСТЬ: `Down` снимает колонки; теряются только эти отметки.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Modules.KeyManager.Schema.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "auto_retries",
                table: "certificate_subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "pending_full",
                table: "certificate_subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "pending_serials",
                table: "certificate_subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "sent_issued",
                table: "certificate_subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_retries",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_full",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_serials",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "sent_issued",
                table: "certificate_subscriptions");
        }
    }
}
