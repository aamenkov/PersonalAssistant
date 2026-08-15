using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAssistant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ImprovedPaymentSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLastDayOfMonth",
                table: "RecurringPayments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleDayOfMonth",
                table: "RecurringPayments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("UPDATE \"RecurringPayments\" SET \"ScheduleDayOfMonth\" = EXTRACT(DAY FROM \"NextPaymentDate\")::integer WHERE \"NextPaymentDate\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsLastDayOfMonth",
                table: "RecurringPayments");

            migrationBuilder.DropColumn(
                name: "ScheduleDayOfMonth",
                table: "RecurringPayments");
        }
    }
}
