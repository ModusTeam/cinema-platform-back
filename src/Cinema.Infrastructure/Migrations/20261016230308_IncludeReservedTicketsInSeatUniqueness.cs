using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IncludeReservedTicketsInSeatUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tickets_session_id_seat_id",
                table: "tickets");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_session_id_seat_id",
                table: "tickets",
                columns: new[] { "session_id", "seat_id" },
                unique: true,
                filter: "ticket_status IN (0, 1, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tickets_session_id_seat_id",
                table: "tickets");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_session_id_seat_id",
                table: "tickets",
                columns: new[] { "session_id", "seat_id" },
                unique: true,
                filter: "ticket_status IN (0, 1)");
        }
    }
}
