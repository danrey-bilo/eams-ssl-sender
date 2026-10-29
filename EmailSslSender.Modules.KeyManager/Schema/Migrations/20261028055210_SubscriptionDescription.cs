// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: необязательная колонка `description`.
// ОБРАТИМОСТЬ: `Down` снимает колонку и проверку; теряются только описания.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Modules.KeyManager.Schema.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "certificate_subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_certificate_subscriptions_description",
                table: "certificate_subscriptions",
                sql: "description IS NULL OR char_length(description) BETWEEN 1 AND 500");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_certificate_subscriptions_description",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "description",
                table: "certificate_subscriptions");
        }
    }
}
