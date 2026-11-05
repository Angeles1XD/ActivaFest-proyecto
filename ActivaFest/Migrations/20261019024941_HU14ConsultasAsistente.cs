using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU14ConsultasAsistente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsultasAsistente",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Pregunta = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Intencion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Resuelta = table.Column<bool>(type: "INTEGER", nullable: false),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: true),
                    ConSesion = table.Column<bool>(type: "INTEGER", nullable: false),
                    RedactadaPorLlm = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsultasAsistente", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsultasAsistente_Fecha",
                table: "ConsultasAsistente",
                column: "Fecha");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsultasAsistente");
        }
    }
}
