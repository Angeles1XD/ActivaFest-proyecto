using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 👥 HU22: COMPRAS POR USUARIO, RANKING RFM Y AUDITORÍA (pruebas del servicio)
// =============================
public class HU22ClientesTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly AnaliticaService _analitica;
    private readonly Evento _caro, _medio, _barato;

    public HU22ClientesTests()
    {
        _analitica = new AnaliticaService(_e.Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        CompraServiceTests.CrearUsuarios(_e.Db, "dani", "eli");

        var hoy = DateTime.Now;
        _caro = _e.Evento("Concierto sinfónico", 1, hoy.AddDays(-1), 100, 200);
        _medio = _e.Evento("Obra de teatro", 2, hoy.AddDays(-9), 100, 100);
        _barato = _e.Evento("Feria del libro", 2, hoy.AddDays(20), 100, 10);

        // ana: 4 compras recientes y el mayor gasto → VIP
        _e.Compra("ana", _caro, 2, hoy.AddDays(-2), usadas: 2);
        _e.Compra("ana", _caro, 2, hoy.AddDays(-5));
        _e.Compra("ana", _caro, 2, hoy.AddDays(-8));
        _e.Compra("ana", _medio, 1, hoy.AddDays(-10), usadas: 1);
        _e.Compra("ana", _caro, 5, hoy.AddDays(-1), estado: EstadoOrden.PendientePago); // no pagada: no cuenta

        // beto: una sola compra hace 10 días → Nuevo
        _e.Compra("beto", _medio, 1, hoy.AddDays(-10));

        // carla: compraba seguido pero no compra hace 90 días → En riesgo
        _e.Compra("carla", _medio, 1, hoy.AddDays(-90));
        _e.Compra("carla", _medio, 1, hoy.AddDays(-100));
        _e.Compra("carla", _medio, 1, hoy.AddDays(-120));

        // dani: la mayor frecuencia con poco gasto → Frecuente
        for (var i = 1; i <= 5; i++)
            _e.Compra("dani", _barato, 1, hoy.AddDays(-3 * i));

        // eli: una compra antigua → Ocasional
        _e.Compra("eli", _barato, 5, hoy.AddDays(-200));
    }

    public void Dispose() => _e.Dispose();

    [Fact]
    public void Los_segmentos_siguen_las_reglas_del_documento()
    {
        Assert.Equal(SegmentoRfm.Nuevo, SegmentoRfm.De(1, 30, 5, 1, 5));
        Assert.Equal(SegmentoRfm.EnRiesgo, SegmentoRfm.De(2, 61, 1, 5, 5));
        Assert.Equal(SegmentoRfm.Vip, SegmentoRfm.De(6, 3, 4, 4, 4));
        Assert.Equal(SegmentoRfm.Frecuente, SegmentoRfm.De(6, 3, 5, 5, 3));
        Assert.Equal(SegmentoRfm.Ocasional, SegmentoRfm.De(1, 31, 1, 1, 1));
        Assert.Equal(new[] { 1, 1, 3, 4, 5 }, AnaliticaService.Puntajes(new double[] { 1, 1, 3, 4, 5 }));
    }

    [Fact]
    public async Task El_ranking_ordena_por_gasto_y_asigna_el_segmento_RFM()
    {
        var clientes = await _analitica.ClientesAsync();

        Assert.Equal(new[] { "ana", "carla", "beto", "dani", "eli" }.Take(3), clientes.Take(3).Select(c => c.UsuarioId));
        var segmentos = clientes.ToDictionary(c => c.UsuarioId, c => c.Segmento);
        Assert.Equal(SegmentoRfm.Vip, segmentos["ana"]);
        Assert.Equal(SegmentoRfm.Nuevo, segmentos["beto"]);
        Assert.Equal(SegmentoRfm.EnRiesgo, segmentos["carla"]);
        Assert.Equal(SegmentoRfm.Frecuente, segmentos["dani"]);
        Assert.Equal(SegmentoRfm.Ocasional, segmentos["eli"]);
        Assert.DoesNotContain(clientes, c => c.UsuarioId == "org"); // sin compras no entra al ranking

        // Cifras contra una consulta de control (solo órdenes pagadas)
        var ana = clientes.Single(c => c.UsuarioId == "ana");
        var control = _e.Db.Ordenes.Where(o => o.UsuarioId == "ana" && o.Estado == EstadoOrden.Pagada).ToList();
        Assert.Equal(control.Sum(o => o.Total), ana.Gasto);
        Assert.Equal(1300m, ana.Gasto);
        Assert.Equal(4, ana.Compras);
        Assert.Equal(7, ana.Entradas);
        Assert.Equal(2, ana.DiasDesdeUltima);
        Assert.Equal((5, 4, 5), (ana.R, ana.F, ana.M));
    }

    [Fact]
    public async Task La_ficha_muestra_ultimas_compras_gasto_eventos_asistidos_y_el_evento_de_mayor_costo()
    {
        var ficha = await _analitica.FichaClienteAsync("ana", "org");

        Assert.NotNull(ficha);
        Assert.Equal("ana", ficha!.Nombre);
        Assert.Equal("ana@test.pe", ficha.Correo);
        Assert.Equal(1300m, ficha.Resumen.Gasto);

        Assert.Equal(4, ficha.UltimasCompras.Count);
        Assert.Equal(ficha.UltimasCompras.OrderByDescending(c => c.Fecha), ficha.UltimasCompras);
        Assert.Equal(1300m, ficha.UltimasCompras.Sum(c => c.Total));

        // Asistió a los dos eventos en los que validó entradas en la puerta
        Assert.Equal(new[] { ("Concierto sinfónico", 2), ("Obra de teatro", 1) },
            ficha.EventosAsistidos.Select(e => (e.Evento, e.Entradas)));

        Assert.Equal(("Concierto sinfónico", 200m), ficha.MayorPrecioEntrada);
        Assert.Equal(("Concierto sinfónico", 1200m), ficha.MayorGastoEvento);
        Assert.Equal(6, ficha.CategoriaPreferida!.Value.Entradas);
    }

    [Fact]
    public async Task La_ficha_lista_solo_las_10_compras_mas_recientes()
    {
        var hoy = DateTime.Now;
        for (var i = 1; i <= 12; i++)
            _e.Compra("org", _barato, 1, hoy.AddDays(-i));

        var ficha = await _analitica.FichaClienteAsync("org", "ana");

        Assert.Equal(10, ficha!.UltimasCompras.Count);
        Assert.True(ficha.UltimasCompras.Last().Fecha > hoy.AddDays(-10.5));
        Assert.Equal(12, ficha.Resumen.Compras);
        Assert.Empty(ficha.EventosAsistidos);
    }

    [Fact]
    public async Task Cada_acceso_a_una_ficha_queda_en_la_auditoria()
    {
        await _analitica.FichaClienteAsync("beto", "org");
        await _analitica.FichaClienteAsync("beto", "org");
        Assert.Null(await _analitica.FichaClienteAsync("no-existe", "org"));

        var auditoria = await _analitica.AuditoriaAsync();
        Assert.Equal(2, auditoria.Count);
        Assert.All(auditoria, a =>
        {
            Assert.Equal("org", a.AdministradorId);
            Assert.Equal($"Ficha del cliente {ClienteAnalitica.CodigoDe("beto")}", a.Consulta);
            Assert.False(string.IsNullOrEmpty(a.FuenteDatos));
            Assert.True(a.Fecha > DateTime.UtcNow.AddMinutes(-1));
        });
        Assert.DoesNotContain(auditoria, a => a.Consulta.Contains("beto@"));
    }
}

// =============================
// 👥 HU22: vistas (solo Administrador, ranking seudonimizado, auditoría)
// =============================
public class HU22ClientesVistaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU22ClientesVistaTests(AppDePrueba app)
    {
        _app = app;
    }

    internal static async Task<(string Id, string Email)> ClienteConCompraAsync(AppDePrueba _app, bool promociones = false, decimal precio = 80)
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email, promociones: promociones);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var usuario = await db.Users.SingleAsync(u => u.Email == email);
        var evento = new Evento
        {
            Titulo = "Festival HU22", Descripcion = "Evento de prueba.", CategoriaId = 1, Ubicacion = "Lima", Fecha = DateTime.Now.AddDays(5),
            Estado = EstadoEvento.Aprobado, OrganizadorId = usuario.Id, Aforo = 50,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = precio, Capacidad = 50 } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        db.Ordenes.Add(new Orden
        {
            Codigo = Guid.NewGuid().ToString("N")[..12], UsuarioId = usuario.Id, EventoId = evento.Id, Estado = EstadoOrden.Pagada,
            Total = precio * 2, Comision = precio * 0.16m, PorcentajeComision = 8, FechaPago = DateTime.UtcNow, FechaCreacion = DateTime.UtcNow, FechaExpiracion = DateTime.UtcNow,
            Items = { new OrdenItem { TipoEntradaId = evento.TiposEntrada.First().Id, Cantidad = 2, PrecioUnitario = precio } }
        });
        await db.SaveChangesAsync();
        return (usuario.Id, email);
    }

    [Fact]
    public async Task Solo_el_administrador_accede_a_clientes_fichas_y_auditoria()
    {
        var (id, _) = await ClienteConCompraAsync(_app);
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);

        foreach (var url in new[] { "/Analitica/Clientes", $"/Analitica/Cliente/{id}", "/Analitica/Auditoria" })
        {
            Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync(url)).Headers.Location!.PathAndQuery);
            Assert.StartsWith("/Auth/Login", (await _app.Cliente().GetAsync(url)).Headers.Location!.PathAndQuery);
        }
    }

    [Fact]
    public async Task El_ranking_muestra_codigos_y_la_ficha_queda_auditada()
    {
        var (id, email) = await ClienteConCompraAsync(_app);
        var codigo = ClienteAnalitica.CodigoDe(id);
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");

        var ranking = await TextoAsync(await admin.GetAsync("/Analitica/Clientes"));
        Assert.Contains($"Cliente {codigo}", ranking);
        Assert.DoesNotContain(email, ranking);
        Assert.Contains($"/Analitica/Cliente/{id}", ranking);

        var ficha = await TextoAsync(await admin.GetAsync($"/Analitica/Cliente/{id}"));
        Assert.Contains(email, ficha);
        Assert.Contains("Festival HU22", ficha);
        Assert.Contains("Este acceso quedó registrado", ficha);

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registro = await db.AuditoriasAnalitica.Include(a => a.Administrador)
                .Where(a => a.Consulta == $"Ficha del cliente {codigo}").SingleAsync();
            Assert.Equal("admin@test.com", registro.Administrador!.Email);
            Assert.True(await db.AuditoriasAnalitica.AnyAsync(a => a.Consulta.StartsWith("Filtro de clientes")));
        }

        var auditoria = await TextoAsync(await admin.GetAsync("/Analitica/Auditoria"));
        Assert.Contains($"Ficha del cliente {codigo}", auditoria);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await admin.GetAsync("/Analitica/Cliente/no-existe")).StatusCode);
    }
}
