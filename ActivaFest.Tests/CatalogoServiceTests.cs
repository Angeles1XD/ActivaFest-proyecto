using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;

namespace ActivaFest.Tests;

// Pruebas del catálogo (HU02) y filtros (HU05) sobre SQLite en memoria, el mismo motor que usa la app
public class CatalogoServiceTests : IDisposable
{
    private const int Conciertos = 1;
    private const int Teatro = 2;

    private readonly SqliteConnection _conexion;
    private readonly ApplicationDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly CatalogoService _servicio;

    public CatalogoServiceTests()
    {
        _conexion = new SqliteConnection("Data Source=:memory:");
        _conexion.Open();

        var opciones = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conexion).Options;
        _db = new ApplicationDbContext(opciones);
        _db.Database.EnsureCreated();

        _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _servicio = new CatalogoService(_db, _cache, NullLogger<CatalogoService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexion.Dispose();
    }

    private Evento Agregar(string titulo, int categoriaId = Conciertos, int dias = 10,
        EstadoEvento estado = EstadoEvento.Aprobado, string ubicacion = "Lima", params decimal[] precios)
    {
        var evento = new Evento
        {
            Titulo = titulo,
            CategoriaId = categoriaId,
            Ubicacion = ubicacion,
            Fecha = DateTime.Today.AddDays(dias).AddHours(20),
            Estado = estado,
            TiposEntrada = precios.Select((p, i) => new TipoEntrada { Nombre = $"Zona {i}", Precio = p, Capacidad = 100 }).ToList()
        };
        _db.Eventos.Add(evento);
        _db.SaveChanges();
        return evento;
    }

    private static List<string> Titulos(ResultadoCatalogo r) => r.Eventos.Select(e => e.Titulo).ToList();

    [Fact]
    public async Task Solo_muestra_eventos_aprobados_y_futuros()
    {
        Agregar("Aprobado", precios: 50);
        Agregar("Pendiente", estado: EstadoEvento.Pendiente, precios: 50);
        Agregar("Rechazado", estado: EstadoEvento.Rechazado, precios: 50);
        Agregar("Pasado", dias: -3, precios: 50);

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro());

        Assert.Equal(new[] { "Aprobado" }, Titulos(resultado));
    }

    [Fact]
    public async Task No_muestra_eventos_de_hoy_que_ya_empezaron()
    {
        Agregar("Mas tarde", dias: 2, precios: 50);
        var empezado = Agregar("Ya empezo", precios: 50);
        empezado.Fecha = DateTime.Now.AddMinutes(-30);
        _db.SaveChanges();

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro { Desde = DateTime.Today });

        Assert.Equal(new[] { "Mas tarde" }, Titulos(resultado));
    }

    [Theory]
    [InlineData(OrdenCatalogo.FechaAsc)]
    [InlineData(OrdenCatalogo.Recientes)]
    [InlineData(OrdenCatalogo.PrecioAsc)]
    [InlineData(OrdenCatalogo.PrecioDesc)]
    public async Task Los_eventos_destacados_aparecen_primero(OrdenCatalogo orden)
    {
        CompraServiceTests.CrearUsuarios(_db, "org");
        Agregar("Pronto y barato", dias: 1, precios: 10);
        var destacado = Agregar("Destacado", dias: 30, precios: 90);
        Agregar("Normal", dias: 5, precios: 40);

        _db.ContratosDestacado.Add(new ContratoDestacado
        {
            Codigo = "DST-prueba", EventoId = destacado.Id, PaqueteId = 1, OrganizadorId = "org", Precio = 29.90m, Dias = 3, Prioridad = 1,
            Estado = EstadoContrato.Activo, FechaInicio = DateTime.Now.AddDays(-1), FechaFin = DateTime.Now.AddDays(2)
        });
        _db.SaveChanges();

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro { Orden = orden });

        Assert.Equal("Destacado", resultado.Eventos[0].Titulo);
        Assert.True(resultado.Eventos[0].Destacado);
    }

    [Fact]
    public async Task Un_destacado_vencido_vuelve_a_su_lugar_normal()
    {
        CompraServiceTests.CrearUsuarios(_db, "org");
        Agregar("Pronto", dias: 1, precios: 10);
        var vencido = Agregar("Fue destacado", dias: 30, precios: 90);
        _db.ContratosDestacado.Add(new ContratoDestacado
        {
            Codigo = "DST-vencido", EventoId = vencido.Id, PaqueteId = 1, OrganizadorId = "org", Precio = 29.90m, Dias = 3, Prioridad = 1,
            Estado = EstadoContrato.Activo, FechaInicio = DateTime.Now.AddDays(-5), FechaFin = DateTime.Now.AddDays(-2)
        });
        _db.SaveChanges();

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro());

        Assert.Equal(new[] { "Pronto", "Fue destacado" }, Titulos(resultado));
    }

    // RNF02: el catálogo carga en menos de 3 s (se mide la consulta sin caché sobre 1 000 eventos)
    [Fact]
    public async Task El_catalogo_responde_en_menos_de_3_segundos_con_mil_eventos()
    {
        _db.Eventos.AddRange(Enumerable.Range(1, 1000).Select(i => new Evento
        {
            Titulo = $"Evento {i}", CategoriaId = 1 + i % 9, Ubicacion = "Lima", Fecha = DateTime.Today.AddDays(1 + i % 60).AddHours(20),
            Estado = EstadoEvento.Aprobado,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = i % 200, Capacidad = 100 } }
        }));
        _db.SaveChanges();

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro { Buscar = "evento", Orden = OrdenCatalogo.PrecioAsc });
        reloj.Stop();

        Assert.Equal(1000, resultado.Total);
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(3), $"Tardó {reloj.Elapsed.TotalMilliseconds:N0} ms");
    }

    [Fact]
    public async Task Filtra_por_categoria()
    {
        Agregar("Rock Fest", Conciertos, precios: 80);
        Agregar("Hamlet", Teatro, precios: 40);

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro { CategoriaId = Teatro });

        Assert.Equal(new[] { "Hamlet" }, Titulos(resultado));
    }

    // HU05: fecha, precio y categoría se combinan (cada evento descartado falla en un solo criterio)
    [Fact]
    public async Task Combina_filtros_de_fecha_precio_y_categoria()
    {
        Agregar("Cumple todo", Conciertos, dias: 5, precios: 50);
        Agregar("Muy caro", Conciertos, dias: 5, precios: 120);
        Agregar("Otra categoria", Teatro, dias: 5, precios: 50);
        Agregar("Fuera de fecha", Conciertos, dias: 20, precios: 50);

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro
        {
            CategoriaId = Conciertos,
            Desde = DateTime.Today.AddDays(3),
            Hasta = DateTime.Today.AddDays(10),
            PrecioMin = 30,
            PrecioMax = 80
        });

        Assert.Equal(new[] { "Cumple todo" }, Titulos(resultado));
    }

    [Fact]
    public async Task Combina_texto_categoria_y_solo_gratis()
    {
        Agregar("Rock gratis", Conciertos, precios: 0);
        Agregar("Rock pagado", Conciertos, precios: 40);
        Agregar("Teatro gratis rock", Teatro, precios: 0);

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro { Buscar = "rock", CategoriaId = Conciertos, SoloGratis = true });

        Assert.Equal(new[] { "Rock gratis" }, Titulos(resultado));
    }

    [Fact]
    public async Task Busqueda_por_texto_ignora_mayusculas_y_busca_en_ubicacion_y_categoria()
    {
        Agregar("Festival de JAZZ", ubicacion: "Arequipa", precios: 30);
        Agregar("Obra infantil", Teatro, ubicacion: "Cusco", precios: 20);

        Assert.Equal(new[] { "Festival de JAZZ" }, Titulos(await _servicio.BuscarAsync(new CatalogoFiltro { Buscar = "jazz" })));
        Assert.Equal(new[] { "Obra infantil" }, Titulos(await _servicio.BuscarAsync(new CatalogoFiltro { Buscar = "cusco" })));
        Assert.Equal(new[] { "Obra infantil" }, Titulos(await _servicio.BuscarAsync(new CatalogoFiltro { Buscar = "teatro" })));
    }

    [Fact]
    public async Task Busqueda_trata_comodines_como_texto()
    {
        Agregar("Descuento 50%", precios: 10);
        Agregar("Concierto normal", precios: 10);

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro { Buscar = "%" });

        Assert.Equal(new[] { "Descuento 50%" }, Titulos(resultado));
    }

    [Fact]
    public async Task Filtro_de_precio_exige_un_mismo_tipo_de_entrada_dentro_del_rango()
    {
        Agregar("Barato y VIP caro", precios: new[] { 10m, 200m });
        Agregar("En rango", precios: 75);

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro { PrecioMin = 50, PrecioMax = 100 });

        Assert.Equal(new[] { "En rango" }, Titulos(resultado));
    }

    [Fact]
    public async Task Solo_gratis_devuelve_eventos_con_entrada_de_precio_cero()
    {
        Agregar("Gratis", precios: 0);
        Agregar("De pago", precios: 25);

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro { SoloGratis = true });

        Assert.Equal(new[] { "Gratis" }, Titulos(resultado));
        Assert.Equal(0m, resultado.Eventos[0].PrecioDesde);
    }

    [Fact]
    public async Task Filtro_de_fechas_incluye_el_dia_hasta_completo()
    {
        Agregar("En 5 dias", dias: 5, precios: 10);
        Agregar("En 20 dias", dias: 20, precios: 10);

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro
        {
            Desde = DateTime.Today,
            Hasta = DateTime.Today.AddDays(5)
        });

        Assert.Equal(new[] { "En 5 dias" }, Titulos(resultado));
    }

    [Fact]
    public async Task Ordena_por_precio_desde_ascendente_y_descendente()
    {
        Agregar("Medio", precios: 50);
        Agregar("Caro", precios: new[] { 150m, 300m });
        Agregar("Barato", precios: 5);

        var asc = await _servicio.BuscarAsync(new CatalogoFiltro { Orden = OrdenCatalogo.PrecioAsc });
        var desc = await _servicio.BuscarAsync(new CatalogoFiltro { Orden = OrdenCatalogo.PrecioDesc });

        Assert.Equal(new[] { "Barato", "Medio", "Caro" }, Titulos(asc));
        Assert.Equal(new[] { "Caro", "Medio", "Barato" }, Titulos(desc));
        Assert.Equal(150m, asc.Eventos[2].PrecioDesde);
    }

    [Fact]
    public async Task Pagina_los_resultados_y_ajusta_paginas_fuera_de_rango()
    {
        for (var i = 1; i <= 12; i++)
            Agregar($"Evento {i:00}", dias: i, precios: 10);

        var pagina2 = await _servicio.BuscarAsync(new CatalogoFiltro { Pagina = 2 });
        var fueraDeRango = await _servicio.BuscarAsync(new CatalogoFiltro { Pagina = 99 });

        Assert.Equal(12, pagina2.Total);
        Assert.Equal(2, pagina2.TotalPaginas);
        Assert.Equal(new[] { "Evento 10", "Evento 11", "Evento 12" }, Titulos(pagina2));
        Assert.Equal(2, fueraDeRango.Pagina);
    }

    [Fact]
    public async Task Usa_cache_hasta_que_se_invalida_el_catalogo()
    {
        Agregar("Primero", precios: 10);
        var antes = await _servicio.BuscarAsync(new CatalogoFiltro());

        Agregar("Recien aprobado", precios: 10);
        var enCache = await _servicio.BuscarAsync(new CatalogoFiltro());

        await _servicio.InvalidarAsync();
        var despues = await _servicio.BuscarAsync(new CatalogoFiltro());

        Assert.Equal(1, antes.Total);
        Assert.Equal(1, enCache.Total);
        Assert.Equal(2, despues.Total);
    }

    [Fact]
    public async Task Evento_sin_precios_muestra_precio_por_confirmar()
    {
        Agregar("Sin entradas");

        var resultado = await _servicio.BuscarAsync(new CatalogoFiltro());

        Assert.Null(resultado.Eventos[0].PrecioDesde);
        Assert.Equal("Precio por confirmar", Formato.PrecioDesde(resultado.Eventos[0].PrecioDesde));
    }

    [Fact]
    public async Task Cuenta_solo_eventos_publicados_y_proximos_por_categoria()
    {
        Agregar("Rock 1", Conciertos, precios: 10);
        Agregar("Rock 2", Conciertos, precios: 10);
        Agregar("Hamlet", Teatro, precios: 10);
        Agregar("Pendiente", Teatro, estado: EstadoEvento.Pendiente, precios: 10);
        Agregar("Pasado", Teatro, dias: -2, precios: 10);

        var conteo = await _servicio.ContarPorCategoriaAsync();

        Assert.Equal(2, conteo[Conciertos]);
        Assert.Equal(1, conteo[Teatro]);
        Assert.False(conteo.ContainsKey(3));
    }

    [Fact]
    public async Task Conteo_por_categoria_se_actualiza_al_invalidar()
    {
        Agregar("Rock 1", Conciertos, precios: 10);
        await _servicio.ContarPorCategoriaAsync();

        Agregar("Rock 2", Conciertos, precios: 10);
        await _servicio.InvalidarAsync();

        Assert.Equal(2, (await _servicio.ContarPorCategoriaAsync())[Conciertos]);
    }

    [Fact]
    public async Task Categorias_iniciales_tienen_color_e_imagen()
    {
        var categorias = await _servicio.ObtenerCategoriasAsync();

        Assert.Equal(10, categorias.Count);
        Assert.All(categorias, c =>
        {
            Assert.Equal(c.Color, Formato.Color(c.Color));
            Assert.StartsWith("/img/categorias/", c.Imagen);
        });
        Assert.Equal("Otros", categorias.Last().Nombre);
    }

    [Theory]
    [InlineData("#a855f7", "#a855f7")]
    [InlineData("red; background:url(x)", "#8b5cf6")]
    [InlineData("#fff", "#8b5cf6")]
    [InlineData(null, "#8b5cf6")]
    public void Color_solo_acepta_hexadecimal_de_6_digitos(string? entrada, string esperado)
    {
        Assert.Equal(esperado, Formato.Color(entrada));
    }

    [Fact]
    public void Normalizar_corrige_rangos_invertidos_y_pagina_invalida()
    {
        var filtro = new CatalogoFiltro
        {
            Buscar = "   ",
            PrecioMin = 100,
            PrecioMax = 20,
            Desde = DateTime.Today.AddDays(10),
            Hasta = DateTime.Today,
            Pagina = -4
        };

        filtro.Normalizar();

        Assert.Null(filtro.Buscar);
        Assert.Equal(20, filtro.PrecioMin);
        Assert.Equal(100, filtro.PrecioMax);
        Assert.True(filtro.Desde < filtro.Hasta);
        Assert.Equal(1, filtro.Pagina);
    }
}
