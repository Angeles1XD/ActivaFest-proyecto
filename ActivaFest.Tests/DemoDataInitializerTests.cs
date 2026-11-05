using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;

namespace ActivaFest.Tests;

public sealed class DemoDataInitializerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ApplicationDbContext _db;

    public DemoDataInitializerTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser
        {
            Id = "demo-admin",
            UserName = "admin@demo.activafest.test",
            NormalizedUserName = "ADMIN@DEMO.ACTIVAFEST.TEST",
            Email = "admin@demo.activafest.test",
            NormalizedEmail = "ADMIN@DEMO.ACTIVAFEST.TEST",
            NombreCompleto = "Administrador de prueba",
            SecurityStamp = Guid.NewGuid().ToString("N")
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Siembra_tres_eventos_por_categoria_y_no_duplica_al_reiniciar()
    {
        _db.Eventos.Add(new Evento
        {
            Titulo = "Evento existente que debe conservarse",
            Descripcion = "Registro anterior",
            CategoriaId = 1,
            Ubicacion = "Arequipa",
            Fecha = DateTime.Today.AddDays(-1),
            Aforo = 100,
            OrganizadorId = "demo-admin",
            Estado = EstadoEvento.Aprobado
        });
        await _db.SaveChangesAsync();

        await DemoDataInitializer.SeedEventosAsync(_db, "demo-admin", NullLogger.Instance);
        await DemoDataInitializer.SeedEventosAsync(_db, "demo-admin", NullLogger.Instance);

        var futuros = await _db.Eventos.AsNoTracking()
            .Where(e => e.Fecha >= DateTime.Today && !e.Titulo.StartsWith("Histórico demo ·"))
            .Include(e => e.TiposEntrada).ThenInclude(t => t.Asientos)
            .Include(e => e.Imagenes)
            .ToListAsync();

        Assert.Equal(30, futuros.Count);
        Assert.Equal(30, futuros.Select(e => e.Titulo).Distinct().Count());
        Assert.Equal(Enumerable.Repeat(3, 10), futuros.GroupBy(e => e.CategoriaId).Select(g => g.Count()).OrderBy(n => n));
        Assert.All(futuros, evento =>
        {
            Assert.Equal(EstadoEvento.Aprobado, evento.Estado);
            Assert.NotNull(evento.OrganizadorId);
            Assert.InRange(evento.Fecha, DateTime.Today.AddDays(1), new DateTime(2027, 12, 31, 23, 59, 59));
            Assert.InRange(evento.Descripcion.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, 20, 40);
            Assert.Equal(evento.Aforo, evento.TiposEntrada.Sum(t => t.Capacidad));
            Assert.Contains(evento.TiposEntrada, t => t.Nombre == "General" && t.Precio > 0);
            Assert.Contains(evento.TiposEntrada, t => t.Nombre == "VIP" && t.Precio > 0);
            Assert.Single(evento.Imagenes);
            Assert.StartsWith("/img/categorias/", evento.Imagenes.Single().Ruta);
            Assert.All(evento.TiposEntrada, tipo => Assert.Equal(0, tipo.Vendidas));
        });
        Assert.Equal("Evento existente que debe conservarse", await _db.Eventos
            .Where(e => e.Titulo == "Evento existente que debe conservarse")
            .Select(e => e.Titulo)
            .SingleAsync());

        var teatros = futuros.Where(e => e.PlantillaPlano == PlantillaPlano.Teatro).ToList();
        Assert.NotEmpty(teatros);
        Assert.All(teatros, evento =>
        {
            Assert.Equal(evento.Aforo, evento.TiposEntrada.Sum(t => t.Asientos.Count));
            Assert.All(evento.TiposEntrada, tipo => Assert.True(tipo.AsientosNumerados));
        });
    }

    [Fact]
    public async Task Siembra_ventas_solo_en_el_periodo_y_las_concilia_con_entradas_y_dias()
    {
        await DemoDataInitializer.SeedEventosAsync(_db, "demo-admin", NullLogger.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Comisiones:Porcentaje"] = "8" })
            .Build();

        await DemoDataInitializer.SeedVentasAsync(_db, "demo-admin", config, NullLogger.Instance);
        var cantidadInicial = await _db.Ordenes.CountAsync();
        await DemoDataInitializer.SeedVentasAsync(_db, "demo-admin", config, NullLogger.Instance);

        var ordenes = await _db.Ordenes.AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.Entradas)
            .Include(o => o.Evento)
            .Where(o => o.ProveedorPago == "Demo académico")
            .ToListAsync();
        Assert.NotEmpty(ordenes);
        Assert.Equal(cantidadInicial, ordenes.Count);
        Assert.All(ordenes, orden =>
        {
            var fechaLocal = orden.FechaPago!.Value.ToLocalTime();
            Assert.InRange(fechaLocal.Date, new DateTime(2026, 7, 1), DateTime.Today);
            Assert.True(fechaLocal <= DateTime.Now);
            Assert.Equal(EstadoOrden.Pagada, orden.Estado);
            Assert.Equal(orden.Total, orden.Items.Sum(i => i.PrecioUnitario * i.Cantidad));
            Assert.Equal(orden.Items.Sum(i => i.Cantidad), orden.Entradas.Count);
            Assert.StartsWith("Histórico demo ·", orden.Evento!.Titulo);
            Assert.True(orden.Evento.Fecha >= fechaLocal);
        });

        var diasConCompras = ordenes.GroupBy(o => o.FechaPago!.Value.ToLocalTime().DayOfWeek).Select(g => g.Key).ToHashSet();
        Assert.Equal(Enum.GetValues<DayOfWeek>().OrderBy(d => d), diasConCompras.OrderBy(d => d));
        Assert.Equal(ordenes.Sum(o => o.Total), await _db.Ordenes
            .Where(o => o.ProveedorPago == "Demo académico")
            .SumAsync(o => o.Total));
        Assert.Equal(ordenes.Sum(o => o.Entradas.Count), await _db.TiposEntrada
            .Where(t => t.Evento!.Titulo.StartsWith("Histórico demo ·"))
            .SumAsync(t => t.Vendidas));

        var analitica = new AnaliticaService(_db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var resumen = await analitica.AnalisisAsync(new FiltroAnalitica
        {
            Desde = new DateTime(2026, 7, 1),
            Hasta = DateTime.Today
        });
        Assert.Equal(ordenes.Count, resumen.Compras);
        Assert.Equal(ordenes.Sum(o => o.Total), resumen.Meses.Sum(m => m.Ingresos));
        Assert.Equal(ordenes.Sum(o => o.Total), resumen.DiasSemana.Sum(d => d.Ingresos));
        Assert.Equal(ordenes.Count, resumen.DiasSemana.Sum(d => d.Compras));
        Assert.All(resumen.DiasSemana, dia => Assert.True(dia.Compras > 0));
        Assert.True(resumen.CompradoresRecurrentes > 0);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
