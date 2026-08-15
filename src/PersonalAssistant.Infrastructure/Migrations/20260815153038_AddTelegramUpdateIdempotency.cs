using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAssistant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramUpdateIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessedTelegramUpdates",
                columns: table => new
                {
                    UpdateId = table.Column<int>(type: "integer", nullable: false),
                    ClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedTelegramUpdates", x => x.UpdateId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedTelegramUpdates");
        }
    }
}
