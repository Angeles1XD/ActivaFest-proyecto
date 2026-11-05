using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Acceso;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Eventos;
using ActivaFest.Services.Imagenes;
using ActivaFest.Services.Panel;

namespace ActivaFest.Tests;

// =============================
// Base común: evento con ventas reales hechas con CompraService
// =============================
public abstract class BaseSprint4 : IDisposable
{
    protected const string Url = "https://activafest.test/x";

    protected readonly SqliteConnection Conexion;
    protected readonly ApplicationDbContext Db;
    protected readonly EmailFalso Email = new();

    protected BaseSprint4()
    {
        Conexion = new SqliteConnection("Data Source=:memory:");
        Conexion.Open();
        Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(Conexion).Options);
        Db.Database.EnsureCreated();
        CompraServiceTests.CrearUsuarios(Db, "org", "otroOrg", "ana", "beto", "porteria");
    }

    public void Dispose()
    {
        Db.Dispose();
        Conexion.Dispose();
    }

    protected (Evento Evento, TipoEntrada General, TipoEntrada Vip) EventoDe(string organizador, double horasHastaInicio = 48)
    {
        var (evento, general, vip) = CompraServiceTests.CrearEvento(Db);
        evento.OrganizadorId = organizador;
        evento.Fecha = DateTime.Now.AddHours(horasHastaInicio);
        Db.SaveChanges();
        return (evento, general, vip);
    }

    protected async Task<List<Entrada>> ComprarAsync(string usuario, int eventoId, Dictionary<int, int> cantidades)
    {
        var compras = Fabrica.Compras(Db, Email);
        var orden = (await compras.CrearOrdenAsync(usuario, eventoId, cantidades)).Orden!;
        await compras.ConfirmarPagoAsync(orden.Codigo, "Simulado", "SIM", orden.Total, Url);

        // Como en la app real (un DbContext por petición): lo siguiente no ve entidades rastreadas desactualizadas
        Db.ChangeTracker.Clear();
        return Db.Entradas.AsNoTracking().Where(e => e.OrdenId == orden.Id).ToList();
    }
}

// =============================
// 🚪 HU08: VALIDACIÓN EN PUERTA
// =============================
public class AccesoServiceTests : BaseSprint4
{
    private readonly AccesoService _acceso;
    private readonly ActivaFest.Services.Entradas.QrEntradaService _qr = Fabrica.Qr();

    public AccesoServiceTests()
    {
        _acceso = new AccesoService(Db, _qr, NullLogger<AccesoService>.Instance);
    }

    private Task<ResultadoValidacion> Escanear(int eventoId, Entrada entrada, DateTime? ahora = null) =>
        _acceso.ValidarAsync(eventoId, _qr.CrearContenido(entrada.Codigo), "porteria", ahora ?? DateTime.Now);

    [Fact]
    public async Task Primer_escaneo_deja_pasar_y_marca_la_entrada_como_usada()
    {
        var (evento, general, _) = EventoDe("org", horasHastaInicio: 1);
        var entrada = (await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 }))[0];

        var r = await Escanear(evento.Id, entrada);

        Assert.Equal(TipoValidacion.Valida, r.Tipo);
        Assert.True(r.PermiteIngreso);
        Assert.Equal("General", r.Zona);
        Assert.Equal("ana", r.Titular);
        Assert.Equal((1, 1), (r.Ingresados, r.Vendidas));

        var guardada = Db.Entradas.AsNoTracking().Single(e => e.Id == entrada.Id);
        Assert.Equal(EstadoEntrada.Usada, guardada.Estado);
        Assert.Equal("porteria", guardada.ValidadoPorId);
        Assert.NotNull(guardada.FechaUso);
    }

    [Fact]
    public async Task Un_qr_copiado_se_rechaza_en_el_segundo_escaneo()
    {
        var (evento, general, _) = EventoDe("org", horasHastaInicio: 1);
        var entrada = (await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 }))[0];

        await Escanear(evento.Id, entrada);
        var clon = await Escanear(evento.Id, entrada);

        Assert.Equal(TipoValidacion.YaUsada, clon.Tipo);
        Assert.False(clon.PermiteIngreso);
        Assert.NotNull(clon.FechaUso);
    }

    [Fact]
    public async Task Rechaza_qr_alterado_o_ajeno_a_activafest()
    {
        var (evento, general, _) = EventoDe("org", horasHastaInicio: 1);
        var entrada = (await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 }))[0];
        var original = _qr.CrearContenido(entrada.Codigo);
        var alterado = original[..^3] + (original[^3] == 'A' ? "B" : "A") + original[^2..];

        Assert.Equal(TipoValidacion.Invalida, (await _acceso.ValidarAsync(evento.Id, alterado, "porteria", DateTime.Now)).Tipo);
        Assert.Equal(TipoValidacion.Invalida, (await _acceso.ValidarAsync(evento.Id, "https://otra-web.com", "porteria", DateTime.Now)).Tipo);
        Assert.Equal(EstadoEntrada.Activa, Db.Entradas.AsNoTracking().Single(e => e.Id == entrada.Id).Estado);
    }

    [Fact]
    public async Task Rechaza_entradas_de_otro_evento()
    {
        var (evento, general, _) = EventoDe("org", horasHastaInicio: 1);
        var (otro, generalOtro, _) = EventoDe("org", horasHastaInicio: 1);
        var entradaOtro = (await ComprarAsync("ana", otro.Id, new() { [generalOtro.Id] = 1 }))[0];

        var r = await Escanear(evento.Id, entradaOtro);

        Assert.Equal(TipoValidacion.OtroEvento, r.Tipo);
        Assert.Equal(EstadoEntrada.Activa, Db.Entradas.AsNoTracking().Single(e => e.Id == entradaOtro.Id).Estado);
    }

    [Fact]
    public async Task Rechaza_entradas_anuladas()
    {
        var (evento, general, _) = EventoDe("org", horasHastaInicio: 1);
        var entrada = (await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 }))[0];
        await Db.Entradas.Where(e => e.Id == entrada.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Estado, EstadoEntrada.Anulada));

        Assert.Equal(TipoValidacion.Anulada, (await Escanear(evento.Id, entrada)).Tipo);
    }

    [Fact]
    public async Task Respeta_el_horario_de_ingreso()
    {
        var (evento, general, _) = EventoDe("org", horasHastaInicio: 48);
        var entrada = (await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 }))[0];
        var inicio = Db.Eventos.AsNoTracking().Single(e => e.Id == evento.Id).Fecha;

        Assert.Equal(TipoValidacion.Temprano, (await Escanear(evento.Id, entrada, inicio.AddHours(-7))).Tipo);
        Assert.Equal(TipoValidacion.Vencida, (await Escanear(evento.Id, entrada, inicio.AddHours(13))).Tipo);
        Assert.Equal(TipoValidacion.Valida, (await Escanear(evento.Id, entrada, inicio.AddHours(-5))).Tipo);
    }

    [Fact]
    public async Task Solo_el_personal_asignado_al_evento_puede_validar_sus_entradas()
    {
        var (propio, _, _) = EventoDe("org");
        var (ajeno, _, _) = EventoDe("otroOrg");

        // Organizador: solo sus eventos
        Assert.True(await _acceso.PuedeControlarAsync("org", false, propio.Id));
        Assert.False(await _acceso.PuedeControlarAsync("org", false, ajeno.Id));

        // Personal de control: nada hasta que el administrador lo asigna (HU19)
        Assert.False(await _acceso.PuedeControlarAsync("porteria", false, ajeno.Id));
        Db.AsignacionesAcceso.Add(new AsignacionAcceso { UsuarioId = "porteria", EventoId = ajeno.Id });
        Db.SaveChanges();
        Assert.True(await _acceso.PuedeControlarAsync("porteria", false, ajeno.Id));
        Assert.False(await _acceso.PuedeControlarAsync("porteria", false, propio.Id));
        Assert.Equal(new[] { ajeno.Id }, (await _acceso.EventosControlablesAsync("porteria", false)).Select(e => e.Id));

        // Administrador: todos
        Assert.True(await _acceso.PuedeControlarAsync("admin", true, propio.Id));
        Assert.True(await _acceso.PuedeControlarAsync("admin", true, ajeno.Id));
    }
}

public class EscaneoSimultaneoTests : IDisposable
{
    private readonly string _archivo = Path.Combine(Path.GetTempPath(), $"activafest-escaneo-{Guid.NewGuid():N}.db");

    private ApplicationDbContext Contexto() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={_archivo};Default Timeout=60;Pooling=False").Options);

    public void Dispose()
    {
        foreach (var f in new[] { _archivo, _archivo + "-wal", _archivo + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task El_mismo_qr_escaneado_en_dos_puertas_a_la_vez_deja_pasar_a_una_sola_persona()
    {
        string contenido;
        int eventoId;
        var qr = Fabrica.Qr();

        using (var db = Contexto())
        {
            db.Database.EnsureCreated();
            CompraServiceTests.CrearUsuarios(db, "ana", "p1", "p2");
            var (evento, general, _) = CompraServiceTests.CrearEvento(db);
            evento.Fecha = DateTime.Now.AddHours(1);
            db.SaveChanges();
            eventoId = evento.Id;

            var compras = Fabrica.Compras(db, new EmailFalso());
            var orden = (await compras.CrearOrdenAsync("ana", evento.Id, new Dictionary<int, int> { [general.Id] = 1 })).Orden!;
            await compras.ConfirmarPagoAsync(orden.Codigo, "Simulado", "SIM", orden.Total, "https://x");
            contenido = qr.CrearContenido(db.Entradas.Single().Codigo);
        }

        var inicio = new TaskCompletionSource();
        var puertas = Enumerable.Range(1, 8).Select(i => Task.Run(async () =>
        {
            await inicio.Task;
            using var db = Contexto();
            var acceso = new AccesoService(db, qr, NullLogger<AccesoService>.Instance);
            return (await acceso.ValidarAsync(eventoId, contenido, $"p{1 + i % 2}", DateTime.Now)).Tipo;
        })).ToList();

        inicio.SetResult();
        var resultados = await Task.WhenAll(puertas);

        Assert.Equal(1, resultados.Count(t => t == TipoValidacion.Valida));
        Assert.Equal(7, resultados.Count(t => t == TipoValidacion.YaUsada));
    }
}

// =============================
// ✏️ HU09: EDICIÓN DE EVENTOS
// =============================
public class EventoEdicionServiceTests : BaseSprint4
{
    private class AlmacenamientoFalso : IAlmacenamientoImagenes
    {
        public List<string> Eliminadas { get; } = new();
        public Task<string> GuardarAsync(IFormFile archivo, string carpeta, CancellationToken ct = default) =>
            Task.FromResult($"/uploads/{carpeta}/{Guid.NewGuid():N}.jpg");
        public Task EliminarAsync(string url, CancellationToken ct = default)
        {
            Eliminadas.Add(url);
            return Task.CompletedTask;
        }
    }

    private readonly AlmacenamientoFalso _imagenes = new();
    private readonly EventoEdicionService _edicion;

    public EventoEdicionServiceTests()
    {
        var catalogo = new CatalogoService(Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), NullLogger<CatalogoService>.Instance);
        _edicion = new EventoEdicionService(Db, _imagenes, catalogo, Email, NullLogger<EventoEdicionService>.Instance);
    }

    private (Evento Evento, TipoEntrada General, TipoEntrada Vip) EventoConFotos(EstadoEvento estado = EstadoEvento.Aprobado)
    {
        var (evento, general, vip) = EventoDe("org");
        evento.Estado = estado;
        for (var i = 0; i < 3; i++)
            evento.Imagenes.Add(new EventoImagen { Ruta = $"/uploads/eventos/f{i}.jpg", Orden = i });
        Db.SaveChanges();
        return (evento, general, vip);
    }

    // Formulario equivalente al que envía la vista de edición, con los datos actuales del evento
    private EventoFormViewModel Formulario(int eventoId)
    {
        var e = Db.Eventos.AsNoTracking().Include(x => x.TiposEntrada).Include(x => x.Imagenes).Single(x => x.Id == eventoId);
        return new EventoFormViewModel
        {
            Id = e.Id,
            Titulo = e.Titulo,
            Descripcion = "Descripción suficientemente larga del evento",
            CategoriaId = e.CategoriaId,
            Fecha = e.Fecha,
            Ubicacion = e.Ubicacion,
            Latitud = -12.05,
            Longitud = -77.04,
            TiposEntrada = e.TiposEntrada.Select(t => new TipoEntradaInput { Id = t.Id, Nombre = t.Nombre, Precio = t.Precio, Capacidad = t.Capacidad }).ToList(),
            ImagenesActualesIds = e.Imagenes.Select(i => i.Id).ToList()
        };
    }

    private Evento Releer(int id) => Db.Eventos.AsNoTracking().Include(e => e.TiposEntrada).Include(e => e.Imagenes).Single(e => e.Id == id);

    [Fact]
    public async Task No_permite_bajar_la_capacidad_por_debajo_de_lo_vendido()
    {
        var (evento, general, _) = EventoConFotos();
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 3 });

        var form = Formulario(evento.Id);
        form.TiposEntrada.Single(t => t.Id == general.Id).Capacidad = 2;
        var r = await _edicion.ActualizarAsync(evento.Id, form, Url);

        Assert.False(r.Exito);
        Assert.Contains(r.Errores, e => e.Contains("General"));
        Assert.Equal(5, Releer(evento.Id).TiposEntrada.Single(t => t.Id == general.Id).Capacidad);
    }

    [Fact]
    public async Task No_permite_eliminar_una_zona_con_compras_pero_si_una_sin_compras()
    {
        var (evento, general, vip) = EventoConFotos();
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 });

        var sinGeneral = Formulario(evento.Id);
        sinGeneral.TiposEntrada.RemoveAll(t => t.Id == general.Id);
        Assert.False((await _edicion.ActualizarAsync(evento.Id, sinGeneral, Url)).Exito);

        var sinVip = Formulario(evento.Id);
        sinVip.TiposEntrada.RemoveAll(t => t.Id == vip.Id);
        Assert.True((await _edicion.ActualizarAsync(evento.Id, sinVip, Url)).Exito);
        Assert.Single(Releer(evento.Id).TiposEntrada);
    }

    [Fact]
    public async Task Cambiar_el_precio_no_altera_las_compras_ni_los_contadores_de_venta()
    {
        var (evento, general, _) = EventoConFotos();
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 2 });

        var form = Formulario(evento.Id);
        form.TiposEntrada.Single(t => t.Id == general.Id).Precio = 999;
        form.TiposEntrada.Single(t => t.Id == general.Id).Capacidad = 8;
        Assert.True((await _edicion.ActualizarAsync(evento.Id, form, Url)).Exito);

        var zona = Releer(evento.Id).TiposEntrada.Single(t => t.Id == general.Id);
        Assert.Equal(999, zona.Precio);
        Assert.Equal(8, zona.Capacidad);
        Assert.Equal(2, zona.Vendidas);
        Assert.Equal(50, Db.OrdenItems.AsNoTracking().Single().PrecioUnitario);
        Assert.Equal(10, Releer(evento.Id).Aforo); // 8 General + 2 VIP
    }

    [Fact]
    public async Task Cambiar_la_fecha_de_un_evento_publicado_avisa_a_los_compradores()
    {
        var (evento, general, vip) = EventoConFotos();
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 2 });
        await ComprarAsync("beto", evento.Id, new() { [vip.Id] = 1 });
        Email.Enviados.Clear();

        var form = Formulario(evento.Id);
        form.Fecha = form.Fecha!.Value.AddDays(7);
        var r = await _edicion.ActualizarAsync(evento.Id, form, Url);

        Assert.True(r.Exito);
        Assert.Equal(2, r.CompradoresNotificados);
        Assert.Equal(new[] { "ana@test.pe", "beto@test.pe" }, Email.Enviados.Select(c => c.Para).OrderBy(c => c));
        Assert.Equal(EstadoEvento.Aprobado, Releer(evento.Id).Estado);
    }

    [Fact]
    public async Task Sin_cambios_de_fecha_ni_lugar_no_se_envian_avisos()
    {
        var (evento, general, _) = EventoConFotos();
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 });
        Email.Enviados.Clear();

        var form = Formulario(evento.Id);
        form.Titulo = "Nuevo título";
        var r = await _edicion.ActualizarAsync(evento.Id, form, Url);

        Assert.True(r.Exito);
        Assert.Equal(0, r.CompradoresNotificados);
        Assert.Empty(Email.Enviados);
    }

    [Fact]
    public async Task Un_evento_rechazado_editado_vuelve_a_revision()
    {
        var (evento, _, _) = EventoConFotos(EstadoEvento.Rechazado);

        Assert.True((await _edicion.ActualizarAsync(evento.Id, Formulario(evento.Id), Url)).Exito);

        Assert.Equal(EstadoEvento.Pendiente, Releer(evento.Id).Estado);
    }

    [Fact]
    public async Task Exige_al_menos_tres_fotos_al_quitar_imagenes_y_borra_los_archivos_quitados()
    {
        var (evento, _, _) = EventoConFotos();
        var actual = Releer(evento.Id).Imagenes.OrderBy(i => i.Orden).ToList();

        var quitaUna = Formulario(evento.Id);
        quitaUna.ImagenesEliminar.Add(actual[0].Id);
        Assert.False((await _edicion.ActualizarAsync(evento.Id, quitaUna, Url)).Exito);
        Assert.Equal(3, Releer(evento.Id).Imagenes.Count);

        var reemplaza = Formulario(evento.Id);
        reemplaza.ImagenesEliminar.Add(actual[0].Id);
        reemplaza.Fotos.Add(new FormFile(new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }), 0, 6, "Fotos", "nueva.jpg"));
        Assert.True((await _edicion.ActualizarAsync(evento.Id, reemplaza, Url)).Exito);

        Assert.Equal(3, Releer(evento.Id).Imagenes.Count);
        Assert.Contains(actual[0].Ruta, _imagenes.Eliminadas);
    }
}

// =============================
// 📊 HU13 / OE4: PANEL DEL ORGANIZADOR
// =============================
public class PanelServiceTests : BaseSprint4
{
    private readonly PanelService _panel;

    public PanelServiceTests()
    {
        _panel = new PanelService(Db);
    }

    [Fact]
    public async Task Las_cifras_del_panel_coinciden_con_las_transacciones()
    {
        var (evento, general, vip) = EventoDe("org", horasHastaInicio: 1);
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 2, [vip.Id] = 1 }); // 50*2 + 120.5
        var entradasBeto = await ComprarAsync("beto", evento.Id, new() { [general.Id] = 1 }); // 50
        var acceso = new AccesoService(Db, Fabrica.Qr(), NullLogger<AccesoService>.Instance);
        await acceso.ValidarAsync(evento.Id, Fabrica.Qr().CrearContenido(entradasBeto[0].Codigo), "porteria", DateTime.Now);
        await Db.Eventos.Where(e => e.Id == evento.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Visitas, 20));

        var panel = (await _panel.DetalleAsync(evento.Id, "org", esAdmin: false))!;

        Assert.Equal(4, panel.Vendidas);
        Assert.Equal(270.5m, panel.Ingresos);
        Assert.Equal(2, panel.OrdenesPagadas);
        Assert.Equal(1, panel.Ingresados);
        Assert.Equal(7, panel.Aforo);
        Assert.Equal(3, panel.Disponibles);
        Assert.Equal(0.1, panel.Conversion, 3);
        Assert.True(panel.Conciliado);

        var zonaGeneral = panel.Zonas.Single(z => z.Nombre == "General");
        Assert.Equal(3, zonaGeneral.Vendidas);
        Assert.Equal(150m, zonaGeneral.Ingresos);
        Assert.Equal(4, panel.VentasPorDia.Sum(d => d.Entradas));
        Assert.Equal(270.5m, panel.VentasPorDia.Sum(d => d.Ingresos));

        var resumen = Assert.Single(await _panel.ResumenAsync("org", esAdmin: false));
        Assert.Equal(4, resumen.Vendidas);
        Assert.Equal(270.5m, resumen.Ingresos);
    }

    [Fact]
    public async Task Detecta_discrepancias_entre_el_aforo_y_las_entradas_emitidas()
    {
        var (evento, general, _) = EventoDe("org");
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 2 });
        await Db.TiposEntrada.Where(t => t.Id == general.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Vendidas, 5));

        var panel = (await _panel.DetalleAsync(evento.Id, "org", esAdmin: false))!;

        Assert.False(panel.Conciliado);
        Assert.Equal(2, panel.Vendidas);
    }

    [Fact]
    public async Task Un_organizador_no_ve_el_panel_de_eventos_ajenos()
    {
        var (ajeno, _, _) = EventoDe("otroOrg");

        Assert.Null(await _panel.DetalleAsync(ajeno.Id, "org", esAdmin: false));
        Assert.NotNull(await _panel.DetalleAsync(ajeno.Id, "admin", esAdmin: true));
        Assert.Empty(await _panel.ResumenAsync("org", esAdmin: false));
    }

    [Fact]
    public void Ventas_por_dia_es_una_serie_continua_con_ceros()
    {
        var hoy = new DateTime(2026, 10, 20);
        var serie = PanelService.VentasPorDia(
            new List<DateTime> { hoy.AddDays(-3).AddHours(10), hoy.AddDays(-3).AddHours(15), hoy.AddHours(9) },
            new List<(DateTime, decimal)> { (hoy.AddDays(-3), 100m), (hoy, 40m) },
            hoy);

        Assert.Equal(7, serie.Count);
        Assert.Equal(hoy, serie[^1].Dia);
        Assert.Equal(2, serie.Single(d => d.Dia == hoy.AddDays(-3)).Entradas);
        Assert.Equal(0, serie.Single(d => d.Dia == hoy.AddDays(-1)).Entradas);
        Assert.Equal(140m, serie.Sum(d => d.Ingresos));
    }
}

// =============================
// ☁️ HU09: CLOUDINARY
// =============================
public class CloudinaryTests
{
    [Fact]
    public void Firma_igual_que_el_ejemplo_de_la_documentacion_de_cloudinary()
    {
        var parametros = new Dictionary<string, string>
        {
            ["timestamp"] = "1315060510",
            ["public_id"] = "sample_image",
            ["eager"] = "w_400,h_300,c_pad|w_260,h_200,c_crop"
        };

        Assert.Equal("bfd09f95f331f558cbd1320e67aa8d488770583e", CloudinaryAlmacenamiento.Firmar(parametros, "abcd"));
    }

    [Theory]
    [InlineData("https://res.cloudinary.com/demo/image/upload/v1712345/activafest/eventos/abc123.jpg", "activafest/eventos/abc123")]
    [InlineData("https://res.cloudinary.com/demo/image/upload/activafest/eventos/abc123.png", "activafest/eventos/abc123")]
    [InlineData("https://res.cloudinary.com/otra-cuenta/image/upload/v1/x/y.jpg", null)]
    [InlineData("/uploads/eventos/local.jpg", null)]
    public void Extrae_el_public_id_solo_de_imagenes_de_la_propia_cuenta(string url, string? esperado)
    {
        Assert.Equal(esperado, CloudinaryAlmacenamiento.ExtraerPublicId(url, "demo"));
    }
}
