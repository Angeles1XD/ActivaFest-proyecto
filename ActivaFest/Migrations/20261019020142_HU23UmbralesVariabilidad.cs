using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU23UmbralesVariabilidad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UmbralesVariabilidad",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CvEstable = table.Column<double>(type: "REAL", nullable: false),
                    CvInestable = table.Column<double>(type: "REAL", nullable: false),
                    OcupacionBaja = table.Column<double>(type: "REAL", nullable: false),
                    OcupacionAlta = table.Column<double>(type: "REAL", nullable: false),
                    ActualizadoEn = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActualizadoPorId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UmbralesVariabilidad", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UmbralesVariabilidad");
        }
    }
}
