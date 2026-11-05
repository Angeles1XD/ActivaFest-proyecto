using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🎫 HU03: FICHA DETALLADA DEL EVENTO (pruebas de integración)
// =============================
public class HU03FichaEventoTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU03FichaEventoTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<Evento> PublicadoAsync(DateTime fecha, params TipoEntrada[] zonas)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = new Evento
        {
            Titulo = $"Festival {Guid.NewGuid():N}", Descripcion = "Festival de música andina con artistas invitados.", CategoriaId = 3,
            Ubicacion = "Parque Selva Alegre, Arequipa", Latitud = -16.39, Longitud = -71.53, Fecha = fecha,
            Estado = EstadoEvento.Aprobado, TiposEntrada = zonas.ToList(), Aforo = zonas.Sum(z => z.Capacidad),
            Imagenes = Enumerable.Range(0, 3).Select(i => new EventoImagen { Ruta = $"/uploads/eventos/recinto{i}.png", Orden = i }).ToList()
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        return evento;
    }

    [Fact]
    public async Task Muestra_fecha_hora_direccion_precios_y_aforo_disponible()
    {
        var fecha = DateTime.Today.AddDays(20).AddHours(19).AddMinutes(30);
        var evento = await PublicadoAsync(fecha,
            new TipoEntrada { Nombre = "General", Precio = 45m, Capacidad = 200, Vendidas = 50, Reservadas = 10 },
            new TipoEntrada { Nombre = "VIP", Precio = 150m, Capacidad = 20, Vendidas = 12 },
            new TipoEntrada { Nombre = "Palco", Precio = 300m, Capacidad = 5, Vendidas = 5 });

        var html = await TextoAsync(await _app.Cliente().GetAsync($"/Eventos/Detalle/{evento.Id}"));

        Assert.Contains(Formato.Fecha(fecha, "dddd dd 'de' MMMM 'de' yyyy"), html);
        Assert.Contains("19:30 horas", html);
        Assert.Contains("Parque Selva Alegre, Arequipa", html);
        Assert.Contains("S/ 45.00", html);
        Assert.Contains("S/ 150.00", html);
        Assert.Contains("S/ 300.00", html);

        // Disponible = capacidad - vendidas - reservadas: 140 + 8 + 0
        Assert.Contains("148 disponibles", html);
        Assert.Matches(@"col-disponibles"">\s*140\s*<", html);
        Assert.Contains("¡Quedan 8!", html);
        Assert.Matches(@"col-disponibles"">\s*<span class=""agotado"">Agotado", html);
    }

    [Fact]
    public async Task La_galeria_muestra_al_menos_tres_imagenes_del_recinto()
    {
        var evento = await PublicadoAsync(DateTime.Now.AddDays(5), new TipoEntrada { Nombre = "General", Precio = 0, Capacidad = 100 });

        var html = await _app.Cliente().GetStringAsync($"/Eventos/Detalle/{evento.Id}");

        var fotos = Regex.Matches(html, @"<img src=""/uploads/eventos/recinto\d\.png""").Count;
        Assert.True(fotos >= 3, $"La galería muestra {fotos} fotos");
    }

    [Fact]
    public async Task Un_evento_agotado_lo_indica()
    {
        var evento = await PublicadoAsync(DateTime.Now.AddDays(5), new TipoEntrada { Nombre = "General", Precio = 20, Capacidad = 10, Vendidas = 10 });

        var html = await TextoAsync(await _app.Cliente().GetAsync($"/Eventos/Detalle/{evento.Id}"));

        Assert.Contains("· agotado", html);
        Assert.Contains("Entradas agotadas", html);
    }

    [Fact]
    public async Task Un_evento_inexistente_devuelve_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Cliente().GetAsync("/Eventos/Detalle/999999")).StatusCode);
    }
}
