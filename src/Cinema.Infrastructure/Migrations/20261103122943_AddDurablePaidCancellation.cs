using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDurablePaidCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paid_cancellations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    admin_actor = table.Column<bool>(type: "boolean", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_reference = table.Column<string>(type: "text", nullable: true),
                    paid_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    paid_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    points_used = table.Column<int>(type: "integer", nullable: false),
                    gold_ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_snapshot = table.Column<string>(type: "text", nullable: false),
                    claimed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    money_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    points_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    gold_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    refund_reference = table.Column<string>(type: "text", nullable: true),
                    execution_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finalized_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paid_cancellations", x => x.id);
                    table.CheckConstraint("paid_cancellation_amount", "paid_amount >= 0 AND points_used >= 0");
                    table.CheckConstraint("paid_cancellation_currency", "paid_currency IS NULL OR paid_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("paid_cancellation_finalized", "finalized_at_utc IS NULL OR (money_state IN ('NotRequired','Succeeded') AND points_state IN ('NotRequired','Succeeded') AND gold_state IN ('NotRequired','Succeeded'))");
                    table.CheckConstraint("paid_cancellation_gold", "gold_state IN ('NotRequired','Ready','Dispatching','Succeeded') AND (gold_state = 'NotRequired') = (gold_ticket_id IS NULL)");
                    table.CheckConstraint("paid_cancellation_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND order_id <> '00000000-0000-0000-0000-000000000000'::uuid AND user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND session_id <> '00000000-0000-0000-0000-000000000000'::uuid AND (actor_id IS NOT NULL OR admin_actor) AND (actor_id IS NULL OR actor_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
                    table.CheckConstraint("paid_cancellation_lease", "(execution_token IS NULL) = (lease_expires_at_utc IS NULL)");
                    table.CheckConstraint("paid_cancellation_money", "money_state IN ('NotRequired','Ready','Dispatching','Succeeded','Unknown') AND (money_state = 'NotRequired') = (paid_amount = 0) AND (money_state = 'Succeeded') = (refund_reference IS NOT NULL AND length(btrim(refund_reference)) > 0)");
                    table.CheckConstraint("paid_cancellation_points", "points_state IN ('NotRequired','Ready','Dispatching','Succeeded') AND (points_state = 'NotRequired') = (points_used = 0)");
                    table.CheckConstraint("paid_cancellation_snapshot", "jsonb_typeof(source_snapshot::jsonb) = 'object'");
                    table.ForeignKey(
                        name: "fk_paid_cancellations_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_paid_cancellations_order_id",
                table: "paid_cancellations",
                column: "order_id",
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION arbitrate_paid_refund_owner() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  -- A real MVCC write also defeats a pre-existing REPEATABLE READ
                  -- snapshot: its competing owner must serialize or abort, rather
                  -- than miss the winner in a cross-table visibility check.
                  UPDATE orders SET id=id WHERE id=NEW.order_id;
                  IF TG_TABLE_NAME='paid_cancellations' THEN
                    IF EXISTS (SELECT 1 FROM refund_operations WHERE order_id=NEW.order_id)
                    THEN RAISE EXCEPTION 'refund lifecycle already owned' USING ERRCODE='23505'; END IF;
                  ELSE
                    IF EXISTS (SELECT 1 FROM paid_cancellations WHERE order_id=NEW.order_id)
                    THEN RAISE EXCEPTION 'cancellation lifecycle already owned' USING ERRCODE='23505'; END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER arbitrate_paid_cancellation BEFORE INSERT ON paid_cancellations
                  FOR EACH ROW EXECUTE FUNCTION arbitrate_paid_refund_owner();
                CREATE TRIGGER arbitrate_refund_operation BEFORE INSERT ON refund_operations
                  FOR EACH ROW EXECUTE FUNCTION arbitrate_paid_refund_owner();
                CREATE FUNCTION protect_paid_cancellation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'paid cancellation owners are retained'; END IF;
                  IF (to_jsonb(NEW) - ARRAY['money_state','points_state','gold_state','refund_reference','execution_token','lease_expires_at_utc','finalized_at_utc','last_code'])
                    IS DISTINCT FROM
                    (to_jsonb(OLD) - ARRAY['money_state','points_state','gold_state','refund_reference','execution_token','lease_expires_at_utc','finalized_at_utc','last_code'])
                  THEN RAISE EXCEPTION 'paid cancellation source is immutable'; END IF;
                  IF OLD.money_state='Succeeded' AND (NEW.money_state<>OLD.money_state OR NEW.refund_reference IS DISTINCT FROM OLD.refund_reference)
                     OR OLD.points_state='Succeeded' AND NEW.points_state<>OLD.points_state
                     OR OLD.gold_state='Succeeded' AND NEW.gold_state<>OLD.gold_state
                     OR OLD.finalized_at_utc IS NOT NULL AND NEW.finalized_at_utc IS DISTINCT FROM OLD.finalized_at_utc
                  THEN RAISE EXCEPTION 'successful cancellation evidence is retained'; END IF;
                  IF OLD.money_state IN ('Dispatching','Unknown') AND NEW.money_state='Ready'
                  THEN RAISE EXCEPTION 'uncertain money cannot be redispatched'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER protect_paid_cancellation BEFORE UPDATE OR DELETE ON paid_cancellations
                  FOR EACH ROW EXECUTE FUNCTION protect_paid_cancellation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER protect_paid_cancellation ON paid_cancellations;
                DROP FUNCTION protect_paid_cancellation();
                DROP TRIGGER arbitrate_paid_cancellation ON paid_cancellations;
                DROP TRIGGER arbitrate_refund_operation ON refund_operations;
                DROP FUNCTION arbitrate_paid_refund_owner();
                """);
            migrationBuilder.DropTable(
                name: "paid_cancellations");
        }
    }
}
