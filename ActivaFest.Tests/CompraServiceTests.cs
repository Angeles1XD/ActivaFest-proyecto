using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Email;
using ActivaFest.Services.Entradas;

namespace ActivaFest.Tests;

// =============================
// Dobles de prueba compartidos
// =============================
public class EmailFalso : IEmailService
{
    public List<(string Para, string Asunto, int Adjuntos)> Enviados { get; } = new();

    public Task EnviarAsync(string destinatario, string asunto, string html, IReadOnlyList<AdjuntoCorreo>? adjuntos = null)
    {
        lock (Enviados) Enviados.Add((destinatario, asunto, adjuntos?.Count ?? 0));
        return Task.CompletedTask;
    }
}

public class EntornoFalso : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "ActivaFest";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public static class Fabrica
{
    public static QrEntradaService Qr(string secreto = "secreto-de-pruebas") =>
        new(Options.Create(new QrOptions { Secreto = secreto }), new EntornoFalso(), NullLogger<QrEntradaService>.Instance);

    public static CompraService Compras(ApplicationDbContext db, IEmailService email, IDistributedCache? cache = null, decimal comision = 8m,
        IBloqueoDistribuido? bloqueo = null, IBloqueoAsientos? asientos = null) =>
        new(db,
            new CatalogoService(db, cache ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), NullLogger<CatalogoService>.Instance),
            Qr(), email, Options.Create(new ComprasOptions()), Options.Create(new ComisionesOptions { Porcentaje = comision }),
            bloqueo ?? new BloqueoEnMemoria(), asientos ?? new BloqueoAsientosEnMemoria(), NullLogger<CompraService>.Instance);
}

// =============================
// 🛒 COMPRAS, AFORO Y EMISIÓN (HU06, HU07, HU11)
// =============================
public class CompraServiceTests : IDisposable
{
    private const string Url = "https://activafest.test/Entradas";

    private readonly SqliteConnection _conexion;
    private readonly ApplicationDbContext _db;
    private readonly EmailFalso _email = new();
    private readonly CompraService _compras;

    public CompraServiceTests()
    {
        _conexion = new SqliteConnection("Data Source=:memory:");
        _conexion.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conexion).Options);
        _db.Database.EnsureCreated();
        _compras = Fabrica.Compras(_db, _email);

        CrearUsuarios(_db, "ana", "beto");
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexion.Dispose();
    }

    internal static void CrearUsuarios(ApplicationDbContext db, params string[] ids)
    {
        foreach (var id in ids)
            db.Users.Add(new ApplicationUser { Id = id, UserName = id, Email = $"{id}@test.pe", NombreCompleto = id });
        db.SaveChanges();
    }

    internal static (Evento Evento, TipoEntrada General, TipoEntrada Vip) CrearEvento(ApplicationDbContext db,
        int capacidadGeneral = 5, int capacidadVip = 2, int dias = 10, EstadoEvento estado = EstadoEvento.Aprobado)
    {
        var general = new TipoEntrada { Nombre = "General", Precio = 50, Capacidad = capacidadGeneral };
        var vip = new TipoEntrada { Nombre = "VIP", Precio = 120.5m, Capacidad = capacidadVip };
        var evento = new Evento
        {
            Titulo = "Concierto de prueba",
            CategoriaId = 1,
            Ubicacion = "Lima",
            Fecha = DateTime.Now.AddDays(dias),
            Estado = estado,
            TiposEntrada = { general, vip }
        };
        db.Eventos.Add(evento);
        db.SaveChanges();
        return (evento, general, vip);
    }

    private TipoEntrada Releer(int id) => _db.TiposEntrada.AsNoTracking().Single(t => t.Id == id);

    private EstadoOrden EstadoDe(string codigo) => _db.Ordenes.AsNoTracking().Single(o => o.Codigo == codigo).Estado;

    [Fact]
    public async Task Crear_orden_reserva_el_aforo_y_calcula_el_total()
    {
        var (evento, general, vip) = CrearEvento(_db);

        var r = await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 3, [vip.Id] = 1 });

        Assert.Null(r.Error);
        Assert.Equal(270.5m, r.Orden!.Total);
        Assert.Equal(3, Releer(general.Id).Reservadas);
        Assert.Equal(2, Releer(general.Id).Disponibles);
        Assert.Equal(1, Releer(vip.Id).Reservadas);
    }

    [Fact]
    public async Task No_reserva_mas_entradas_de_las_disponibles()
    {
        var (evento, general, _) = CrearEvento(_db, capacidadGeneral: 2);

        var r = await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 3 });

        Assert.NotNull(r.Error);
        Assert.Equal(0, Releer(general.Id).Reservadas);
        Assert.Empty(_db.Ordenes);
    }

    [Fact]
    public async Task Si_una_zona_no_alcanza_no_se_reserva_ninguna()
    {
        var (evento, general, vip) = CrearEvento(_db, capacidadGeneral: 5, capacidadVip: 1);

        var r = await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2, [vip.Id] = 2 });

        Assert.NotNull(r.Error);
        Assert.Equal(0, Releer(general.Id).Reservadas);
        Assert.Equal(0, Releer(vip.Id).Reservadas);
    }

    [Theory]
    [InlineData(-2, EstadoEvento.Aprobado)]
    [InlineData(10, EstadoEvento.Pendiente)]
    public async Task No_vende_eventos_pasados_ni_sin_aprobar(int dias, EstadoEvento estado)
    {
        var (evento, general, _) = CrearEvento(_db, dias: dias, estado: estado);

        var r = await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 1 });

        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Limita_la_cantidad_de_entradas_por_compra()
    {
        var (evento, general, _) = CrearEvento(_db, capacidadGeneral: 50);

        var r = await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 11 });

        Assert.Contains("10", r.Error);
    }

    [Fact]
    public async Task Rechaza_tipos_de_entrada_de_otro_evento()
    {
        var (evento, _, _) = CrearEvento(_db);
        var (_, generalOtro, _) = CrearEvento(_db);

        var r = await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [generalOtro.Id] = 1 });

        Assert.NotNull(r.Error);
        Assert.Equal(0, Releer(generalOtro.Id).Reservadas);
    }

    [Fact]
    public async Task Confirmar_pago_emite_una_entrada_por_persona_y_envia_los_qr()
    {
        var (evento, general, vip) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2, [vip.Id] = 1 })).Orden!;

        var resultado = await _compras.ConfirmarPagoAsync(orden.Codigo, "Simulado", "SIM-1", orden.Total, Url);

        Assert.Equal(ResultadoConfirmacion.Confirmada, resultado);
        Assert.Equal(EstadoOrden.Pagada, EstadoDe(orden.Codigo));
        Assert.Equal(0, Releer(general.Id).Reservadas);
        Assert.Equal(2, Releer(general.Id).Vendidas);
        Assert.Equal(1, Releer(vip.Id).Vendidas);

        var entradas = _db.Entradas.AsNoTracking().ToList();
        Assert.Equal(3, entradas.Count);
        Assert.Equal(3, entradas.Select(e => e.Codigo).Distinct().Count());
        Assert.All(entradas, e => Assert.Equal(EstadoEntrada.Activa, e.Estado));

        var correo = Assert.Single(_email.Enviados);
        Assert.Equal("ana@test.pe", correo.Para);
        Assert.Equal(3 + 1, correo.Adjuntos); // un QR por entrada + el plano con la ubicación (HU27)
    }

    [Fact]
    public async Task Confirmar_dos_veces_no_duplica_entradas_ni_ventas()
    {
        var (evento, general, _) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;

        await _compras.ConfirmarPagoAsync(orden.Codigo, "MercadoPago", "123", orden.Total, Url);
        var segunda = await _compras.ConfirmarPagoAsync(orden.Codigo, "MercadoPago", "123", orden.Total, Url);

        Assert.Equal(ResultadoConfirmacion.YaEstabaConfirmada, segunda);
        Assert.Equal(2, _db.Entradas.Count());
        Assert.Equal(2, Releer(general.Id).Vendidas);
        Assert.Single(_email.Enviados);
    }

    [Fact]
    public async Task No_confirma_si_el_monto_pagado_no_coincide()
    {
        var (evento, general, _) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;

        var resultado = await _compras.ConfirmarPagoAsync(orden.Codigo, "MercadoPago", "123", 1m, Url);

        Assert.Equal(ResultadoConfirmacion.MontoNoCoincide, resultado);
        Assert.Equal(EstadoOrden.PendientePago, EstadoDe(orden.Codigo));
        Assert.Empty(_db.Entradas);
    }

    // HU06: un pago rechazado no descuenta aforo (la reserva se libera en el momento, no al vencer)
    [Fact]
    public async Task Un_pago_rechazado_no_descuenta_aforo()
    {
        var (evento, general, _) = CrearEvento(_db);
        var antes = Releer(general.Id).Disponibles;
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 3 })).Orden!;
        Assert.Equal(antes - 3, Releer(general.Id).Disponibles);

        Assert.True(await _compras.RechazarPagoAsync(orden.Codigo));

        Assert.Equal(EstadoOrden.Rechazada, EstadoDe(orden.Codigo));
        Assert.Equal(antes, Releer(general.Id).Disponibles);
        Assert.Equal(0, Releer(general.Id).Vendidas);
        Assert.False(await _compras.RechazarPagoAsync(orden.Codigo)); // no devuelve la reserva dos veces
        Assert.Equal(antes, Releer(general.Id).Disponibles);
    }

    [Fact]
    public async Task Un_rechazo_tardio_no_anula_una_orden_ya_pagada()
    {
        var (evento, general, _) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;
        await _compras.ConfirmarPagoAsync(orden.Codigo, "Simulado", "SIM-1", orden.Total, Url);

        Assert.False(await _compras.RechazarPagoAsync(orden.Codigo));
        Assert.Equal(EstadoOrden.Pagada, EstadoDe(orden.Codigo));
        Assert.Equal(2, Releer(general.Id).Vendidas);
    }

    [Fact]
    public async Task Si_tras_un_rechazo_el_reintento_se_aprueba_se_emiten_las_entradas()
    {
        var (evento, general, _) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;
        await _compras.RechazarPagoAsync(orden.Codigo);

        var resultado = await _compras.ConfirmarPagoAsync(orden.Codigo, "MercadoPago", "123", orden.Total, Url);

        Assert.Equal(EstadoOrden.Pagada, EstadoDe(orden.Codigo));
        Assert.Equal(2, Releer(general.Id).Vendidas);
        Assert.Equal(0, Releer(general.Id).Reservadas);
        Assert.Equal(2, _db.Entradas.Count(e => e.OrdenId == orden.Id));
        Assert.NotEqual(ResultadoConfirmacion.RequiereReembolso, resultado);
    }

    [Fact]
    public async Task La_reserva_dura_diez_minutos()
    {
        var (evento, general, _) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 1 })).Orden!;

        Assert.InRange((orden.FechaExpiracion - DateTime.UtcNow).TotalMinutes, 9.9, 10.01);
    }

    [Fact]
    public async Task Las_reservas_vencidas_devuelven_el_aforo()
    {
        var (evento, general, _) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 4 })).Orden!;
        await _db.Ordenes.Where(o => o.Id == orden.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.FechaExpiracion, DateTime.UtcNow.AddMinutes(-1)));

        var liberadas = await _compras.LiberarExpiradasAsync();

        Assert.Equal(1, liberadas);
        Assert.Equal(EstadoOrden.Expirada, EstadoDe(orden.Codigo));
        Assert.Equal(0, Releer(general.Id).Reservadas);
        Assert.Equal(0, await _compras.LiberarExpiradasAsync());
    }

    [Fact]
    public async Task Pago_tardio_se_acepta_si_aun_queda_aforo()
    {
        var (evento, general, _) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;
        await _db.Ordenes.Where(o => o.Id == orden.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.FechaExpiracion, DateTime.UtcNow.AddMinutes(-1)));
        await _compras.LiberarExpiradasAsync();

        var resultado = await _compras.ConfirmarPagoAsync(orden.Codigo, "MercadoPago", "999", orden.Total, Url);

        Assert.Equal(ResultadoConfirmacion.Confirmada, resultado);
        Assert.Equal(2, Releer(general.Id).Vendidas);
        Assert.Equal(0, Releer(general.Id).Reservadas);
        Assert.Equal(2, _db.Entradas.Count());
    }

    [Fact]
    public async Task Pago_tardio_sin_aforo_queda_para_reembolso_sin_sobrevender()
    {
        var (evento, general, _) = CrearEvento(_db, capacidadGeneral: 2);
        var tardia = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;
        await _db.Ordenes.Where(o => o.Id == tardia.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.FechaExpiracion, DateTime.UtcNow.AddMinutes(-1)));
        await _compras.LiberarExpiradasAsync();

        // Otra persona compra todo el aforo liberado
        var otra = (await _compras.CrearOrdenAsync("beto", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;
        await _compras.ConfirmarPagoAsync(otra.Codigo, "Simulado", "SIM", otra.Total, Url);

        var resultado = await _compras.ConfirmarPagoAsync(tardia.Codigo, "MercadoPago", "777", tardia.Total, Url);

        Assert.Equal(ResultadoConfirmacion.RequiereReembolso, resultado);
        Assert.Equal(EstadoOrden.RequiereReembolso, EstadoDe(tardia.Codigo));
        Assert.Equal(2, Releer(general.Id).Vendidas);
        Assert.Equal(2, _db.Entradas.Count());
    }

    [Fact]
    public async Task Una_nueva_orden_cancela_la_pendiente_del_mismo_usuario_y_evento()
    {
        var (evento, general, _) = CrearEvento(_db);
        var primera = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 4 })).Orden!;

        var segunda = await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 3 });

        Assert.Null(segunda.Error);
        Assert.Equal(EstadoOrden.Cancelada, EstadoDe(primera.Codigo));
        Assert.Equal(3, Releer(general.Id).Reservadas);
    }

    [Fact]
    public async Task Solo_el_dueno_puede_cancelar_su_orden()
    {
        var (evento, general, _) = CrearEvento(_db);
        var orden = (await _compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 2 })).Orden!;

        Assert.False(await _compras.CancelarAsync(orden.Codigo, "beto"));
        Assert.True(await _compras.CancelarAsync(orden.Codigo, "ana"));
        Assert.Equal(0, Releer(general.Id).Reservadas);
        Assert.False(await _compras.CancelarAsync(orden.Codigo, "ana"));
    }
}

// =============================
// 🏁 RNF05: COMPRAS SIMULTÁNEAS SIN SOBREVENTA
// =============================
public class ConcurrenciaAforoTests : IDisposable
{
    private readonly string _archivo = Path.Combine(Path.GetTempPath(), $"activafest-concurrencia-{Guid.NewGuid():N}.db");

    private string Cadena => $"Data Source={_archivo};Default Timeout=60;Pooling=False";

    private ApplicationDbContext NuevoContexto() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(Cadena).Options);

    public void Dispose()
    {
        foreach (var f in new[] { _archivo, _archivo + "-wal", _archivo + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    // Compras simultáneas de "compradores" entradas cuando quedan "capacidad"; con o sin el bloqueo compartido
    // (sin él se simulan varias instancias de la app sin Redis: protege solo el RowVersion + UPDATE condicional)
    private async Task<(int Exitos, TipoEntrada Zona, int Entradas)> CompraSimultaneaAsync(int compradores, int capacidad, bool bloqueoCompartido)
    {
        int generalId, eventoId;
        var usuarios = Enumerable.Range(1, compradores).Select(i => $"u{i}").ToArray();

        using (var db = NuevoContexto())
        {
            db.Database.EnsureCreated();
            CompraServiceTests.CrearUsuarios(db, usuarios);
            var (evento, general, _) = CompraServiceTests.CrearEvento(db, capacidadGeneral: capacidad);
            generalId = general.Id;
            eventoId = evento.Id;
        }

        var bloqueo = new BloqueoEnMemoria();
        var inicio = new TaskCompletionSource();
        var tareas = usuarios.Select(u => Task.Run(async () =>
        {
            await inicio.Task; // todas arrancan a la vez
            using var db = NuevoContexto();
            var compras = Fabrica.Compras(db, new EmailFalso(), bloqueo: bloqueoCompartido ? bloqueo : null);
            var r = await compras.CrearOrdenAsync(u, eventoId, new Dictionary<int, int> { [generalId] = 1 });
            if (r.Orden != null)
                await compras.ConfirmarPagoAsync(r.Orden.Codigo, "Simulado", "SIM-" + u, r.Orden.Total, "https://x");
            return r.Error == null;
        })).ToList();

        inicio.SetResult();
        var exitos = (await Task.WhenAll(tareas)).Count(ok => ok);

        using var verificacion = NuevoContexto();
        return (exitos, verificacion.TiposEntrada.AsNoTracking().Single(t => t.Id == generalId), verificacion.Entradas.Count());
    }

    // Criterio de HU11: dos compras simultáneas de la última entrada producen una sola venta
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dos_compras_simultaneas_de_la_ultima_entrada_producen_una_sola_venta(bool bloqueoCompartido)
    {
        var (exitos, zona, entradas) = await CompraSimultaneaAsync(compradores: 2, capacidad: 1, bloqueoCompartido);

        Assert.Equal(1, exitos);
        Assert.Equal(1, zona.Vendidas);
        Assert.Equal(1, entradas);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Veinte_compras_simultaneas_por_cinco_entradas_venden_exactamente_cinco(bool bloqueoCompartido)
    {
        var (exitos, zona, entradas) = await CompraSimultaneaAsync(compradores: 20, capacidad: 5, bloqueoCompartido);

        Assert.Equal(5, exitos);
        Assert.Equal(5, zona.Vendidas);
        Assert.Equal(0, zona.Reservadas);
        Assert.Equal(5, entradas);
        Assert.True(zona.RowVersion >= 10); // cada reserva y cada pago cambian la versión
    }
}
