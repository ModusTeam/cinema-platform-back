using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeparateSessionScreeningAndOccupancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve the recorded legacy boundary without inferring screening time.
            // PostgreSQL updates the existing overlap index's column reference too.
            migrationBuilder.RenameColumn(
                name: "end_time",
                table: "sessions",
                newName: "occupancy_end_time");

            migrationBuilder.AddColumn<DateTime>(
                name: "end_time",
                table: "sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "session_screening_end_valid",
                table: "sessions",
                sql: "end_time IS NULL OR (end_time > start_time AND end_time <= occupancy_end_time)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "session_screening_end_valid",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "end_time",
                table: "sessions");

            migrationBuilder.RenameColumn(
                name: "occupancy_end_time",
                table: "sessions",
                newName: "end_time");
        }
    }
}
