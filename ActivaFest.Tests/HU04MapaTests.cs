using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🗺️ HU04: MAPA Y RUTA DE LLEGADA (pruebas de integración)
// =============================
// El trazado de la ruta corre en el navegador (mapa-evento.js); aquí se verifica que la ficha entregue
// todo lo que el mapa necesita. Los escenarios del navegador (ubicación concedida, negada y mapa caído)
// se verificaron en Edge sin interfaz.
public class HU04MapaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU04MapaTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<int> PublicadoAsync(double? lat, double? lng, string titulo = "Concierto <b>al aire libre</b>")
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = new Evento
        {
            Titulo = titulo, Descripcion = "Concierto al aire libre con bandas locales.", CategoriaId = 1,
            Ubicacion = "Plaza de Armas de Arequipa", Latitud = lat, Longitud = lng, Fecha = DateTime.Now.AddDays(7),
            Estado = EstadoEvento.Aprobado, Aforo = 100,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 20, Capacidad = 100 } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        return evento.Id;
    }

    [Fact]
    public async Task La_ficha_entrega_el_marcador_del_recinto_y_la_ruta_desde_el_usuario()
    {
        var id = await PublicadoAsync(-16.398866, -71.536961);

        var html = await _app.Cliente().GetStringAsync($"/Eventos/Detalle/{id}");

        // Coordenadas con punto decimal (independiente de la cultura es-PE)
        Assert.Contains("data-lat=\"-16.398866\"", html);
        Assert.Contains("data-lng=\"-71.536961\"", html);
        Assert.Contains("id=\"btnRuta\"", html);
        Assert.Contains("leaflet@1.9.4/dist/leaflet.js", html);
        Assert.Contains("/js/mapa-evento.js", html);
        Assert.Contains("https://www.google.com/maps/dir/?api=1&destination=-16.398866,-71.536961", System.Net.WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task El_titulo_del_marcador_se_codifica_para_no_inyectar_html()
    {
        var id = await PublicadoAsync(-16.4, -71.5);

        var html = await _app.Cliente().GetStringAsync($"/Eventos/Detalle/{id}");

        Assert.Contains("data-titulo=\"Concierto &lt;b&gt;al aire libre&lt;/b&gt;\"", html);
    }

    [Fact]
    public async Task Sin_coordenadas_muestra_la_direccion_sin_mapa_ni_error()
    {
        var id = await PublicadoAsync(null, null, "Sin mapa");

        var html = await TextoAsync(await _app.Cliente().GetAsync($"/Eventos/Detalle/{id}"));

        Assert.DoesNotContain("id=\"mapaEvento\"", html);
        Assert.DoesNotContain("mapa-evento.js", html);
        Assert.Contains("Plaza de Armas de Arequipa — el organizador aún no marcó la ubicación exacta", html);
    }
}
