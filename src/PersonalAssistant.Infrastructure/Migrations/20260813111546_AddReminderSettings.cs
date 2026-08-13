using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAssistant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ReminderTimeUtc",
                table: "Users",
                newName: "ReminderTimeLocal");

            migrationBuilder.AddColumn<int>(
                name: "ReminderDaysBefore",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.Sql("UPDATE \"Users\" SET \"ReminderDaysBefore\" = 3 WHERE \"ReminderDaysBefore\" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReminderDaysBefore",
                table: "Users");

            migrationBuilder.RenameColumn(
                name: "ReminderTimeLocal",
                table: "Users",
                newName: "ReminderTimeUtc");
        }
    }
}
