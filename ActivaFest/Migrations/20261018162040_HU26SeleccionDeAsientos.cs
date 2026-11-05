using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU26SeleccionDeAsientos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AsientoId",
                table: "OrdenItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AsientoId",
                table: "Entradas",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrdenItems_AsientoId",
                table: "OrdenItems",
                column: "AsientoId");

            migrationBuilder.CreateIndex(
                name: "IX_Entradas_AsientoId",
                table: "Entradas",
                column: "AsientoId",
                unique: true,
                filter: "AsientoId IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Entradas_Asientos_AsientoId",
                table: "Entradas",
                column: "AsientoId",
                principalTable: "Asientos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrdenItems_Asientos_AsientoId",
                table: "OrdenItems",
                column: "AsientoId",
                principalTable: "Asientos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Entradas_Asientos_AsientoId",
                table: "Entradas");

            migrationBuilder.DropForeignKey(
                name: "FK_OrdenItems_Asientos_AsientoId",
                table: "OrdenItems");

            migrationBuilder.DropIndex(
                name: "IX_OrdenItems_AsientoId",
                table: "OrdenItems");

            migrationBuilder.DropIndex(
                name: "IX_Entradas_AsientoId",
                table: "Entradas");

            migrationBuilder.DropColumn(
                name: "AsientoId",
                table: "OrdenItems");

            migrationBuilder.DropColumn(
                name: "AsientoId",
                table: "Entradas");
        }
    }
}
