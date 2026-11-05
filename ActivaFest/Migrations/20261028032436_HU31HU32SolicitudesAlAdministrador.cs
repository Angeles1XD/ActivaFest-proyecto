using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU31HU32SolicitudesAlAdministrador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OrganizadorHasta",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SolicitudesOrganizador",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UsuarioId = table.Column<string>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    NombreOrganizador = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Documento = table.Column<string>(type: "TEXT", maxLength: 11, nullable: false),
                    Telefono = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DescripcionEventos = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    WebORedes = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PlazoSolicitado = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Estado = table.Column<int>(type: "INTEGER", nullable: false),
                    FechaSolicitud = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FechaRespuesta = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RespondidaPorId = table.Column<string>(type: "TEXT", nullable: true),
                    PlazoAprobado = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    HabilitadoHasta = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MotivoRechazo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesOrganizador", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolicitudesOrganizador_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SolicitudesPersonal",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrganizadorId = table.Column<string>(type: "TEXT", nullable: false),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Correos = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Mensaje = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Estado = table.Column<int>(type: "INTEGER", nullable: false),
                    FechaSolicitud = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FechaRespuesta = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RespondidaPorId = table.Column<string>(type: "TEXT", nullable: true),
                    MotivoRechazo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesPersonal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolicitudesPersonal_AspNetUsers_OrganizadorId",
                        column: x => x.OrganizadorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SolicitudesPersonal_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesOrganizador_Estado_FechaSolicitud",
                table: "SolicitudesOrganizador",
                columns: new[] { "Estado", "FechaSolicitud" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesOrganizador_UsuarioId",
                table: "SolicitudesOrganizador",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesPersonal_Estado_FechaSolicitud",
                table: "SolicitudesPersonal",
                columns: new[] { "Estado", "FechaSolicitud" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesPersonal_EventoId",
                table: "SolicitudesPersonal",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesPersonal_OrganizadorId",
                table: "SolicitudesPersonal",
                column: "OrganizadorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolicitudesOrganizador");

            migrationBuilder.DropTable(
                name: "SolicitudesPersonal");

            migrationBuilder.DropColumn(
                name: "OrganizadorHasta",
                table: "AspNetUsers");
        }
    }
}
