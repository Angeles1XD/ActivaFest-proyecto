using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Models;
using ActivaFest.Services.Panel;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🔎 HU13 (mejora): búsqueda, filtros y paginación del panel de ventas
// =============================
public class HU13BusquedaPanelTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly PanelService _panel;

    public HU13BusquedaPanelTests()
    {
        _panel = new PanelService(_e.Db);
        CompraServiceTests.CrearUsuarios(_e.Db, "productora");
        _e.Db.Users.Single(u => u.Id == "productora").NombreCompleto = "Productora Andina";
        _e.Db.SaveChanges();

        // 22 eventos de "org" en Arequipa (12 próximos y 10 pasados) y 3 de "Productora Andina" en Cusco
        for (var i = 1; i <= 12; i++) _e.Evento($"Concierto {i:D2}", 1, DateTime.Now.AddDays(i), 100, 50);
        for (var i = 1; i <= 10; i++) _e.Evento($"Teatro {i:D2}", 2, DateTime.Now.AddDays(-i), 100, 30);
        foreach (var titulo in new[] { "Feria 100% local", "Feria gastronómica", "Rock en Cusco" })
        {
            var ev = _e.Evento(titulo, 3, DateTime.Now.AddDays(5), 100, 20);
            ev.OrganizadorId = "productora";
            ev.Ubicacion = "Cusco";
        }
        _e.Db.SaveChanges();
    }

    public void Dispose() => _e.Dispose();

    private Task<ListadoPanel> Buscar(FiltroPanel f, bool admin = true, string usuario = "admin") => _panel.BuscarAsync(usuario, admin, f);

    [Fact]
    public async Task Busca_por_titulo_o_ubicacion_sin_importar_mayusculas()
    {
        Assert.Equal(new[] { "Concierto 03" }, (await Buscar(new() { Q = "concierto 03" })).Eventos.Select(e => e.Titulo));
        Assert.Equal(3, (await Buscar(new() { Q = "CUSCO" })).Total);
        Assert.Equal(2, (await Buscar(new() { Q = "  feria " })).Total);
    }

    [Fact]
    public async Task Los_comodines_se_buscan_literalmente()
    {
        Assert.Equal(new[] { "Feria 100% local" }, (await Buscar(new() { Q = "100%" })).Eventos.Select(e => e.Titulo));
        Assert.Equal(0, (await Buscar(new() { Q = "_" })).Total);
    }

    [Fact]
    public async Task El_administrador_busca_por_organizador_y_el_organizador_solo_ve_los_suyos()
    {
        Assert.Equal(3, (await Buscar(new() { Q = "andina" })).Total);
        Assert.Equal(3, (await Buscar(new() { Q = "productora@test.pe" })).Total);

        Assert.Equal(22, (await Buscar(new(), admin: false, usuario: "org")).Total);
        Assert.Equal(0, (await Buscar(new() { Q = "Cusco" }, admin: false, usuario: "org")).Total);
        Assert.Equal(0, (await Buscar(new() { Q = "andina" }, admin: false, usuario: "org")).Total);
    }

    [Fact]
    public async Task Filtra_por_estado_y_por_fecha()
    {
        var cancelado = _e.Db.Eventos.Single(e => e.Titulo == "Concierto 05");
        cancelado.Estado = EstadoEvento.Cancelado;
        _e.Db.SaveChanges();

        Assert.Equal(new[] { "Concierto 05" }, (await Buscar(new() { Estado = EstadoEvento.Cancelado })).Eventos.Select(e => e.Titulo));
        Assert.Equal(15, (await Buscar(new() { Periodo = "proximos" })).Total);
        Assert.Equal(10, (await Buscar(new() { Periodo = "pasados" })).Total);
        Assert.Equal(10, (await Buscar(new() { Q = "teatro", Periodo = "pasados", Estado = EstadoEvento.Aprobado })).Total);
    }

    [Fact]
    public async Task Pagina_de_20_en_20_con_el_mismo_orden_del_panel()
    {
        var todos = await _panel.ResumenAsync("admin", esAdmin: true);
        var p1 = await Buscar(new() { Pagina = 1 });
        var p2 = await Buscar(new() { Pagina = 2 });

        Assert.Equal((25, 2), (p1.Total, p1.Paginas));
        Assert.Equal(20, p1.Eventos.Count);
        Assert.Equal(5, p2.Eventos.Count);
        Assert.Equal(todos.Select(e => e.Id), p1.Eventos.Concat(p2.Eventos).Select(e => e.Id));

        // Una página fuera de rango muestra la última
        Assert.Equal(2, (await Buscar(new() { Pagina = 99 })).Pagina);
        Assert.Equal(1, (await Buscar(new() { Pagina = -3 })).Pagina);
    }

    [Fact]
    public async Task Los_totales_son_de_todos_los_eventos_encontrados_no_solo_de_la_pagina()
    {
        var concierto = _e.Db.Eventos.Single(e => e.Titulo == "Concierto 01");
        var feria = _e.Db.Eventos.Single(e => e.Titulo == "Feria gastronómica");
        _e.Compra("ana", concierto, 2, DateTime.Now.AddDays(-1));
        _e.Compra("beto", feria, 3, DateTime.Now.AddDays(-1));

        var conciertos = await Buscar(new() { Q = "concierto" });
        Assert.Equal((100m, 2, 12), (conciertos.Ingresos, conciertos.Vendidas, conciertos.Proximos));

        var todos = await Buscar(new() { Pagina = 2 });
        Assert.Equal((160m, 5), (todos.Ingresos, todos.Vendidas));
        Assert.Equal(100m, (await Buscar(new() { Q = "Concierto 01" })).Eventos.Single().Ingresos);
    }
}

// =============================
// 🏷️ Panel: filtro y rendimiento por categoría
// =============================
public class HU13RendimientoCategoriaTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly PanelService _panel;

    public HU13RendimientoCategoriaTests()
    {
        _panel = new PanelService(_e.Db);
        // Categoría 1: 4 eventos de 100 (aforo 400); categoría 2: 3 eventos de 100 (uno se cancela); categoría 3: 2 eventos de 50
        var c1 = Enumerable.Range(1, 4).Select(i => _e.Evento($"Concierto {i}", 1, DateTime.Now.AddDays(i), 100, 50)).ToList();
        var c2 = Enumerable.Range(1, 3).Select(i => _e.Evento($"Teatro {i}", 2, DateTime.Now.AddDays(i), 100, 30)).ToList();
        var c3 = Enumerable.Range(1, 2).Select(i => _e.Evento($"Feria {i}", 3, DateTime.Now.AddDays(i), 50, 10)).ToList();

        _e.Compra("ana", c1[0], 20, DateTime.Now.AddDays(-1));   // cat 1: 20 / 400 = 5 %  → Baja
        _e.Compra("beto", c2[0], 60, DateTime.Now.AddDays(-1));  // cat 2: 60 / 200 = 30 % → Baja
        _e.Compra("ana", c2[1], 40, DateTime.Now.AddDays(-1));   //   (+40: 100 / 200 = 50 % → Media)
        _e.Compra("carla", c3[0], 45, DateTime.Now.AddDays(-1)); // cat 3: 80 / 100 = 80 % → Alta
        _e.Compra("carla", c3[1], 35, DateTime.Now.AddDays(-1));

        c2[2].Estado = EstadoEvento.Cancelado;                    // no cuenta para el rendimiento
        _e.Db.SaveChanges();
    }

    public void Dispose() => _e.Dispose();

    [Fact]
    public async Task Filtra_los_eventos_por_categoria()
    {
        var teatro = await _panel.BuscarAsync("admin", true, new FiltroPanel { CategoriaId = 2 });

        Assert.Equal(3, teatro.Total);
        Assert.All(teatro.Eventos, e => Assert.StartsWith("Teatro", e.Titulo));
        var nombre = _e.Db.Categorias.Single(c => c.Id == 2).Nombre;
        Assert.All(teatro.Eventos, e => Assert.Equal(nombre, e.Categoria));
        Assert.Equal((3000m, 100), (teatro.Ingresos, teatro.Vendidas));
    }

    [Fact]
    public async Task Muestra_que_categoria_se_llena_y_cual_esta_floja()
    {
        var r = (await _panel.BuscarAsync("admin", true, new FiltroPanel())).PorCategoria;

        Assert.Equal(new[] { 3, 2, 1 }, r.Select(c => c.CategoriaId));   // de mayor a menor ocupación
        Assert.Equal((2, 100, 80, "Alta"), (r[0].Eventos, r[0].Aforo, r[0].Vendidas, r[0].Nivel));
        Assert.Equal((2, 200, 100, "Media"), (r[1].Eventos, r[1].Aforo, r[1].Vendidas, r[1].Nivel)); // sin el cancelado
        Assert.Equal((4, 400, 20, "Baja"), (r[2].Eventos, r[2].Aforo, r[2].Vendidas, r[2].Nivel));
        Assert.Equal((800m, 3000m, 1000m), (r[0].Ingresos, r[1].Ingresos, r[2].Ingresos));
        Assert.Equal(0.8, r[0].Ocupacion, 3);

        // Coincide con la consulta de control sobre las entradas emitidas
        var control = _e.Db.Entradas.Count(x => x.Evento!.CategoriaId == 3 && x.Evento.Estado == EstadoEvento.Aprobado);
        Assert.Equal(control, r[0].Vendidas);
    }

    [Fact]
    public async Task El_rendimiento_respeta_la_busqueda()
    {
        var r = (await _panel.BuscarAsync("admin", true, new FiltroPanel { Q = "Concierto 1" })).PorCategoria;
        Assert.Equal((1, 1, 100, 20), (r.Single().CategoriaId, r.Single().Eventos, r.Single().Aforo, r.Single().Vendidas));
    }
}

public class HU13BusquedaPanelVistaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU13BusquedaPanelVistaTests(AppDePrueba app)
    {
        _app = app;
    }

    [Fact]
    public async Task El_panel_tiene_buscador_y_muestra_solo_lo_encontrado()
    {
        var titulo = $"Evento buscable {Guid.NewGuid():N}"[..28];
        var (_, email) = await HU22ClientesVistaTests.ClienteConCompraAsync(_app);
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ActivaFest.Data.ApplicationDbContext>();
            var ev = db.Eventos.OrderByDescending(e => e.Id).First(e => e.Titulo == "Festival HU22");
            ev.Titulo = titulo;
            await db.SaveChangesAsync();
        }

        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");

        var html = await TextoAsync(await admin.GetAsync($"/Panel?Q={Uri.EscapeDataString(titulo)}"));
        Assert.Contains("name=\"Q\"", html);
        Assert.Contains("name=\"CategoriaId\"", html);
        Assert.Contains("Rendimiento por categoría", html);
        Assert.Contains($"<strong>{titulo}</strong>", html);
        Assert.Contains("1–1 de 1 evento", html);
        Assert.DoesNotContain("<strong>Festival HU22</strong>", html);

        var vacio = await TextoAsync(await admin.GetAsync("/Panel?Q=no-existe-ningun-evento-asi"));
        Assert.Contains("Ningún evento coincide con «no-existe-ningun-evento-asi»", vacio);
    }
}
