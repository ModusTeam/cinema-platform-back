using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGoldCheckoutJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "gold_checkout_state",
                table: "orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "gold_original_paid",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "gold_original_ticket_price",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "gold_original_total",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "gold_payment_outcome_unknown",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "gold_recovery_after",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "gold_ticket_id",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "gold_use_confirmed",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_orders_gold_checkout_state_gold_recovery_after",
                table: "orders",
                columns: new[] { "gold_checkout_state", "gold_recovery_after" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_gold_checkout_state_gold_recovery_after",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "gold_checkout_state",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "gold_original_paid",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "gold_original_ticket_price",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "gold_original_total",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "gold_payment_outcome_unknown",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "gold_recovery_after",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "gold_ticket_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "gold_use_confirmed",
                table: "orders");
        }
    }
}
