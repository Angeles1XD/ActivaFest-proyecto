using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class CategoriasColorImagen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Categorias",
                type: "TEXT",
                maxLength: 9,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Imagen",
                table: "Categorias",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#a855f7", "/img/categorias/conciertos.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#ec4899", "/img/categorias/teatro.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#f97316", "/img/categorias/festivales.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#f59e0b", "/img/categorias/ferias.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#06b6d4", "/img/categorias/exposiciones.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#22c55e", "/img/categorias/deportes.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 7,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#ef4444", "/img/categorias/gastronomia.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 8,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#14b8a6", "/img/categorias/familiar.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 9,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#3b82f6", "/img/categorias/cultura.jpg" });

            migrationBuilder.UpdateData(
                table: "Categorias",
                keyColumn: "Id",
                keyValue: 10,
                columns: new[] { "Color", "Imagen" },
                values: new object[] { "#8b5cf6", "/img/categorias/otros.jpg" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Color",
                table: "Categorias");

            migrationBuilder.DropColumn(
                name: "Imagen",
                table: "Categorias");
        }
    }
}
