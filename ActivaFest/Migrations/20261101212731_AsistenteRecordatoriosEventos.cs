using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class AsistenteRecordatoriosEventos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecordatoriosEvento",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    UsuarioId = table.Column<string>(type: "TEXT", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EnviadoEn = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordatoriosEvento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecordatoriosEvento_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecordatoriosEvento_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordatoriosEvento_EnviadoEn",
                table: "RecordatoriosEvento",
                column: "EnviadoEn");

            migrationBuilder.CreateIndex(
                name: "IX_RecordatoriosEvento_EventoId_UsuarioId",
                table: "RecordatoriosEvento",
                columns: new[] { "EventoId", "UsuarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecordatoriosEvento_UsuarioId",
                table: "RecordatoriosEvento",
                column: "UsuarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordatoriosEvento");
        }
    }
}
