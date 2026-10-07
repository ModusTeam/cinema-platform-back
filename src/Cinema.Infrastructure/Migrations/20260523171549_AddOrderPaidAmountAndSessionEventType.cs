using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderPaidAmountAndSessionEventType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "event_type",
                table: "sessions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "STANDARD");

            migrationBuilder.AddColumn<decimal>(
                name: "paid_amount",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "event_type",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "paid_amount",
                table: "orders");
        }
    }
}
