using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAssistant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CriticalIntervalMinutes",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "CriticalWindowEnd",
                table: "Users",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "CriticalWindowStart",
                table: "Users",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderMode",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReminderOverdueMode",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PaymentReminderSettings",
                columns: table => new
                {
                    RecurringPaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Profile_Mode = table.Column<int>(type: "integer", nullable: true),
                    Profile_DaysBefore = table.Column<int>(type: "integer", nullable: true),
                    Profile_ReminderTimeLocal = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Profile_OverdueMode = table.Column<int>(type: "integer", nullable: true),
                    Profile_CriticalIntervalMinutes = table.Column<int>(type: "integer", nullable: true),
                    Profile_CriticalWindowStart = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Profile_CriticalWindowEnd = table.Column<TimeOnly>(type: "time without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReminderSettings", x => x.RecurringPaymentId);
                    table.ForeignKey(
                        name: "FK_PaymentReminderSettings_RecurringPayments_RecurringPaymentId",
                        column: x => x.RecurringPaymentId,
                        principalTable: "RecurringPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "PaymentReminderSettings")
                        OR EXISTS (SELECT 1 FROM "Users" WHERE "ReminderMode" <> 0 OR "ReminderOverdueMode" <> 0) THEN
                        RAISE EXCEPTION 'The old reminder schema cannot preserve configured reminder profiles. Restore a tested pre-upgrade backup instead.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "PaymentReminderSettings");

            migrationBuilder.DropColumn(
                name: "CriticalIntervalMinutes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CriticalWindowEnd",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CriticalWindowStart",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReminderMode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReminderOverdueMode",
                table: "Users");
        }
    }
}
