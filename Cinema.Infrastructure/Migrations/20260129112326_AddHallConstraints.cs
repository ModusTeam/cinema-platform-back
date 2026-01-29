using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHallConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sessions_hall_id",
                table: "sessions");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Hall_Time_Overlap",
                table: "sessions",
                columns: new[] { "hall_id", "start_time", "end_time" });

            migrationBuilder.CreateIndex(
                name: "ix_halls_name",
                table: "halls",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_Hall_Time_Overlap",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "ix_halls_name",
                table: "halls");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_hall_id",
                table: "sessions",
                column: "hall_id");
        }
    }
}
