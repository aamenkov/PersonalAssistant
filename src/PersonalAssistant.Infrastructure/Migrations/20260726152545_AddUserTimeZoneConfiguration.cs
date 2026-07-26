using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAssistant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTimeZoneConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTimeZoneConfigured",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTimeZoneConfigured",
                table: "Users");
        }
    }
}
