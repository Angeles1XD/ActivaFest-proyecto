using System.Net;
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
// 📈 HU23: ESTADÍSTICOS Y SEMÁFORO (pruebas unitarias)
// =============================
public class HU23EstadisticosTests
{
    [Fact]
    public void Media_desviacion_cv_e_iqr_del_ejemplo_del_documento()
    {
        var e = Estadisticos.De(new double[] { 120, 40, 200, 60 });

        Assert.Equal(105, e.Media);
        Assert.Equal(71.88, e.Desviacion!.Value, 2);    // √(Σ(x − μ)² ÷ (n − 1))
        Assert.Equal(68.5, e.Cv!.Value, 1);             // σ ÷ μ × 100
        Assert.Equal((55.0, 140.0, 85.0), (e.Q1!.Value, e.Q3!.Value, e.Iqr!.Value));
        Assert.Equal(267.5, e.LimiteAtipico!.Value);    // Q3 + 1,5 × IQR
    }

    [Fact]
    public void Con_un_solo_dato_no_hay_dispersion()
    {
        var e = Estadisticos.De(new double[] { 30 });
        Assert.Equal((1, 30.0), (e.N, e.Media));
        Assert.Null(e.Desviacion);
        Assert.Null(e.Cv);
        Assert.Null(Estadisticos.De(Array.Empty<double>()).Cv);
    }

    [Theory]
    [InlineData(68.0, 0.10, Semaforo.Rojo, "Demanda inestable y baja ocupación")]
    [InlineData(68.0, 0.80, Semaforo.Rojo, "Demanda inestable:")]
    [InlineData(10.0, 0.20, Semaforo.Rojo, "Ocupación baja")]
    [InlineData(35.0, 0.60, Semaforo.Ambar, "monitorear")]
    [InlineData(10.0, 0.90, Semaforo.Verde, "Demanda estable y alta ocupación")]
    [InlineData(10.0, 0.50, Semaforo.Verde, "mantener el precio")]
    public void El_semaforo_combina_el_cv_y_la_ocupacion(double cv, double ocupacion, Semaforo color, string decision)
    {
        var (c, d) = AnaliticaService.Decidir(cv, ocupacion, new UmbralesVariabilidad());
        Assert.Equal(color, c);
        Assert.Contains(decision, d);
    }

    [Fact]
    public void Los_umbrales_son_configurables_y_se_validan()
    {
        var u = new UmbralesVariabilidad { CvEstable = 30, CvInestable = 80, OcupacionBaja = 20, OcupacionAlta = 60 };
        Assert.Null(u.Validar());
        Assert.Equal(Semaforo.Ambar, AnaliticaService.Decidir(68, 0.5, u).Color);   // con los umbrales por defecto sería rojo

        Assert.NotNull(new UmbralesVariabilidad { CvEstable = 60, CvInestable = 50 }.Validar());
        Assert.NotNull(new UmbralesVariabilidad { OcupacionBaja = 80, OcupacionAlta = 70 }.Validar());
        Assert.NotNull(new UmbralesVariabilidad { OcupacionAlta = 120 }.Validar());
    }
}

// =============================
// 📈 HU23: reporte de variabilidad sobre ventas reales
// =============================
public class HU23VariabilidadTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly AnaliticaService _analitica;
    private readonly DateTime _hasta = new(2026, 11, 28);

    public HU23VariabilidadTests()
    {
        _analitica = new AnaliticaService(_e.Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        CompraServiceTests.CrearUsuarios(_e.Db, "dani", "eli");
    }

    public void Dispose() => _e.Dispose();

    private Evento Semanal(string titulo, int categoria, int aforo, decimal precio, string usuario, params int[] semanas)
    {
        var ev = _e.Evento(titulo, categoria, new DateTime(2026, 12, 20, 20, 0, 0), aforo, precio);
        for (var s = 0; s < semanas.Length; s++)
            if (semanas[s] > 0)
                _e.Compra(usuario, ev, semanas[s], _e.Desde.AddDays(7 * s + 2).AddHours(12));
        return ev;
    }

    private Task<ReporteVariabilidad> ReporteAsync(int? categoria = null) =>
        _analitica.VariabilidadAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _hasta, CategoriaId = categoria });

    [Fact]
    public async Task Cada_evento_tiene_sus_estadisticos_y_su_semaforo()
    {
        Semanal("Concierto inestable", 1, 10000, 10, "ana", 120, 40, 200, 60);   // CV 68 % y ocupación 4 %
        Semanal("Obra llena", 2, 210, 30, "beto", 50, 51, 52, 53);              // CV 2,5 % y ocupación 98 %
        Semanal("Feria floja", 4, 1000, 5, "carla", 50, 51, 52, 53);            // CV 2,5 % pero ocupación 21 %

        var r = await ReporteAsync();

        Assert.Equal(new[] { "Concierto inestable", "Feria floja", "Obra llena" }, r.Eventos.Select(e => e.Evento));   // los rojos primero

        var inestable = r.Eventos[0];
        Assert.Equal(new[] { 120, 40, 200, 60 }, inestable.Semanas);
        Assert.Equal((105.0, 71.88), (inestable.Ventas.Media, inestable.Ventas.Desviacion!.Value));
        Assert.Equal(85.0, inestable.Ventas.Iqr);
        Assert.Equal(0.042, inestable.Ocupacion!.Value, 3);
        Assert.Equal(Semaforo.Rojo, inestable.Semaforo);
        Assert.Contains("lanzar una promoción y destacar", inestable.Decision);

        Assert.Equal(Semaforo.Rojo, r.Eventos[1].Semaforo);
        Assert.StartsWith("Ocupación baja", r.Eventos[1].Decision);
        Assert.Equal(Semaforo.Verde, r.Eventos[2].Semaforo);
        Assert.Contains("alta ocupación", r.Eventos[2].Decision);
    }

    [Fact]
    public async Task Mide_la_variabilidad_de_los_precios_de_cada_evento()
    {
        var ev = Semanal("Gala", 2, 100, 50, "ana", 5, 5);
        _e.Db.TiposEntrada.Add(new TipoEntrada { EventoId = ev.Id, Nombre = "VIP", Precio = 150, Capacidad = 20 });
        _e.Db.SaveChanges();

        var fila = (await ReporteAsync()).Eventos.Single();

        Assert.Equal((50m, 150m), (fila.PrecioMin, fila.PrecioMax));
        Assert.Equal(100, fila.Precios.Media);
        Assert.Equal(70.71, fila.Precios.Desviacion!.Value, 2);
    }

    [Fact]
    public async Task Por_categoria_cuenta_las_semanas_sin_venta_y_el_gasto_por_compra()
    {
        Semanal("Rock", 1, 1000, 10, "ana", 10, 0, 30, 0);
        Semanal("Jazz", 1, 1000, 20, "beto", 0, 10, 0, 0);

        var r = await ReporteAsync(categoria: 1);

        var cat = Assert.Single(r.Categorias);
        Assert.Equal((2, 50), (cat.Eventos, cat.Entradas));
        Assert.Equal(12.5, cat.VentasSemanales.Media);                // 10, 10, 30 y 0 (las cuatro semanas del período)
        Assert.Equal(4, cat.VentasSemanales.N);
        Assert.Equal(12, cat.PrecioPagado.Media);                     // 40 entradas a S/ 10 y 10 a S/ 20
        Assert.Equal(new double[] { 100, 300, 200 }.Average(), cat.GastoPorCompra.Media);

        // Control: lo mismo que suman las órdenes pagadas
        Assert.Equal(_e.Db.Ordenes.Where(o => o.Estado == EstadoOrden.Pagada).ToList().Sum(o => o.Total), (decimal)(cat.GastoPorCompra.Media * cat.GastoPorCompra.N));
    }

    [Fact]
    public async Task Detecta_clientes_con_gasto_atipico_con_su_codigo_anonimo()
    {
        var ev = _e.Evento("Festival", 1, new DateTime(2026, 12, 20), 5000, 10);
        foreach (var (u, cantidad) in new[] { ("ana", 3), ("beto", 4), ("carla", 3), ("dani", 5), ("eli", 90) })
            _e.Compra(u, ev, cantidad, _e.Desde.AddDays(3));

        var r = await ReporteAsync();

        var atipico = Assert.Single(r.ClientesAtipicos);
        Assert.Equal((ClienteAnalitica.CodigoDe("eli"), 900m), (atipico.Codigo, atipico.Gasto));
        Assert.True(900 > r.GastoPorCliente.LimiteAtipico);
    }

    [Fact]
    public async Task Los_umbrales_guardados_cambian_el_semaforo_y_la_consulta_de_la_IA()
    {
        Semanal("Concierto", 1, 600, 10, "ana", 120, 40, 200, 60);   // CV 68 % y ocupación 70 %

        Assert.Equal(Semaforo.Rojo, (await ReporteAsync()).Eventos.Single().Semaforo);
        Assert.Equal("Inestable", (await _analitica.VariabilidadPorEventoAsync(_e.Desde, _hasta)).Single().Nivel);

        Assert.Null(await _analitica.GuardarUmbralesAsync(new UmbralesVariabilidad { CvEstable = 30, CvInestable = 80, OcupacionBaja = 40, OcupacionAlta = 70 }, "org"));
        Assert.Equal(Semaforo.Ambar, (await ReporteAsync()).Eventos.Single().Semaforo);
        Assert.Equal("Monitorear", (await _analitica.VariabilidadPorEventoAsync(_e.Desde, _hasta)).Single().Nivel);

        // Valores inválidos no se guardan
        Assert.NotNull(await _analitica.GuardarUmbralesAsync(new UmbralesVariabilidad { CvEstable = 90, CvInestable = 80 }, "org"));
        Assert.Equal(80, (await _analitica.UmbralesAsync()).CvInestable);
        Assert.Single(_e.Db.UmbralesVariabilidad);
    }
}

// =============================
// 📈 HU23: página de variabilidad (solo Administrador)
// =============================
public class HU23VariabilidadVistaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU23VariabilidadVistaTests(AppDePrueba app)
    {
        _app = app;
    }

    [Fact]
    public async Task El_administrador_ve_el_reporte_y_cambia_los_umbrales()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");

        var html = await TextoAsync(await admin.GetAsync("/Analitica/Variabilidad"));
        Assert.Contains("Variabilidad de la", html);
        Assert.Contains("Semáforo: CV", html);
        Assert.Contains("variabilidad.js", html);
        Assert.Contains("📈 Variabilidad", await TextoAsync(await admin.GetAsync("/Analitica")));

        var datos = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(admin, "/Analitica/Variabilidad"),
            ["CvEstable"] = "25", ["CvInestable"] = "60", ["OcupacionBaja"] = "35", ["OcupacionAlta"] = "75"
        };
        var r = await admin.PostAsync("/Analitica/Umbrales", new FormUrlEncodedContent(datos));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Umbrales del semáforo actualizados", await TextoAsync(await admin.GetAsync(r.Headers.Location!.OriginalString)));

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var u = await db.UmbralesVariabilidad.SingleAsync();
        Assert.Equal((25.0, 60.0, 35.0, 75.0), (u.CvEstable, u.CvInestable, u.OcupacionBaja, u.OcupacionAlta));
        Assert.True(await db.AuditoriasAnalitica.AnyAsync(a => a.Consulta.StartsWith("Variabilidad y atípicos")));
    }

    [Fact]
    public async Task Solo_el_administrador_accede()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);

        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Analitica/Variabilidad")).Headers.Location!.PathAndQuery);
        Assert.StartsWith("/Auth/Login", (await _app.Cliente().GetAsync("/Analitica/Variabilidad")).Headers.Location!.PathAndQuery);
    }
}
