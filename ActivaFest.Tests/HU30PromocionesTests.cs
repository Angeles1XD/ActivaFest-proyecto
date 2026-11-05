using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Pagos;
using ActivaFest.Services.Promociones;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🎁 HU30: PROMOCIONES Y CUPONES (pruebas de los servicios)
// =============================
public class HU30PromocionesTests : IDisposable
{
    private const string Url = "https://activafest.test";
    private readonly SqliteConnection _conexion;
    private readonly ApplicationDbContext _db;
    private readonly EmailFalso _email = new();
    private readonly PromocionService _promociones;
    private readonly CompraService _compras;
    private readonly Evento _evento;
    private readonly TipoEntrada _general;

    public HU30PromocionesTests()
    {
        _conexion = new SqliteConnection("Data Source=:memory:");
        _conexion.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conexion).Options);
        _db.Database.EnsureCreated();
        CompraServiceTests.CrearUsuarios(_db, "admin", "ana", "beto", "carla");
        _db.Users.Where(u => u.Id == "ana" || u.Id == "beto").ExecuteUpdate(s => s.SetProperty(u => u.AceptaPromociones, true));

        _promociones = new PromocionService(_db, _email, NullLogger<PromocionService>.Instance);
        _compras = Fabrica.Compras(_db, _email);
        (_evento, _general, _) = CompraServiceTests.CrearEvento(_db, capacidadGeneral: 20); // General S/ 50
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexion.Dispose();
    }

    private static PromocionInput Input(params string[] clientes) => new()
    {
        Nombre = "15 % VIP", TipoDescuento = TipoDescuento.Porcentaje, Valor = 15,
        FechaInicio = DateTime.Today, FechaFin = DateTime.Today.AddDays(15), CriterioSegmento = "segmento VIP",
        Clientes = clientes.ToList()
    };

    private async Task<Promocion> CrearAsync(PromocionInput input)
    {
        var r = await _promociones.CrearAsync(input, "admin", Url);
        Assert.Null(r.Error);
        return r.Promocion!;
    }

    private string CodigoDe(Promocion p, string usuario) => _db.Cupones.AsNoTracking().Single(c => c.PromocionId == p.Id && c.UsuarioId == usuario).Codigo;
    private Cupon Cupon(string codigo) => _db.Cupones.AsNoTracking().Single(c => c.Codigo == codigo);
    private int Usos(Promocion p) => _db.Promociones.AsNoTracking().Single(x => x.Id == p.Id).Usos;

    private Task<ResultadoOrden> ComprarAsync(string usuario, int cantidad, string? cupon, int? eventoId = null) =>
        _compras.CrearOrdenAsync(usuario, eventoId ?? _evento.Id, new Dictionary<int, int> { [_general.Id] = cantidad }, cupon);

    [Fact]
    public async Task Genera_un_cupon_personal_y_lo_envia_solo_a_quienes_aceptaron_promociones()
    {
        var r = await _promociones.CrearAsync(Input("ana", "beto", "carla"), "admin", Url);

        Assert.Null(r.Error);
        Assert.Equal((2, 1), (r.Enviados, r.Omitidos));

        var cupones = _db.Cupones.AsNoTracking().Where(c => c.PromocionId == r.Promocion!.Id).ToList();
        Assert.Equal(new[] { "ana", "beto" }, cupones.Select(c => c.UsuarioId).OrderBy(u => u));
        Assert.All(cupones, c => Assert.Matches("^AF-[A-HJ-NP-Z2-9]{8}$", c.Codigo));
        Assert.Equal(2, cupones.Select(c => c.Codigo).Distinct().Count());
        Assert.All(cupones, c => Assert.False(c.Usado));

        Assert.Equal(new[] { "ana@test.pe", "beto@test.pe" }, _email.Enviados.Select(e => e.Para).OrderBy(p => p));
        Assert.DoesNotContain(_email.Enviados, e => e.Para == "carla@test.pe");

        var p = _db.Promociones.AsNoTracking().Single();
        Assert.Equal((TipoDescuento.Porcentaje, 15m, "segmento VIP", "admin"), (p.TipoDescuento, p.Valor, p.CriterioSegmento, p.CreadaPorId));
    }

    [Fact]
    public async Task Sin_clientes_con_consentimiento_no_se_crea_la_promocion()
    {
        var r = await _promociones.CrearAsync(Input("carla"), "admin", Url);

        Assert.Equal("Ninguno de los clientes seleccionados aceptó recibir promociones.", r.Error);
        Assert.Empty(_db.Promociones);
        Assert.Empty(_email.Enviados);
    }

    [Theory]
    [InlineData(TipoDescuento.Porcentaje, 0, 0, 15, null, "El valor del descuento debe ser mayor que cero.")]
    [InlineData(TipoDescuento.Porcentaje, 120, 0, 15, null, "El porcentaje no puede superar el 100 %.")]
    [InlineData(TipoDescuento.Monto, 20, 5, 2, null, "La fecha de fin debe ser igual o posterior a la de inicio.")]
    [InlineData(TipoDescuento.Monto, 20, -10, -1, null, "La vigencia ya terminó: elige una fecha de fin futura.")]
    [InlineData(TipoDescuento.Monto, 20, 0, 15, 0, "El límite de usos debe ser al menos 1.")]
    public void La_promocion_define_tipo_valor_vigencia_y_limite_validos(TipoDescuento tipo, int valor, int inicio, int fin, int? limite, string error)
    {
        var input = Input("ana");
        (input.TipoDescuento, input.Valor, input.FechaInicio, input.FechaFin, input.LimiteUsos) =
            (tipo, valor, DateTime.Today.AddDays(inicio), DateTime.Today.AddDays(fin), limite);

        Assert.Equal(error, PromocionService.Validar(input, DateTime.Today));
        Assert.Null(PromocionService.Validar(Input("ana"), DateTime.Today));
        Assert.Equal("Selecciona al menos un cliente en el filtro de clientes.", PromocionService.Validar(Input(), DateTime.Today));
    }

    [Fact]
    public void El_descuento_es_porcentaje_o_monto_y_nunca_supera_el_subtotal()
    {
        Assert.Equal(25.58m, new Promocion { TipoDescuento = TipoDescuento.Porcentaje, Valor = 15 }.DescuentoPara(170.5m));
        Assert.Equal(20m, new Promocion { TipoDescuento = TipoDescuento.Monto, Valor = 20 }.DescuentoPara(100m));
        Assert.Equal(15m, new Promocion { TipoDescuento = TipoDescuento.Monto, Valor = 20 }.DescuentoPara(15m));
    }

    [Fact]
    public async Task El_cupon_se_aplica_al_reservar_y_queda_usado_al_pagar()
    {
        var p = await CrearAsync(Input("ana"));
        var codigo = CodigoDe(p, "ana");

        var r = await ComprarAsync("ana", 2, $"  {codigo.ToLowerInvariant()} ");   // se normaliza: espacios y minúsculas
        Assert.Null(r.Error);
        var orden = r.Orden!;
        Assert.Equal((15m, 85m), (orden.Descuento, orden.Total));
        Assert.Equal(Cupon(codigo).Id, orden.CuponId);
        Assert.Equal((orden.Id, false), (Cupon(codigo).OrdenId, Cupon(codigo).Usado));
        Assert.Equal(1, Usos(p));

        // El monto pagado debe coincidir con el total con descuento
        Assert.Equal(ResultadoConfirmacion.MontoNoCoincide, await _compras.ConfirmarPagoAsync(orden.Codigo, "Simulado", "SIM-1", 100m, Url));
        Assert.Equal(ResultadoConfirmacion.Confirmada, await _compras.ConfirmarPagoAsync(orden.Codigo, "Simulado", "SIM-1", 85m, Url));

        var usado = Cupon(codigo);
        Assert.True(usado.Usado);
        Assert.Equal(orden.Id, usado.OrdenId);
        Assert.NotNull(usado.FechaUso);
        var pagada = _db.Ordenes.AsNoTracking().Single(o => o.Id == orden.Id);
        Assert.Equal(6.8m, pagada.Comision); // la comisión se calcula sobre lo cobrado

        // Un solo uso
        var otra = await ComprarAsync("ana", 1, codigo);
        Assert.Equal("Este cupón ya fue usado.", otra.Error);
    }

    [Fact]
    public async Task El_cupon_es_personal()
    {
        var p = await CrearAsync(Input("ana", "beto"));
        var deAna = CodigoDe(p, "ana");

        Assert.Equal("El cupón no es válido para tu cuenta.", (await ComprarAsync("beto", 1, deAna)).Error);
        Assert.Equal("El cupón no es válido para tu cuenta.", (await ComprarAsync("carla", 1, deAna)).Error);
        Assert.Equal("El cupón no es válido para tu cuenta.", (await ComprarAsync("ana", 1, "AF-NOEXISTE")).Error);
        Assert.Equal(0, Usos(p));
        Assert.Equal(0, _db.TiposEntrada.AsNoTracking().Single(t => t.Id == _general.Id).Reservadas); // no se reservó nada
    }

    [Fact]
    public async Task Respeta_la_vigencia_y_la_desactivacion()
    {
        var futura = Input("ana");
        (futura.FechaInicio, futura.FechaFin) = (DateTime.Today.AddDays(3), DateTime.Today.AddDays(10));
        var p1 = await CrearAsync(futura);
        Assert.StartsWith("Este cupón se puede usar desde el", (await ComprarAsync("ana", 1, CodigoDe(p1, "ana"))).Error);

        var p2 = await CrearAsync(Input("ana"));
        _db.Promociones.Where(p => p.Id == p2.Id).ExecuteUpdate(s => s
            .SetProperty(p => p.FechaInicio, DateTime.Today.AddDays(-10)).SetProperty(p => p.FechaFin, DateTime.Today.AddDays(-1)));
        Assert.StartsWith("Este cupón venció el", (await ComprarAsync("ana", 1, CodigoDe(p2, "ana"))).Error);

        var p3 = await CrearAsync(Input("ana"));
        Assert.True(await _promociones.DesactivarAsync(p3.Id));
        Assert.Equal("Esta promoción ya no está disponible.", (await ComprarAsync("ana", 1, CodigoDe(p3, "ana"))).Error);
        Assert.Empty(await _promociones.CuponesDisponiblesAsync("ana"));
    }

    [Fact]
    public async Task Si_el_pago_se_rechaza_el_cupon_vuelve_a_estar_disponible()
    {
        var p = await CrearAsync(Input("ana"));
        var codigo = CodigoDe(p, "ana");

        var orden = (await ComprarAsync("ana", 1, codigo)).Orden!;
        Assert.True(await _compras.RechazarPagoAsync(orden.Codigo));

        Assert.Null(Cupon(codigo).OrdenId);
        Assert.False(Cupon(codigo).Usado);
        Assert.Equal(0, Usos(p));

        var nueva = await ComprarAsync("ana", 1, codigo);
        Assert.Null(nueva.Error);
        Assert.Equal(nueva.Orden!.Id, Cupon(codigo).OrdenId);
    }

    [Fact]
    public async Task Un_cupon_no_se_aparta_en_dos_compras_a_la_vez()
    {
        var p = await CrearAsync(Input("ana"));
        var codigo = CodigoDe(p, "ana");
        var (otroEvento, otraZona, _) = CompraServiceTests.CrearEvento(_db);

        Assert.Null((await ComprarAsync("ana", 1, codigo)).Error);
        var segunda = await _compras.CrearOrdenAsync("ana", otroEvento.Id, new Dictionary<int, int> { [otraZona.Id] = 1 }, codigo);

        Assert.Equal("Este cupón ya se está usando en otra compra o la promoción alcanzó su límite de usos.", segunda.Error);
        Assert.Equal(0, _db.TiposEntrada.AsNoTracking().Single(t => t.Id == otraZona.Id).Reservadas);   // la reserva se revirtió
        Assert.Equal(1, Usos(p));

        // Si vuelve a elegir entradas del mismo evento, la orden pendiente anterior se cancela y libera su cupón
        Assert.Null((await ComprarAsync("ana", 2, codigo)).Error);
        Assert.Equal(1, Usos(p));
    }

    [Fact]
    public async Task El_limite_de_usos_de_la_promocion_se_respeta()
    {
        var input = Input("ana", "beto");
        input.LimiteUsos = 1;
        var p = await CrearAsync(input);

        var deAna = (await ComprarAsync("ana", 1, CodigoDe(p, "ana"))).Orden!;
        Assert.Equal("La promoción alcanzó su límite de usos.", (await ComprarAsync("beto", 1, CodigoDe(p, "beto"))).Error);

        // Si la compra de ana no se concreta, el cupo vuelve a quedar libre
        Assert.True(await _compras.CancelarAsync(deAna.Codigo, "ana"));
        Assert.Null((await ComprarAsync("beto", 1, CodigoDe(p, "beto"))).Error);
        Assert.Equal(1, Usos(p));
    }

    [Fact]
    public async Task La_pasarela_cobra_el_total_con_descuento()
    {
        var p = await CrearAsync(Input("ana"));
        var orden = (await ComprarAsync("ana", 3, CodigoDe(p, "ana"))).Orden!;

        var cobro = SolicitudCobro.DesdeOrden(orden, "Concierto");
        Assert.Equal(127.5m, cobro.Total);
        Assert.Equal(orden.Total, cobro.Total);
        Assert.Single(cobro.Items);
    }
}

// =============================
// 🎁 HU30: cupones usados a la vez desde varias instancias (BD en archivo)
// =============================
public class HU30ConcurrenciaCuponTests : IDisposable
{
    private readonly string _archivo = Path.Combine(Path.GetTempPath(), $"activafest-cupon-{Guid.NewGuid():N}.db");
    private string Cadena => $"Data Source={_archivo};Default Timeout=60;Pooling=False";
    private ApplicationDbContext NuevoContexto() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(Cadena).Options);

    public void Dispose()
    {
        foreach (var f in new[] { _archivo, _archivo + "-wal", _archivo + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task El_mismo_cupon_en_compras_simultaneas_solo_se_aplica_una_vez()
    {
        string codigo;
        var zonas = new List<(int Evento, int Zona)>();
        using (var db = NuevoContexto())
        {
            db.Database.EnsureCreated();
            CompraServiceTests.CrearUsuarios(db, "admin", "ana");
            db.Users.Where(u => u.Id == "ana").ExecuteUpdate(s => s.SetProperty(u => u.AceptaPromociones, true));
            for (var i = 0; i < 6; i++)
            {
                var (e, g, _) = CompraServiceTests.CrearEvento(db);
                zonas.Add((e.Id, g.Id));
            }
            var promociones = new PromocionService(db, new EmailFalso(), NullLogger<PromocionService>.Instance);
            var r = await promociones.CrearAsync(new PromocionInput
            {
                Nombre = "S/ 20", TipoDescuento = TipoDescuento.Monto, Valor = 20, FechaInicio = DateTime.Today, FechaFin = DateTime.Today,
                Clientes = { "ana" }
            }, "admin", "https://x");
            codigo = r.Promocion!.Cupones.Single().Codigo;
        }

        var inicio = new TaskCompletionSource();
        var tareas = zonas.Select(z => Task.Run(async () =>
        {
            await inicio.Task;
            using var db = NuevoContexto();
            var r = await Fabrica.Compras(db, new EmailFalso()).CrearOrdenAsync("ana", z.Evento, new Dictionary<int, int> { [z.Zona] = 1 }, codigo);
            return r.Error == null;
        })).ToList();
        inicio.SetResult();

        Assert.Equal(1, (await Task.WhenAll(tareas)).Count(ok => ok));
        using var control = NuevoContexto();
        Assert.Equal(1, control.Ordenes.Count(o => o.CuponId != null));
        Assert.Equal(1, control.Promociones.Single().Usos);
        Assert.Equal(1, control.TiposEntrada.Sum(t => t.Reservadas));
    }
}

// =============================
// 🎁 HU30: flujo completo por HTTP (administrador → correo → compra con cupón)
// =============================
public class HU30PromocionesVistaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU30PromocionesVistaTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    private async Task<(HttpClient Cliente, string Id, string Email)> UsuarioAsync(bool promociones)
    {
        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email, promociones: promociones);
        using var scope = _app.Services.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return (cliente, id, email);
    }

    private async Task<(int EventoId, int ZonaId)> EventoAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = new Evento
        {
            Titulo = $"Concierto {Guid.NewGuid():N}", Descripcion = "Concierto de prueba con cupones.", CategoriaId = 1, Ubicacion = "Cusco",
            Fecha = DateTime.Now.AddDays(20), Estado = EstadoEvento.Aprobado, Aforo = 50,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 60m, Capacidad = 50 } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        return (evento.Id, evento.TiposEntrada.First().Id);
    }

    [Fact]
    public async Task Solo_el_administrador_gestiona_promociones()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);

        foreach (var url in new[] { "/Promociones", "/Promociones/Nueva" })
        {
            Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync(url)).Headers.Location!.PathAndQuery);
            Assert.StartsWith("/Auth/Login", (await _app.Cliente().GetAsync(url)).Headers.Location!.PathAndQuery);
        }
    }

    [Fact]
    public async Task El_administrador_envia_cupones_y_el_cliente_compra_con_descuento()
    {
        var (ana, anaId, anaEmail) = await UsuarioAsync(promociones: true);
        var (beto, betoId, betoEmail) = await UsuarioAsync(promociones: false);
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");

        // Llega desde el filtro de clientes con la selección
        var nueva = $"/Promociones/Nueva?clientes={anaId}&clientes={betoId}&criterio=gasto%20%E2%89%A5%20500";
        var formulario = await TextoAsync(await admin.GetAsync(nueva));
        Assert.Contains("<b data-dato=\"seleccionados\">2</b>", formulario);
        Assert.Contains("<b data-dato=\"con-consentimiento\">1</b>", formulario);

        var hoy = DateTime.Today.ToString("yyyy-MM-dd");
        var r = await PostAsync(admin, nueva, "/Promociones/Crear",
            ("Nombre", "Gracias por tu fidelidad"), ("TipoDescuento", "Monto"), ("Valor", "25"),
            ("FechaInicio", hoy), ("FechaFin", DateTime.Today.AddDays(15).ToString("yyyy-MM-dd")), ("LimiteUsos", ""),
            ("CriterioSegmento", "gasto ≥ 500"), ("Clientes", anaId), ("Clientes", betoId));
        var detalle = r.Headers.Location!.OriginalString;
        Assert.StartsWith("/Promociones/Detalle/", detalle);
        Assert.Contains("1 cliente no aceptó recibir promociones", await TextoAsync(await admin.GetAsync(detalle)));

        // Solo ana recibe el correo, con su código
        Assert.Empty(_app.Correos.Para(betoEmail).Where(c => c.Asunto.Contains("cupón")));
        var correo = Assert.Single(_app.Correos.Para(anaEmail), c => c.Asunto.Contains("cupón"));
        var codigo = Regex.Match(correo.Html, "AF-[A-Z0-9]{8}").Value;
        Assert.NotEmpty(codigo);

        var (eventoId, zonaId) = await EventoAsync();

        // beto no puede usar el cupón de ana
        r = await PostAsync(beto, $"/Eventos/Detalle/{eventoId}", "/Compras/Iniciar",
            ("eventoId", eventoId.ToString()), ($"cantidades[{zonaId}]", "2"), ("cupon", codigo));
        Assert.StartsWith($"/Eventos/Detalle/{eventoId}", r.Headers.Location!.OriginalString);
        Assert.Contains("El cupón no es válido para tu cuenta.", await TextoAsync(await beto.GetAsync(r.Headers.Location)));

        // ana ve su cupón en la ficha y compra 2 entradas (S/ 120 − S/ 25)
        Assert.Contains($"<option value=\"{codigo}\">", await TextoAsync(await ana.GetAsync($"/Eventos/Detalle/{eventoId}")));
        r = await PostAsync(ana, $"/Eventos/Detalle/{eventoId}", "/Compras/Iniciar",
            ("eventoId", eventoId.ToString()), ($"cantidades[{zonaId}]", "2"), ("cupon", codigo));
        var checkout = r.Headers.Location!.OriginalString;
        Assert.StartsWith("/Compras/Checkout", checkout);
        var resumen = await TextoAsync(await ana.GetAsync(checkout));
        Assert.Contains($"Cupón {codigo}", resumen);
        Assert.Contains("S/ 95.00", resumen);

        var orden = checkout.Split('=')[1];
        r = await PostAsync(ana, checkout, "/Compras/Pagar", ("codigo", orden));
        r = await PostAsync(ana, r.Headers.Location!.OriginalString, "/Compras/PasarelaSimulada", ("codigo", orden), ("aprobar", "true"));
        Assert.StartsWith("/Compras/Confirmacion", r.Headers.Location!.OriginalString);

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cupon = await db.Cupones.Include(c => c.Orden).SingleAsync(c => c.Codigo == codigo);
            Assert.True(cupon.Usado);
            Assert.Equal((EstadoOrden.Pagada, 95m, 25m), (cupon.Orden!.Estado, cupon.Orden.Total, cupon.Orden.Descuento));
        }

        // El administrador ve el cupón usado
        var html = await TextoAsync(await admin.GetAsync(detalle));
        Assert.Contains("<span class=\"kpi-valor\" data-kpi=\"usados\">1</span>", html);
        Assert.DoesNotContain(anaEmail, html);
    }
}
