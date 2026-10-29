using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionCompletionSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_completion_facts",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_completion_facts", x => x.event_id);
                    table.CheckConstraint("session_completion_v1", "revision = 1");
                    table.ForeignKey(
                        name: "fk_session_completion_facts_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_end_time_id",
                table: "sessions",
                columns: new[] { "end_time", "id" },
                filter: "end_time IS NOT NULL AND status IN (0,1,2)");

            migrationBuilder.CreateIndex(
                name: "ix_session_completion_facts_session_id_revision",
                table: "session_completion_facts",
                columns: new[] { "session_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_completion_facts");

            migrationBuilder.DropIndex(
                name: "ix_sessions_end_time_id",
                table: "sessions");
        }
    }
}
