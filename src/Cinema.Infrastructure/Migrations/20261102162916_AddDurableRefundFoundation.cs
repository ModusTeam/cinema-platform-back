using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableRefundFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refund_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    admission_evidence = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dispatch_started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_operations", x => x.id);
                    table.CheckConstraint("refund_admission_object", "jsonb_typeof(admission_evidence::jsonb) = 'object'");
                    table.CheckConstraint("refund_amount_positive", "amount > 0");
                    table.CheckConstraint("refund_approval_time", "approved_at_utc >= paid_at_utc");
                    table.CheckConstraint("refund_attempts", "attempts >= 0 AND attempts <= 8");
                    table.CheckConstraint("refund_claim_pair", "(lease_token IS NULL) = (lease_expires_at_utc IS NULL)");
                    table.CheckConstraint("refund_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("refund_key_identity", "provider_key = 'refund:' || replace(id::text, '-', '')");
                    table.CheckConstraint("refund_state", "state IN ('Approved','Dispatching','Pending','Unknown','Succeeded','Declined','ReconciliationRequired')");
                    table.ForeignKey(
                        name: "fk_refund_operations_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_provider_observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    evidence_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    observed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_provider_observations", x => x.id);
                    table.CheckConstraint("refund_observation_outcome", "outcome IN ('Pending','Unknown','Succeeded','Declined','ReconciliationRequired')");
                    table.ForeignKey(
                        name: "fk_refund_provider_observations_refund_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "refund_operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refund_operations_order_id",
                table: "refund_operations",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_operations_provider_key",
                table: "refund_operations",
                column: "provider_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refund_operations_state_next_attempt_at_utc",
                table: "refund_operations",
                columns: new[] { "state", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_refund_provider_observations_operation_id_claim_token",
                table: "refund_provider_observations",
                columns: new[] { "operation_id", "claim_token" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION protect_refund_operation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'refund operations are retained'; END IF;
                  IF (to_jsonb(NEW) - ARRAY['state','lease_token','lease_expires_at_utc','dispatch_started_at_utc','next_attempt_at_utc','attempts','last_code'])
                     IS DISTINCT FROM
                     (to_jsonb(OLD) - ARRAY['state','lease_token','lease_expires_at_utc','dispatch_started_at_utc','next_attempt_at_utc','attempts','last_code'])
                  THEN RAISE EXCEPTION 'refund approval identity is immutable'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER protect_refund_operation BEFORE UPDATE OR DELETE ON refund_operations
                  FOR EACH ROW EXECUTE FUNCTION protect_refund_operation();
                CREATE FUNCTION protect_refund_observation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'refund provider observations are immutable'; END $$;
                CREATE TRIGGER protect_refund_observation BEFORE UPDATE OR DELETE ON refund_provider_observations
                  FOR EACH ROW EXECUTE FUNCTION protect_refund_observation();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER protect_refund_observation ON refund_provider_observations; DROP FUNCTION protect_refund_observation(); DROP TRIGGER protect_refund_operation ON refund_operations; DROP FUNCTION protect_refund_operation();");
            migrationBuilder.DropTable(
                name: "refund_provider_observations");

            migrationBuilder.DropTable(
                name: "refund_operations");
        }
    }
}
