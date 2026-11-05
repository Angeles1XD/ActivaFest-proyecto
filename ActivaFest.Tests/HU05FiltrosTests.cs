using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🔎 HU05: FILTROS SIN RESULTADOS (pruebas de integración)
// =============================
public class HU05FiltrosTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU05FiltrosTests(AppDePrueba app)
    {
        _app = app;
    }

    [Fact]
    public async Task Sin_resultados_muestra_un_mensaje_y_la_opcion_de_limpiar_filtros()
    {
        var html = await TextoAsync(await _app.Cliente().GetAsync("/Eventos?CategoriaId=1&PrecioMin=99000&PrecioMax=99999&Desde=2030-01-01"));

        Assert.Contains("No hay eventos que coincidan con los filtros elegidos", html);
        Assert.Contains("<a class=\"btn-rosa\" href=\"/Eventos\">🧹 Limpiar filtros</a>", html);
    }

    [Fact]
    public async Task Los_filtros_elegidos_se_conservan_en_el_formulario()
    {
        var html = await TextoAsync(await _app.Cliente().GetAsync("/Eventos?CategoriaId=2&PrecioMin=10&PrecioMax=50&SoloGratis=false"));

        Assert.Matches(@"<option value=""2""[^>]*selected", html);
        Assert.Contains("name=\"PrecioMin\"", html);
        Assert.Matches(@"name=""PrecioMin""[^>]*value=""10""|value=""10""[^>]*name=""PrecioMin""", html);
    }

    [Fact]
    public async Task En_una_categoria_permite_limpiar_la_busqueda_sin_salir_de_ella()
    {
        var html = await TextoAsync(await _app.Cliente().GetAsync("/Categorias?CategoriaId=1&Buscar=inexistentezzz"));

        Assert.Contains("No hay eventos que coincidan con «inexistentezzz» en Conciertos", html);
        Assert.Contains("href=\"/Categorias?CategoriaId=1\"", html);
        Assert.Contains("Limpiar búsqueda", html);
    }

    [Fact]
    public async Task Valores_invalidos_en_la_url_no_rompen_el_catalogo()
    {
        var r = await _app.Cliente().GetAsync("/Eventos?PrecioMin=abc&Desde=no-es-fecha&Pagina=-4&CategoriaId=999");

        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
    }
}
