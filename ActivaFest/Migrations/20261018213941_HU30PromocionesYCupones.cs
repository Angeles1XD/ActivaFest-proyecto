using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class HU30PromocionesYCupones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CuponId",
                table: "Ordenes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Descuento",
                table: "Ordenes",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "Promociones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TipoDescuento = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Valor = table.Column<double>(type: "REAL", nullable: false),
                    FechaInicio = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FechaFin = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CriterioSegmento = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    LimiteUsos = table.Column<int>(type: "INTEGER", nullable: true),
                    Usos = table.Column<int>(type: "INTEGER", nullable: false),
                    Activa = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreadaPorId = table.Column<string>(type: "TEXT", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Promociones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Promociones_AspNetUsers_CreadaPorId",
                        column: x => x.CreadaPorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cupones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PromocionId = table.Column<int>(type: "INTEGER", nullable: false),
                    UsuarioId = table.Column<string>(type: "TEXT", nullable: false),
                    Codigo = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Usado = table.Column<bool>(type: "INTEGER", nullable: false),
                    OrdenId = table.Column<int>(type: "INTEGER", nullable: true),
                    FechaUso = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cupones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cupones_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Cupones_Ordenes_OrdenId",
                        column: x => x.OrdenId,
                        principalTable: "Ordenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Cupones_Promociones_PromocionId",
                        column: x => x.PromocionId,
                        principalTable: "Promociones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ordenes_CuponId",
                table: "Ordenes",
                column: "CuponId");

            migrationBuilder.CreateIndex(
                name: "IX_Cupones_Codigo",
                table: "Cupones",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cupones_OrdenId",
                table: "Cupones",
                column: "OrdenId",
                unique: true,
                filter: "OrdenId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Cupones_PromocionId_UsuarioId",
                table: "Cupones",
                columns: new[] { "PromocionId", "UsuarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cupones_UsuarioId",
                table: "Cupones",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Promociones_CreadaPorId",
                table: "Promociones",
                column: "CreadaPorId");

            migrationBuilder.AddForeignKey(
                name: "FK_Ordenes_Cupones_CuponId",
                table: "Ordenes",
                column: "CuponId",
                principalTable: "Cupones",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Ordenes_Cupones_CuponId",
                table: "Ordenes");

            migrationBuilder.DropTable(
                name: "Cupones");

            migrationBuilder.DropTable(
                name: "Promociones");

            migrationBuilder.DropIndex(
                name: "IX_Ordenes_CuponId",
                table: "Ordenes");

            migrationBuilder.DropColumn(
                name: "CuponId",
                table: "Ordenes");

            migrationBuilder.DropColumn(
                name: "Descuento",
                table: "Ordenes");
        }
    }
}
