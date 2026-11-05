using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU17HistorialSegunModeloDeDatos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActividadesUsuario");

            migrationBuilder.CreateTable(
                name: "HistorialBusquedas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UsuarioId = table.Column<string>(type: "TEXT", nullable: false),
                    Termino = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistorialBusquedas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistorialBusquedas_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VisualizacionesEvento",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    UsuarioId = table.Column<string>(type: "TEXT", nullable: false),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisualizacionesEvento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisualizacionesEvento_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VisualizacionesEvento_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistorialBusquedas_UsuarioId_Fecha",
                table: "HistorialBusquedas",
                columns: new[] { "UsuarioId", "Fecha" });

            migrationBuilder.CreateIndex(
                name: "IX_VisualizacionesEvento_EventoId",
                table: "VisualizacionesEvento",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_VisualizacionesEvento_UsuarioId_Fecha",
                table: "VisualizacionesEvento",
                columns: new[] { "UsuarioId", "Fecha" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistorialBusquedas");

            migrationBuilder.DropTable(
                name: "VisualizacionesEvento");

            migrationBuilder.CreateTable(
                name: "ActividadesUsuario",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CategoriaId = table.Column<int>(type: "INTEGER", nullable: true),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: true),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Termino = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Tipo = table.Column<int>(type: "INTEGER", nullable: false),
                    UsuarioId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActividadesUsuario", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActividadesUsuario_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActividadesUsuario_UsuarioId_Fecha",
                table: "ActividadesUsuario",
                columns: new[] { "UsuarioId", "Fecha" });
        }
    }
}
