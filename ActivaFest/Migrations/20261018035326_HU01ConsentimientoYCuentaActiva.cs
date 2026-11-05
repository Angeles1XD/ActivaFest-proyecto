using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU01ConsentimientoYCuentaActiva : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AceptaPromociones",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AceptaTratamientoDatos",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Activo",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // las cuentas ya existentes siguen activas
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AceptaPromociones",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AceptaTratamientoDatos",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Activo",
                table: "AspNetUsers");
        }
    }
}
