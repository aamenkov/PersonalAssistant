using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAssistant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reminders_RecurringPaymentId_DueDate_LocalDate_Kind",
                table: "Reminders");

            migrationBuilder.AlterColumn<DateTime>(
                name: "SentAtUtc",
                table: "Reminders",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Reminders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                table: "Reminders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAtUtc",
                table: "Reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptAtUtc",
                table: "Reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntilUtc",
                table: "Reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAtUtc",
                table: "Reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledAtUtc",
                table: "Reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlotKey",
                table: "Reminders",
                type: "character varying(48)",
                maxLength: 48,
                nullable: false,
                defaultValue: "daily");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Reminders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TelegramMessageId",
                table: "Reminders",
                type: "integer",
                nullable: true);

            // The old worker wrote SentAtUtc before making the Telegram request.
            migrationBuilder.Sql("""
                UPDATE "Reminders" SET "ClaimedAtUtc" = "SentAtUtc", "SentAtUtc" = NULL
                """);
            migrationBuilder.AlterColumn<DateTime>(
                name: "ClaimedAtUtc",
                table: "Reminders",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_RecurringPaymentId_DueDate_LocalDate_Kind_SlotKey",
                table: "Reminders",
                columns: new[] { "RecurringPaymentId", "DueDate", "LocalDate", "Kind", "SlotKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_Status_LeaseUntilUtc",
                table: "Reminders",
                columns: new[] { "Status", "LeaseUntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Reminders" WHERE "Status" <> 0 OR "SlotKey" <> 'daily') THEN
                        RAISE EXCEPTION 'The old reminder schema cannot preserve new delivery records. Restore a tested pre-upgrade backup instead.';
                    END IF;
                END $$;
                UPDATE "Reminders" SET "SentAtUtc" = "ClaimedAtUtc";
                """);

            migrationBuilder.DropIndex(
                name: "IX_Reminders_RecurringPaymentId_DueDate_LocalDate_Kind_SlotKey",
                table: "Reminders");

            migrationBuilder.DropIndex(
                name: "IX_Reminders_Status_LeaseUntilUtc",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "ClaimedAtUtc",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "LastAttemptAtUtc",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "LeaseUntilUtc",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "ScheduledAtUtc",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "SlotKey",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "TelegramMessageId",
                table: "Reminders");

            migrationBuilder.AlterColumn<DateTime>(
                name: "SentAtUtc",
                table: "Reminders",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_RecurringPaymentId_DueDate_LocalDate_Kind",
                table: "Reminders",
                columns: new[] { "RecurringPaymentId", "DueDate", "LocalDate", "Kind" },
                unique: true);
        }
    }
}
