using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// ⭐ HU12: EL DESTACADO APARECE Y DESAPARECE A TIEMPO (pruebas del catálogo con caché)
// =============================
public class HU12VigenciaDestacadoTests : IDisposable
{
    private readonly SqliteConnection _conexion;
    private readonly ApplicationDbContext _db;
    private readonly CatalogoService _catalogo;

    public HU12VigenciaDestacadoTests()
    {
        _conexion = new SqliteConnection("Data Source=:memory:");
        _conexion.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conexion).Options);
        _db.Database.EnsureCreated();
        // Caché real (con vencimiento): la que usa la app sin Redis
        _catalogo = new CatalogoService(_db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), NullLogger<CatalogoService>.Instance);
        CompraServiceTests.CrearUsuarios(_db, "org");
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexion.Dispose();
    }

    private int EventoDestacado(DateTime inicio, DateTime fin)
    {
        var (evento, _, _) = CompraServiceTests.CrearEvento(_db);
        _db.ContratosDestacado.Add(new ContratoDestacado
        {
            Codigo = ContratoDestacado.PrefijoCodigo + Guid.NewGuid().ToString("N")[..16], EventoId = evento.Id, PaqueteId = 1, OrganizadorId = "org",
            Precio = 29.90m, Dias = 3, Prioridad = 1, Estado = EstadoContrato.Activo, FechaPago = inicio, FechaInicio = inicio, FechaFin = fin
        });
        _db.SaveChanges();
        return evento.Id;
    }

    // Criterio: al vencer el período deja de aparecer automáticamente (sin esperar a que caduque la caché de 5 min)
    [Fact]
    public async Task Al_vencer_el_periodo_deja_de_aparecer_aunque_la_portada_este_en_cache()
    {
        var id = EventoDestacado(DateTime.Now.AddDays(-1), DateTime.Now.AddSeconds(2));

        Assert.Contains(await _catalogo.DestacadosAsync(), e => e.Id == id);
        Assert.True((await _catalogo.BuscarAsync(new CatalogoFiltro())).Eventos.Single(e => e.Id == id).Destacado);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Assert.DoesNotContain(await _catalogo.DestacadosAsync(), e => e.Id == id);
        Assert.False((await _catalogo.BuscarAsync(new CatalogoFiltro())).Eventos.Single(e => e.Id == id).Destacado);
    }

    // Un segundo paquete que empieza cuando termina el primero aparece a su hora, también con la caché llena
    [Fact]
    public async Task Un_destacado_programado_aparece_justo_cuando_empieza_su_periodo()
    {
        var id = EventoDestacado(DateTime.Now.AddSeconds(2), DateTime.Now.AddDays(3));

        Assert.DoesNotContain(await _catalogo.DestacadosAsync(), e => e.Id == id);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Assert.Contains(await _catalogo.DestacadosAsync(), e => e.Id == id);
    }
}

// =============================
// ⭐ HU12: CONTRATAR UN PAQUETE DESDE LA INTERFAZ (pruebas de integración)
// =============================
public class HU12DestacarEventoTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU12DestacarEventoTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    // Solo la sección "Eventos destacados" de la portada (la portada también lista los próximos eventos)
    private async Task<string> SeccionDestacadosAsync()
    {
        var portada = await _app.Cliente().GetStringAsync("/");
        var inicio = portada.IndexOf("Eventos destacados", StringComparison.Ordinal);
        if (inicio < 0)
            return "";
        var seccion = portada[inicio..];
        return seccion[..seccion.IndexOf("</section>", StringComparison.Ordinal)];
    }

    // Criterio: al confirmarse el pago del paquete, el evento aparece en destacados durante el período contratado
    [Fact]
    public async Task Al_confirmarse_el_pago_el_evento_aparece_en_destacados_con_su_tiempo_restante()
    {
        var organizador = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(organizador, email, rol: Roles.Organizador);

        int eventoId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dueño = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!;
            var evento = new Evento
            {
                Titulo = $"Festival {Guid.NewGuid():N}", Descripcion = "Festival para destacar.", CategoriaId = 3, Ubicacion = "Arequipa",
                Fecha = DateTime.Now.AddDays(20), Estado = EstadoEvento.Aprobado, OrganizadorId = dueño.Id, Aforo = 10,
                TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 10, Capacidad = 10 } }
            };
            db.Eventos.Add(evento);
            await db.SaveChangesAsync();
            eventoId = evento.Id;
        }

        Assert.DoesNotContain($"/Eventos/Detalle/{eventoId}\"", await SeccionDestacadosAsync());

        // Elige el paquete Impulso (3 días) y paga en la pasarela simulada
        var pagina = $"/Destacados/Contratar/{eventoId}";
        var pasarela = (await PostAsync(organizador, pagina, pagina, ("paqueteId", "1"))).Headers.Location!.OriginalString;
        Assert.StartsWith("/Destacados/PasarelaSimulada", pasarela);
        var confirmacion = (await PostAsync(organizador, pasarela, pasarela, ("aprobar", "true"))).Headers.Location!.OriginalString;
        Assert.StartsWith("/Destacados/Confirmacion", confirmacion);

        var html = await TextoAsync(await organizador.GetAsync(confirmacion));
        Assert.Contains("¡Tu evento ya está destacado!", html);
        var segundos = int.Parse(System.Text.RegularExpressions.Regex.Match(html, @"temporizador-destacado"" data-segundos=""(\d+)""").Groups[1].Value);
        Assert.InRange(segundos, 3 * 86400 - 120, 3 * 86400); // 3 días contratados

        // Aparece en la sección Destacados de la portada y con la etiqueta en el catálogo
        Assert.Contains($"/Eventos/Detalle/{eventoId}\"", await SeccionDestacadosAsync());

        var catalogo = await _app.Cliente().GetStringAsync("/Eventos");
        var enlace = catalogo.IndexOf($"/Eventos/Detalle/{eventoId}\"", StringComparison.Ordinal);
        var inicioTarjeta = catalogo.LastIndexOf("<article", enlace, StringComparison.Ordinal);
        var tarjeta = catalogo[inicioTarjeta..catalogo.IndexOf("</article>", enlace, StringComparison.Ordinal)];
        Assert.Contains("⭐ Destacado", tarjeta);
    }

    [Fact]
    public async Task Un_pago_rechazado_no_destaca_el_evento()
    {
        var organizador = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(organizador, email, rol: Roles.Organizador);
        int eventoId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dueño = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!;
            var evento = new Evento
            {
                Titulo = $"Feria {Guid.NewGuid():N}", Descripcion = "Feria.", CategoriaId = 4, Ubicacion = "Lima", Fecha = DateTime.Now.AddDays(15),
                Estado = EstadoEvento.Aprobado, OrganizadorId = dueño.Id, Aforo = 10, TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 0, Capacidad = 10 } }
            };
            db.Eventos.Add(evento);
            await db.SaveChangesAsync();
            eventoId = evento.Id;
        }

        var pagina = $"/Destacados/Contratar/{eventoId}";
        var pasarela = (await PostAsync(organizador, pagina, pagina, ("paqueteId", "1"))).Headers.Location!.OriginalString;
        await PostAsync(organizador, pasarela, pasarela, ("aprobar", "false"));

        using var verificacion = _app.Services.CreateScope();
        Assert.False(verificacion.ServiceProvider.GetRequiredService<ApplicationDbContext>().ContratosDestacado
            .Any(c => c.EventoId == eventoId && c.Estado == EstadoContrato.Activo));
        Assert.DoesNotContain($"/Eventos/Detalle/{eventoId}\"", await SeccionDestacadosAsync());
    }
}
