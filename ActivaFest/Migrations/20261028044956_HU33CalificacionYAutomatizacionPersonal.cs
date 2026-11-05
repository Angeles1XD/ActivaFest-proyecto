using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU33CalificacionYAutomatizacionPersonal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Observacion",
                table: "SolicitudesPersonal",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RespuestaAutomatica",
                table: "SolicitudesPersonal",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinalizadaEn",
                table: "AsignacionesAcceso",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CalificacionesPersonal",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntradaId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    PersonalId = table.Column<string>(type: "TEXT", nullable: false),
                    UsuarioId = table.Column<string>(type: "TEXT", nullable: false),
                    Puntaje = table.Column<int>(type: "INTEGER", nullable: false),
                    Comentario = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalificacionesPersonal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalificacionesPersonal_AspNetUsers_PersonalId",
                        column: x => x.PersonalId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalificacionesPersonal_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalificacionesPersonal_Entradas_EntradaId",
                        column: x => x.EntradaId,
                        principalTable: "Entradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalificacionesPersonal_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalificacionesPersonal_EntradaId",
                table: "CalificacionesPersonal",
                column: "EntradaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalificacionesPersonal_EventoId",
                table: "CalificacionesPersonal",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_CalificacionesPersonal_PersonalId",
                table: "CalificacionesPersonal",
                column: "PersonalId");

            migrationBuilder.CreateIndex(
                name: "IX_CalificacionesPersonal_UsuarioId",
                table: "CalificacionesPersonal",
                column: "UsuarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalificacionesPersonal");

            migrationBuilder.DropColumn(
                name: "Observacion",
                table: "SolicitudesPersonal");

            migrationBuilder.DropColumn(
                name: "RespuestaAutomatica",
                table: "SolicitudesPersonal");

            migrationBuilder.DropColumn(
                name: "FinalizadaEn",
                table: "AsignacionesAcceso");
        }
    }
}
