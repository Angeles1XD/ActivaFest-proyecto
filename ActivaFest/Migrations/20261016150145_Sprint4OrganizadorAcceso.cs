using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class Sprint4OrganizadorAcceso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Visitas",
                table: "Eventos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ValidadoPorId",
                table: "Entradas",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Visitas",
                table: "Eventos");

            migrationBuilder.DropColumn(
                name: "ValidadoPorId",
                table: "Entradas");
        }
    }
}
