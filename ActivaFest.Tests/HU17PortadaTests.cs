using Microsoft.Extensions.DependencyInjection;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🎯 HU17: SECCIÓN DE RECOMENDADOS EN LA PORTADA (pruebas de integración)
// =============================
public class HU17PortadaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU17PortadaTests(AppDePrueba app)
    {
        _app = app;

        // Un evento publicado en Arequipa para que haya algo que recomendar
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ActivaFest.Data.ApplicationDbContext>();
        if (!db.Eventos.Any(e => e.Titulo == "Concierto de rock en Arequipa"))
        {
            db.Eventos.Add(new ActivaFest.Models.Evento
            {
                Titulo = "Concierto de rock en Arequipa", Descripcion = "Bandas de rock en vivo.", CategoriaId = 1, Ubicacion = "Arequipa",
                Latitud = -16.40, Longitud = -71.53, Fecha = DateTime.Now.AddDays(9), Estado = ActivaFest.Models.EstadoEvento.Aprobado, Aforo = 100,
                TiposEntrada = { new ActivaFest.Models.TipoEntrada { Nombre = "General", Precio = 30, Capacidad = 100 } }
            });
            db.SaveChanges();
        }
    }

    [Fact]
    public async Task Un_usuario_nuevo_ve_los_populares_y_con_ubicacion_los_cercanos()
    {
        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, Correo());

        var sinUbicacion = await TextoAsync(await cliente.GetAsync("/"));
        var conUbicacion = await TextoAsync(await cliente.GetAsync("/?lat=-16.40&lng=-71.53"));

        Assert.Contains("Populares en ActivaFest", sinUbicacion);
        Assert.Contains("Usar mi ubicación", sinUbicacion);
        Assert.Contains("Populares cerca de ti", conUbicacion);
    }

    [Fact]
    public async Task Con_historial_ve_sus_recomendaciones_personalizadas()
    {
        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, Correo());
        await cliente.GetAsync("/Eventos?Buscar=concierto");

        var html = await TextoAsync(await cliente.GetAsync("/"));

        Assert.Contains("Recomendados para ti", html);
    }

    [Fact]
    public async Task El_perfil_muestra_el_historial_y_permite_borrarlo()
    {
        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, Correo());
        await cliente.GetAsync("/Categorias?CategoriaId=2");

        Assert.Contains("Guardamos 1 registro", await TextoAsync(await cliente.GetAsync("/Perfil")));

        await cliente.PostAsync("/Perfil/BorrarHistorial", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(cliente, "/Perfil")
        }));

        Assert.Contains("Guardamos 0 registros", await TextoAsync(await cliente.GetAsync("/Perfil")));
    }
}
