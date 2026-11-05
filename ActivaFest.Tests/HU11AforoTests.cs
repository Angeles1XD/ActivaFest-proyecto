using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Compras;

namespace ActivaFest.Tests;

// =============================
// 🔐 HU11: TOKEN DE CONCURRENCIA, BLOQUEO DISTRIBUIDO Y LIBERACIÓN A LOS 10 MINUTOS
// =============================
public class HU11AforoTests : IDisposable
{
    private readonly SqliteConnection _conexion;
    private readonly ApplicationDbContext _db;
    private readonly CompraService _compras;

    public HU11AforoTests()
    {
        _conexion = new SqliteConnection("Data Source=:memory:");
        _conexion.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conexion).Options);
        _db.Database.EnsureCreated();
        _compras = Fabrica.Compras(_db, new EmailFalso());
        CompraServiceTests.CrearUsuarios(_db, "ana", "beto");
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexion.Dispose();
    }

    private TipoEntrada Releer(int id) => _db.TiposEntrada.AsNoTracking().Single(t => t.Id == id);

    [Fact]
    public async Task Cada_cambio_de_aforo_sube_la_version_de_la_zona()
    {
        var (evento, general, _) = CompraServiceTests.CrearEvento(_db);
        var v0 = Releer(general.Id).RowVersion;

        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;
        var v1 = Releer(general.Id).RowVersion;
        await _compras.ConfirmarPagoAsync(orden.Codigo, "Simulado", "SIM", orden.Total, "https://x");
        var v2 = Releer(general.Id).RowVersion;

        Assert.True(v0 < v1 && v1 < v2);
    }

    // El token de EF: guardar una zona leída antes de una venta no pisa la venta
    [Fact]
    public async Task Guardar_una_zona_con_una_version_vieja_falla_en_lugar_de_pisar_la_venta()
    {
        var (evento, general, _) = CompraServiceTests.CrearEvento(_db);

        using var otroContexto = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conexion).Options);
        var leidaAntes = otroContexto.TiposEntrada.Single(t => t.Id == general.Id);

        await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 3 }); // cambia la versión

        leidaAntes.Capacidad = 2; // intento de guardar con datos viejos: dejaría la zona por debajo de lo reservado
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => otroContexto.SaveChangesAsync());
        Assert.Equal(3, Releer(general.Id).Reservadas);
    }

    // Criterio de HU11: una reserva no pagada se libera a los 10 minutos, aunque el proceso periódico aún no haya pasado
    [Fact]
    public async Task Una_reserva_no_pagada_se_libera_a_los_diez_minutos()
    {
        var (evento, general, _) = CompraServiceTests.CrearEvento(_db, capacidadGeneral: 2);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;
        Assert.InRange((orden.FechaExpiracion - DateTime.UtcNow).TotalMinutes, 9.9, 10.01);

        // Antes de los 10 minutos: no hay aforo para otro comprador
        Assert.NotNull((await _compras.CrearOrdenAsync("beto", evento.Id, new Dictionary<int, int> { [general.Id] = 1 })).Error);

        // Pasados los 10 minutos: la reserva de ana se libera al instante y beto puede comprar
        await _db.Ordenes.Where(o => o.Id == orden.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.FechaExpiracion, DateTime.UtcNow.AddSeconds(-1)));
        var r = await _compras.CrearOrdenAsync("beto", evento.Id, new Dictionary<int, int> { [general.Id] = 2 });

        Assert.Null(r.Error);
        Assert.Equal(EstadoOrden.Expirada, _db.Ordenes.AsNoTracking().Single(o => o.Id == orden.Id).Estado);
        Assert.Equal(2, Releer(general.Id).Reservadas);
    }

    [Fact]
    public async Task El_bloqueo_es_exclusivo_y_se_libera_al_terminar()
    {
        var bloqueo = new BloqueoEnMemoria();

        var primero = await bloqueo.AdquirirAsync("aforo:evento:1", TimeSpan.FromSeconds(10), TimeSpan.Zero);
        var segundo = await bloqueo.AdquirirAsync("aforo:evento:1", TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));
        var otroEvento = await bloqueo.AdquirirAsync("aforo:evento:2", TimeSpan.FromSeconds(10), TimeSpan.Zero);

        Assert.NotNull(primero);
        Assert.Null(segundo);        // el mismo evento espera
        Assert.NotNull(otroEvento);  // otro evento no

        await primero!.DisposeAsync();
        await primero.DisposeAsync(); // soltarlo dos veces no rompe nada
        Assert.NotNull(await bloqueo.AdquirirAsync("aforo:evento:1", TimeSpan.FromSeconds(10), TimeSpan.Zero));
    }

    // Si el bloqueo no se obtiene (demanda extrema), la compra se rechaza sin reservar nada
    [Fact]
    public async Task Sin_bloqueo_no_se_reserva()
    {
        var (evento, general, _) = CompraServiceTests.CrearEvento(_db);
        var ocupado = new BloqueoEnMemoria();
        await using var tomado = await ocupado.AdquirirAsync($"aforo:evento:{evento.Id}", TimeSpan.FromSeconds(30), TimeSpan.Zero);
        var compras = Fabrica.Compras(_db, new EmailFalso(), bloqueo: new BloqueoQueNuncaSeObtiene());

        var r = await compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 1 });

        Assert.Contains("mucha demanda", r.Error);
        Assert.Equal(0, Releer(general.Id).Reservadas);
    }

    private class BloqueoQueNuncaSeObtiene : IBloqueoDistribuido
    {
        public Task<IAsyncDisposable?> AdquirirAsync(string clave, TimeSpan duracion, TimeSpan espera, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }
}
