using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class Sprint5PublicidadComisiones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Comision",
                table: "Ordenes",
                type: "REAL",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "PorcentajeComision",
                table: "Ordenes",
                type: "REAL",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "Liquidaciones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrganizadorId = table.Column<string>(type: "TEXT", nullable: false),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Monto = table.Column<double>(type: "REAL", precision: 10, scale: 2, nullable: false),
                    Referencia = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Nota = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RegistradoPorId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Liquidaciones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Liquidaciones_AspNetUsers_OrganizadorId",
                        column: x => x.OrganizadorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Liquidaciones_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaquetesDestacado",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Dias = table.Column<int>(type: "INTEGER", nullable: false),
                    Precio = table.Column<double>(type: "REAL", precision: 10, scale: 2, nullable: false),
                    Prioridad = table.Column<int>(type: "INTEGER", nullable: false),
                    Activo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaquetesDestacado", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContratosDestacado",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Codigo = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    PaqueteId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrganizadorId = table.Column<string>(type: "TEXT", nullable: false),
                    Precio = table.Column<double>(type: "REAL", precision: 10, scale: 2, nullable: false),
                    Dias = table.Column<int>(type: "INTEGER", nullable: false),
                    Prioridad = table.Column<int>(type: "INTEGER", nullable: false),
                    Estado = table.Column<int>(type: "INTEGER", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FechaPago = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FechaInicio = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FechaFin = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProveedorPago = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    ReferenciaPago = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContratosDestacado", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContratosDestacado_AspNetUsers_OrganizadorId",
                        column: x => x.OrganizadorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContratosDestacado_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContratosDestacado_PaquetesDestacado_PaqueteId",
                        column: x => x.PaqueteId,
                        principalTable: "PaquetesDestacado",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "PaquetesDestacado",
                columns: new[] { "Id", "Activo", "Descripcion", "Dias", "Nombre", "Precio", "Prioridad" },
                values: new object[,]
                {
                    { 1, true, "Tu evento en la sección Destacados de la portada durante 3 días.", 3, "Impulso", 29.899999999999999, 1 },
                    { 2, true, "Una semana en Destacados, por delante de los paquetes Impulso.", 7, "Plus", 59.899999999999999, 2 },
                    { 3, true, "Dos semanas en los primeros lugares de Destacados.", 15, "Premium", 109.90000000000001, 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContratosDestacado_Codigo",
                table: "ContratosDestacado",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContratosDestacado_Estado_FechaFin",
                table: "ContratosDestacado",
                columns: new[] { "Estado", "FechaFin" });

            migrationBuilder.CreateIndex(
                name: "IX_ContratosDestacado_EventoId",
                table: "ContratosDestacado",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_ContratosDestacado_OrganizadorId",
                table: "ContratosDestacado",
                column: "OrganizadorId");

            migrationBuilder.CreateIndex(
                name: "IX_ContratosDestacado_PaqueteId",
                table: "ContratosDestacado",
                column: "PaqueteId");

            migrationBuilder.CreateIndex(
                name: "IX_Liquidaciones_EventoId",
                table: "Liquidaciones",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_Liquidaciones_OrganizadorId_EventoId",
                table: "Liquidaciones",
                columns: new[] { "OrganizadorId", "EventoId" });

            // Órdenes pagadas antes de existir las comisiones: se les asigna la tarifa inicial (8%) para que el reporte sea completo
            migrationBuilder.Sql(
                "UPDATE Ordenes SET PorcentajeComision = 8, Comision = round(Total * 0.08, 2) WHERE Estado = 1 AND PorcentajeComision = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContratosDestacado");

            migrationBuilder.DropTable(
                name: "Liquidaciones");

            migrationBuilder.DropTable(
                name: "PaquetesDestacado");

            migrationBuilder.DropColumn(
                name: "Comision",
                table: "Ordenes");

            migrationBuilder.DropColumn(
                name: "PorcentajeComision",
                table: "Ordenes");
        }
    }
}
