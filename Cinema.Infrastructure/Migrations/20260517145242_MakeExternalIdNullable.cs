using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinema.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeExternalIdNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_movies_external_id",
                table: "movies");

            migrationBuilder.AlterColumn<int>(
                name: "external_id",
                table: "movies",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.CreateIndex(
                name: "ix_movies_external_id",
                table: "movies",
                column: "external_id",
                unique: true,
                filter: "external_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_movies_external_id",
                table: "movies");

            migrationBuilder.AlterColumn<int>(
                name: "external_id",
                table: "movies",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_movies_external_id",
                table: "movies",
                column: "external_id",
                unique: true);
        }
    }
}
