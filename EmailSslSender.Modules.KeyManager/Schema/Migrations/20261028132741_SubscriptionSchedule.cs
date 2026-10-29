// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: необязательные колонки периода и времени отправки; NULL — как прежде.
// ОБРАТИМОСТЬ: `Down` снимает колонки и проверки; теряются только периоды и время отправки.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmailSslSender.Modules.KeyManager.Schema.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "active_from",
                table: "certificate_subscriptions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "active_until",
                table: "certificate_subscriptions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_slot_at",
                table: "certificate_subscriptions",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "send_time",
                table: "certificate_subscriptions",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                table: "certificate_subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_certificate_subscriptions_period",
                table: "certificate_subscriptions",
                sql: "active_from IS NULL OR active_until IS NULL OR active_until >= active_from");

            migrationBuilder.AddCheckConstraint(
                name: "ck_certificate_subscriptions_time_zone",
                table: "certificate_subscriptions",
                sql: "(active_from IS NULL AND active_until IS NULL AND send_time IS NULL AND time_zone IS NULL) OR (time_zone IS NOT NULL AND char_length(time_zone) BETWEEN 1 AND 64)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_certificate_subscriptions_period",
                table: "certificate_subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_certificate_subscriptions_time_zone",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "active_from",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "active_until",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "last_slot_at",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "send_time",
                table: "certificate_subscriptions");

            migrationBuilder.DropColumn(
                name: "time_zone",
                table: "certificate_subscriptions");
        }
    }
}
