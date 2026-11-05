using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ActivaFest.Migrations
{
    /// <inheritdoc />
    public partial class Sprint2CatalogoMapas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ajustado a mano: EF interpretaba Asistentes -> CategoriaId como un renombrado.
            // Asistentes pasa a llamarse Aforo y la categoría de texto se convierte en FK a Categorias.
            migrationBuilder.RenameColumn(
                name: "Asistentes",
                table: "Eventos",
                newName: "Aforo");

            migrationBuilder.AddColumn<int>(
                name: "CategoriaId",
                table: "Eventos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<string>(
                name: "Descripcion",
                table: "Eventos",
                type: "TEXT",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "Latitud",
                table: "Eventos",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitud",
                table: "Eventos",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Categorias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Icono = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categorias", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventoImagenes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ruta = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventoImagenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventoImagenes_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TiposEntrada",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Precio = table.Column<double>(type: "REAL", precision: 10, scale: 2, nullable: false),
                    Capacidad = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiposEntrada", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TiposEntrada_Eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "Eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Categorias",
                columns: new[] { "Id", "Icono", "Nombre" },
                values: new object[,]
                {
                    { 1, "🎵", "Conciertos" },
                    { 2, "🎭", "Teatro" },
                    { 3, "🎉", "Festivales" },
                    { 4, "🎪", "Ferias" },
                    { 5, "🖼️", "Exposiciones" },
                    { 6, "⚽", "Deportes" },
                    { 7, "🍽️", "Gastronomía" },
                    { 8, "👨‍👩‍👧", "Familiar" },
                    { 9, "🏛️", "Cultura" },
                    { 10, "🎫", "Otros" }
                });

            // Asigna la categoría equivalente al texto anterior ("teatro" -> Teatro, "concierto" -> Conciertos); si no hay, "Otros"
            migrationBuilder.Sql(@"
                UPDATE Eventos SET CategoriaId = COALESCE(
                    (SELECT c.Id FROM Categorias c
                     WHERE lower(c.Nombre) = lower(trim(Eventos.Categoria))
                        OR lower(c.Nombre) = lower(trim(Eventos.Categoria)) || 's'
                     LIMIT 1),
                    10);");

            migrationBuilder.DropColumn(
                name: "Categoria",
                table: "Eventos");

            migrationBuilder.CreateIndex(
                name: "IX_Eventos_CategoriaId",
                table: "Eventos",
                column: "CategoriaId");

            migrationBuilder.CreateIndex(
                name: "IX_Eventos_Fecha",
                table: "Eventos",
                column: "Fecha");

            migrationBuilder.CreateIndex(
                name: "IX_Categorias_Nombre",
                table: "Categorias",
                column: "Nombre",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventoImagenes_EventoId",
                table: "EventoImagenes",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_TiposEntrada_EventoId",
                table: "TiposEntrada",
                column: "EventoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Eventos_Categorias_CategoriaId",
                table: "Eventos",
                column: "CategoriaId",
                principalTable: "Categorias",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventoImagenes");

            migrationBuilder.DropTable(
                name: "TiposEntrada");

            // Reconstrucción manual de Eventos con el esquema del Sprint 1 (EF la dejaría para el final,
            // después de eliminar Categorias, lo que viola la FK). Todo ocurre en la misma transacción.
            migrationBuilder.Sql(@"
                CREATE TABLE ""ef_temp_Eventos"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Eventos"" PRIMARY KEY AUTOINCREMENT,
                    ""Asistentes"" INTEGER NOT NULL,
                    ""Categoria"" TEXT NOT NULL,
                    ""Estado"" INTEGER NOT NULL,
                    ""Fecha"" TEXT NOT NULL,
                    ""FechaCreacion"" TEXT NOT NULL,
                    ""FechaModeracion"" TEXT NULL,
                    ""MotivoRechazo"" TEXT NULL,
                    ""OrganizadorId"" TEXT NULL,
                    ""Titulo"" TEXT NOT NULL,
                    ""Ubicacion"" TEXT NOT NULL,
                    CONSTRAINT ""FK_Eventos_AspNetUsers_OrganizadorId"" FOREIGN KEY (""OrganizadorId"") REFERENCES ""AspNetUsers"" (""Id"") ON DELETE SET NULL
                );

                INSERT INTO ""ef_temp_Eventos"" (""Id"", ""Asistentes"", ""Categoria"", ""Estado"", ""Fecha"", ""FechaCreacion"",
                    ""FechaModeracion"", ""MotivoRechazo"", ""OrganizadorId"", ""Titulo"", ""Ubicacion"")
                SELECT e.""Id"", e.""Aforo"", COALESCE(c.""Nombre"", ''), e.""Estado"", e.""Fecha"", e.""FechaCreacion"",
                    e.""FechaModeracion"", e.""MotivoRechazo"", e.""OrganizadorId"", e.""Titulo"", e.""Ubicacion""
                FROM ""Eventos"" e
                LEFT JOIN ""Categorias"" c ON c.""Id"" = e.""CategoriaId"";

                DROP TABLE ""Eventos"";
                ALTER TABLE ""ef_temp_Eventos"" RENAME TO ""Eventos"";

                CREATE INDEX ""IX_Eventos_Estado"" ON ""Eventos"" (""Estado"");
                CREATE INDEX ""IX_Eventos_OrganizadorId"" ON ""Eventos"" (""OrganizadorId"");

                DROP TABLE ""Categorias"";");
        }
    }
}
