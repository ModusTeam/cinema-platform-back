using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerFactsSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "customer_facts_revision",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "dob_first_set_at_utc",
                table: "AspNetUsers",
                type: "timestamp(3) with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "registered_at_utc",
                table: "AspNetUsers",
                type: "timestamp(3) with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customer_fact_revisions",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_fact_revisions", x => x.event_id);
                    table.CheckConstraint("customer_fact_revision_positive", "revision > 0");
                    table.CheckConstraint("customer_fact_snapshot_identity", "(snapshot::jsonb->'data'->>'eventId') IS NOT NULL AND (snapshot::jsonb->'data'->>'userId') IS NOT NULL AND (snapshot::jsonb->'data'->>'revision') IS NOT NULL AND (snapshot::jsonb->'data'->>'eventId')::uuid = event_id AND (snapshot::jsonb->'data'->>'userId')::uuid = user_id AND (snapshot::jsonb->'data'->>'revision')::integer = revision");
                    table.ForeignKey(
                        name: "fk_customer_fact_revisions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "customer_facts_head_valid",
                table: "AspNetUsers",
                sql: "customer_facts_revision >= 0 AND (registered_at_utc IS NULL OR customer_facts_revision > 0) AND (dob_first_set_at_utc IS NULL OR (date_of_birth IS NOT NULL AND customer_facts_revision > 0))");

            migrationBuilder.CreateIndex(
                name: "ix_customer_fact_revisions_user_id_revision",
                table: "customer_fact_revisions",
                columns: new[] { "user_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_fact_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "customer_facts_head_valid",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "customer_facts_revision",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "dob_first_set_at_utc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "registered_at_utc",
                table: "AspNetUsers");
        }
    }
}
