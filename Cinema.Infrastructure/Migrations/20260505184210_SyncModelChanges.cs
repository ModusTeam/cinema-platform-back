using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_orders_users_user_id",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "fk_refresh_tokens_users_user_id",
                table: "refresh_tokens");

            migrationBuilder.DropForeignKey(
                name: "fk_seat_locks_users_user_id",
                table: "seat_locks");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "movies",
                type: "vector",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_status_booking_date",
                table: "orders",
                columns: new[] { "status", "booking_date" });

            migrationBuilder.AddForeignKey(
                name: "fk_orders_user_user_id",
                table: "orders",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_refresh_tokens_user_user_id",
                table: "refresh_tokens",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_seat_locks_user_user_id",
                table: "seat_locks",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_orders_user_user_id",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "fk_refresh_tokens_user_user_id",
                table: "refresh_tokens");

            migrationBuilder.DropForeignKey(
                name: "fk_seat_locks_user_user_id",
                table: "seat_locks");

            migrationBuilder.DropIndex(
                name: "ix_orders_status_booking_date",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "embedding",
                table: "movies");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddForeignKey(
                name: "fk_orders_users_user_id",
                table: "orders",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_refresh_tokens_users_user_id",
                table: "refresh_tokens",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_seat_locks_users_user_id",
                table: "seat_locks",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
