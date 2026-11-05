using System.Text;
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
// 🔎 HU29: FILTRO DE CLIENTES (pruebas del servicio)
// =============================
public class HU29FiltroClientesTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly AnaliticaService _analitica;
    private static readonly DateTime Agosto = new(2026, 8, 1), FinAgosto = new(2026, 8, 31);

    public HU29FiltroClientesTests()
    {
        _analitica = new AnaliticaService(_e.Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var concierto = _e.Evento("Concierto", 1, new DateTime(2026, 9, 1, 20, 0, 0), 100, 100);
        var teatro = _e.Evento("Teatro", 2, new DateTime(2026, 9, 5, 20, 0, 0), 100, 50);

        // ana: historial S/ 550 (6 entradas, 3 compras); en agosto S/ 500 (5 entradas, 2 compras), todo de conciertos
        _e.Compra("ana", concierto, 3, new DateTime(2026, 8, 5, 12, 0, 0));
        _e.Compra("ana", concierto, 2, new DateTime(2026, 8, 20, 12, 0, 0));
        _e.Compra("ana", teatro, 1, new DateTime(2026, 6, 10, 12, 0, 0));

        // beto: S/ 200 en agosto (4 entradas de teatro, 1 compra); su orden sin pagar no cuenta
        _e.Compra("beto", teatro, 4, new DateTime(2026, 8, 10, 12, 0, 0));
        _e.Compra("beto", concierto, 9, new DateTime(2026, 8, 11, 12, 0, 0), estado: EstadoOrden.PendientePago);

        // carla: historial S/ 250 (4 entradas, 3 compras); en agosto S/ 150 (2 entradas, 2 compras)
        _e.Compra("carla", concierto, 1, new DateTime(2026, 8, 15, 12, 0, 0));
        _e.Compra("carla", teatro, 2, new DateTime(2026, 7, 1, 12, 0, 0));
        _e.Compra("carla", teatro, 1, new DateTime(2026, 8, 25, 12, 0, 0));
    }

    public void Dispose() => _e.Dispose();

    private async Task<List<string>> Usuarios(FiltroClientes f) =>
        (await _analitica.FiltrarClientesAsync(f)).Select(c => c.UsuarioId).ToList();

    [Fact]
    public async Task Sin_filtros_ordena_por_gasto_de_mayor_a_menor()
    {
        var clientes = await _analitica.FiltrarClientesAsync(new FiltroClientes());

        Assert.Equal(new[] { "ana", "carla", "beto" }, clientes.Select(c => c.UsuarioId));
        Assert.Equal(new[] { 550m, 250m, 200m }, clientes.Select(c => c.Gasto));
    }

    [Fact]
    public async Task El_periodo_y_la_categoria_recalculan_las_cifras()
    {
        var agosto = await _analitica.FiltrarClientesAsync(new FiltroClientes { Desde = Agosto, Hasta = FinAgosto });
        Assert.Equal(new[] { ("ana", 500m, 5, 2), ("beto", 200m, 4, 1), ("carla", 150m, 2, 2) },
            agosto.Select(c => (c.UsuarioId, c.Gasto, c.Entradas, c.Compras)));

        var conciertos = await _analitica.FiltrarClientesAsync(new FiltroClientes { Desde = Agosto, Hasta = FinAgosto, CategoriaId = 1 });
        Assert.Equal(new[] { ("ana", 500m, 5), ("carla", 100m, 1) }, conciertos.Select(c => (c.UsuarioId, c.Gasto, c.Entradas)));

        // El segmento RFM no cambia con el período: es el del historial completo
        var historial = (await _analitica.ClientesAsync()).ToDictionary(c => c.UsuarioId, c => c.Segmento);
        Assert.All(agosto, c => Assert.Equal(historial[c.UsuarioId], c.Segmento));
    }

    [Fact]
    public async Task Los_filtros_se_combinan()
    {
        Assert.Equal(new[] { "ana", "beto" }, await Usuarios(new() { Desde = Agosto, Hasta = FinAgosto, EntradasMin = 3 }));
        Assert.Equal(new[] { "ana", "carla" }, await Usuarios(new() { Desde = Agosto, Hasta = FinAgosto, ComprasMin = 2 }));
        Assert.Equal(new[] { "beto", "carla" }, await Usuarios(new() { Desde = Agosto, Hasta = FinAgosto, GastoMax = 200 }));
        Assert.Equal(new[] { "beto" }, await Usuarios(new() { Desde = Agosto, Hasta = FinAgosto, GastoMin = 160, GastoMax = 400 }));
        Assert.Equal(new[] { "carla" }, await Usuarios(new() { Desde = Agosto, Hasta = FinAgosto, CategoriaId = 1, GastoMax = 100, ComprasMin = 1 }));
        Assert.Empty(await Usuarios(new() { GastoMin = 1000 }));
    }

    [Fact]
    public async Task Se_ordena_por_entradas_o_compras_y_el_empate_lo_decide_el_gasto()
    {
        Assert.Equal(new[] { "ana", "carla", "beto" }, await Usuarios(new() { Orden = "entradas" }));   // 6, 4 (S/ 250), 4 (S/ 200)
        Assert.Equal(new[] { "ana", "carla", "beto" }, await Usuarios(new() { Orden = "compras" }));    // 3 (S/ 550), 3 (S/ 250), 1
        Assert.Equal(new[] { "ana", "beto", "carla" }, await Usuarios(new() { Desde = Agosto, Hasta = FinAgosto, Orden = "entradas" }));
    }

    [Fact]
    public async Task Filtra_por_segmento_RFM()
    {
        var historial = await _analitica.ClientesAsync();
        foreach (var segmento in historial.Select(c => c.Segmento).Distinct())
        {
            var esperado = historial.Where(c => c.Segmento == segmento).Select(c => c.UsuarioId);
            Assert.Equal(esperado, await Usuarios(new() { Segmento = segmento }));
        }
    }

    [Fact]
    public async Task Los_totales_coinciden_con_la_consulta_de_control()
    {
        var f = new FiltroClientes { Desde = Agosto, Hasta = FinAgosto, CategoriaId = 2 };
        var clientes = await _analitica.FiltrarClientesAsync(f);

        var desdeUtc = Agosto.ToUniversalTime();
        var hastaUtc = FinAgosto.AddDays(1).ToUniversalTime();
        var control = _e.Db.Ordenes.Include(o => o.Items).Include(o => o.Evento)
            .Where(o => o.Estado == EstadoOrden.Pagada && o.FechaPago >= desdeUtc && o.FechaPago < hastaUtc && o.Evento!.CategoriaId == 2)
            .ToList();

        Assert.Equal(control.Sum(o => o.Total), clientes.Sum(c => c.Gasto));
        Assert.Equal(control.Sum(o => o.Items.Sum(i => i.Cantidad)), clientes.Sum(c => c.Entradas));
        Assert.Equal(control.Count, clientes.Sum(c => c.Compras));
        Assert.Equal(control.Select(o => o.UsuarioId).Distinct().Count(), clientes.Count);
    }
}

// =============================
// 🔎 HU29: vistas (filtros, CSV, selección y acceso)
// =============================
public class HU29FiltroClientesVistaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU29FiltroClientesVistaTests(AppDePrueba app)
    {
        _app = app;
    }

    [Fact]
    public async Task Solo_el_administrador_filtra_y_exporta()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);

        foreach (var url in new[] { "/Analitica/Clientes?GastoMin=10", "/Analitica/ExportarClientes" })
        {
            Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync(url)).Headers.Location!.PathAndQuery);
            Assert.StartsWith("/Auth/Login", (await _app.Cliente().GetAsync(url)).Headers.Location!.PathAndQuery);
        }
    }

    [Fact]
    public async Task Filtra_exporta_y_solo_permite_seleccionar_a_quienes_aceptan_promociones()
    {
        var (conPromo, emailConPromo) = await HU22ClientesVistaTests.ClienteConCompraAsync(_app, promociones: true, precio: 3000);
        var (sinPromo, _) = await HU22ClientesVistaTests.ClienteConCompraAsync(_app, promociones: false, precio: 3100);
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");

        // Gasto de S/ 6000 a 6200: solo estos dos clientes
        var html = await TextoAsync(await admin.GetAsync("/Analitica/Clientes?GastoMin=6000&GastoMax=6200&Orden=gasto"));
        Assert.Contains($"Cliente {ClienteAnalitica.CodigoDe(conPromo)}", html);
        Assert.Contains($"Cliente {ClienteAnalitica.CodigoDe(sinPromo)}", html);
        Assert.True(html.IndexOf(ClienteAnalitica.CodigoDe(sinPromo)) < html.IndexOf(ClienteAnalitica.CodigoDe(conPromo))); // mayor gasto primero
        Assert.Contains($"name=\"clientes\" value=\"{conPromo}\"", html);
        Assert.DoesNotContain($"name=\"clientes\" value=\"{sinPromo}\"", html);
        Assert.Contains("action=\"/Promociones/Nueva\"", html);
        Assert.DoesNotContain(emailConPromo, html);

        var csv = await admin.GetAsync("/Analitica/ExportarClientes?GastoMin=6000&GastoMax=6200");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var texto = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        var filas = texto.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(4, filas.Count); // título, encabezado y dos clientes
        Assert.StartsWith($"1;{ClienteAnalitica.CodigoDe(sinPromo)};", filas[2]);
        Assert.Contains(";6200,00;2;1;", filas[2]);
        Assert.EndsWith(";No", filas[2]);
        Assert.EndsWith(";Sí", filas[3]);
        Assert.DoesNotContain(emailConPromo, texto);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await db.AuditoriasAnalitica.AnyAsync(a => a.Consulta == "Filtro de clientes (gasto ≥ 6000, gasto ≤ 6200)"));
        Assert.True(await db.AuditoriasAnalitica.AnyAsync(a => a.Consulta == "Exportación de clientes (gasto ≥ 6000, gasto ≤ 6200)"));
    }
}
