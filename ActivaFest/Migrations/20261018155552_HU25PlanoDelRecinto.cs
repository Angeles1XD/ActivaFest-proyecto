using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU25PlanoDelRecinto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AsientosNumerados",
                table: "TiposEntrada",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AsientosPorFila",
                table: "TiposEntrada",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "TiposEntrada",
                type: "TEXT",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Filas",
                table: "TiposEntrada",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PuertaIngreso",
                table: "TiposEntrada",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantillaPlano",
                table: "Eventos",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "CampoAbierto"); // los eventos existentes se venden por zona

            migrationBuilder.CreateTable(
                name: "Asientos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TipoEntradaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Fila = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Numero = table.Column<int>(type: "INTEGER", nullable: false),
                    PosX = table.Column<int>(type: "INTEGER", nullable: false),
                    PosY = table.Column<int>(type: "INTEGER", nullable: false),
                    Estado = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asientos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Asientos_TiposEntrada_TipoEntradaId",
                        column: x => x.TipoEntradaId,
                        principalTable: "TiposEntrada",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Asientos_TipoEntradaId_Fila_Numero",
                table: "Asientos",
                columns: new[] { "TipoEntradaId", "Fila", "Numero" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Asientos");

            migrationBuilder.DropColumn(
                name: "AsientosNumerados",
                table: "TiposEntrada");

            migrationBuilder.DropColumn(
                name: "AsientosPorFila",
                table: "TiposEntrada");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "TiposEntrada");

            migrationBuilder.DropColumn(
                name: "Filas",
                table: "TiposEntrada");

            migrationBuilder.DropColumn(
                name: "PuertaIngreso",
                table: "TiposEntrada");

            migrationBuilder.DropColumn(
                name: "PlantillaPlano",
                table: "Eventos");
        }
    }
}
