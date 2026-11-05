using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU22AuditoriaAnalitica : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditoriasAnalitica",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdministradorId = table.Column<string>(type: "TEXT", nullable: false),
                    Consulta = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FuenteDatos = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditoriasAnalitica", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditoriasAnalitica_AspNetUsers_AdministradorId",
                        column: x => x.AdministradorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditoriasAnalitica_AdministradorId",
                table: "AuditoriasAnalitica",
                column: "AdministradorId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditoriasAnalitica_Fecha",
                table: "AuditoriasAnalitica",
                column: "Fecha");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditoriasAnalitica");
        }
    }
}
