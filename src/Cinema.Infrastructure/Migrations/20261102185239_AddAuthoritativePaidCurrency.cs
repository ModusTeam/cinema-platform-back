using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthoritativePaidCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "paid_currency",
                table: "orders",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "order_paid_currency",
                table: "orders",
                sql: "paid_currency IS NULL OR (paid_currency ~ '^[A-Z]{3}$' AND paid_amount > 0 AND paid_at IS NOT NULL AND payment_transaction_id IS NOT NULL)");
            migrationBuilder.Sql("""
                CREATE FUNCTION protect_order_paid_currency() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF (OLD.paid_at IS NOT NULL OR OLD.payment_transaction_id IS NOT NULL)
                     AND NEW.paid_currency IS DISTINCT FROM OLD.paid_currency
                  THEN RAISE EXCEPTION 'confirmed payment currency is immutable'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER protect_order_paid_currency BEFORE UPDATE OF paid_currency ON orders
                  FOR EACH ROW EXECUTE FUNCTION protect_order_paid_currency();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER protect_order_paid_currency ON orders; DROP FUNCTION protect_order_paid_currency();");
            migrationBuilder.DropCheckConstraint(
                name: "order_paid_currency",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "paid_currency",
                table: "orders");
        }
    }
}
