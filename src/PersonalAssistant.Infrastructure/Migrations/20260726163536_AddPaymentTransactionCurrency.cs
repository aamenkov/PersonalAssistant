using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAssistant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTransactionCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "PaymentTransactions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "RUB");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "PaymentTransactions");
        }
    }
}
