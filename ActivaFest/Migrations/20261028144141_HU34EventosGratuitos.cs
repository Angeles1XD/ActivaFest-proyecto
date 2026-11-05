using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU34EventosGratuitos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsGratuito",
                table: "Eventos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PendientePagoPublicacion",
                table: "Eventos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "InteresesEvento",
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
                    table.PrimaryKey("PK_InteresesEvento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InteresesEvento_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InteresesEvento_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PagosPublicacion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Codigo = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrganizadorId = table.Column<string>(type: "TEXT", nullable: false),
                    Monto = table.Column<double>(type: "REAL", nullable: false),
                    Aforo = table.Column<int>(type: "INTEGER", nullable: false),
                    Estado = table.Column<int>(type: "INTEGER", nullable: false),
                    Proveedor = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Referencia = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FechaCreacion = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FechaPago = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PagosPublicacion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PagosPublicacion_AspNetUsers_OrganizadorId",
                        column: x => x.OrganizadorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PagosPublicacion_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InteresesEvento_EventoId_UsuarioId",
                table: "InteresesEvento",
                columns: new[] { "EventoId", "UsuarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InteresesEvento_UsuarioId",
                table: "InteresesEvento",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_PagosPublicacion_Codigo",
                table: "PagosPublicacion",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PagosPublicacion_EventoId_Estado",
                table: "PagosPublicacion",
                columns: new[] { "EventoId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_PagosPublicacion_OrganizadorId",
                table: "PagosPublicacion",
                column: "OrganizadorId");

            // Los eventos que ya existían con todas sus zonas a precio 0 pasan a ser de entrada libre (sin cobro de publicación)
            var verdadero = migrationBuilder.ActiveProvider?.Contains("Npgsql") == true ? "TRUE" : "1";
            migrationBuilder.Sql($@"UPDATE ""Eventos"" SET ""EsGratuito"" = {verdadero}
                WHERE EXISTS (SELECT 1 FROM ""TiposEntrada"" t WHERE t.""EventoId"" = ""Eventos"".""Id"")
                  AND NOT EXISTS (SELECT 1 FROM ""TiposEntrada"" t WHERE t.""EventoId"" = ""Eventos"".""Id"" AND t.""Precio"" > 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InteresesEvento");

            migrationBuilder.DropTable(
                name: "PagosPublicacion");

            migrationBuilder.DropColumn(
                name: "EsGratuito",
                table: "Eventos");

            migrationBuilder.DropColumn(
                name: "PendientePagoPublicacion",
                table: "Eventos");
        }
    }
}
