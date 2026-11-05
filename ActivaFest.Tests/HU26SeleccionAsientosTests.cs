using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Plano;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🪑 HU26: ELEGIR ZONA O ASIENTO AL COMPRAR (pruebas del servicio)
// =============================
public class HU26SeleccionAsientosTests : IDisposable
{
    private const string Url = "https://x/Entradas";

    private readonly SqliteConnection _conexion;
    private readonly ApplicationDbContext _db;
    private readonly BloqueoAsientosEnMemoria _redis = new(); // el "Redis" compartido por las compras de la prueba
    private readonly CompraService _compras;

    public HU26SeleccionAsientosTests()
    {
        _conexion = new SqliteConnection("Data Source=:memory:");
        _conexion.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conexion).Options);
        _db.Database.EnsureCreated();
        _compras = Fabrica.Compras(_db, new EmailFalso(), asientos: _redis);
        CompraServiceTests.CrearUsuarios(_db, "ana", "beto");
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexion.Dispose();
    }

    // Teatro con 2 zonas: Platea (2 filas x 4, S/ 80) y Balcón (1 fila x 4, S/ 40)
    private (Evento Evento, List<Asiento> Asientos) Teatro()
    {
        var evento = new Evento
        {
            Titulo = "Hamlet", Descripcion = "Obra de teatro.", CategoriaId = 2, Ubicacion = "Teatro Municipal", Fecha = DateTime.Now.AddDays(10),
            Estado = EstadoEvento.Aprobado, PlantillaPlano = PlantillaPlano.Teatro, OrganizadorId = "beto",
            TiposEntrada =
            {
                new TipoEntrada { Nombre = "Platea", Precio = 80, Capacidad = 8, AsientosNumerados = true, Filas = 2, AsientosPorFila = 4 },
                new TipoEntrada { Nombre = "Balcón", Precio = 40, Capacidad = 4, AsientosNumerados = true, Filas = 1, AsientosPorFila = 4 }
            }
        };
        evento.Aforo = 12;
        _db.Eventos.Add(evento);
        _db.SaveChanges();
        var asientos = PlanoService.GenerarAsientos(evento.TiposEntrada.OrderBy(t => t.Id).ToList());
        asientos.ForEach(a => a.TipoEntrada = null);
        _db.Asientos.AddRange(asientos);
        _db.SaveChanges();
        return (evento, _db.Asientos.AsNoTracking().Where(a => a.TipoEntrada!.EventoId == evento.Id).OrderBy(a => a.Id).ToList());
    }

    private Asiento Asiento(int id) => _db.Asientos.AsNoTracking().Single(a => a.Id == id);
    private TipoEntrada Zona(int id) => _db.TiposEntrada.AsNoTracking().Single(t => t.Id == id);

    [Fact]
    public async Task Elegir_asientos_los_bloquea_y_al_pagar_quedan_vendidos_en_la_entrada()
    {
        var (evento, asientos) = Teatro();
        var a1 = asientos.First(a => a.Fila == "A" && a.Numero == 2);
        var c1 = asientos.First(a => a.Fila == "C" && a.Numero == 1); // Balcón

        var r = await _compras.CrearOrdenConAsientosAsync("ana", evento.Id, new[] { a1.Id, c1.Id });

        Assert.Null(r.Error);
        Assert.Equal(120m, r.Orden!.Total);
        Assert.Equal(EstadoAsiento.Bloqueado, Asiento(a1.Id).Estado);
        Assert.Equal(1, Zona(a1.TipoEntradaId).Reservadas);

        await _compras.ConfirmarPagoAsync(r.Orden.Codigo, "Simulado", "SIM", r.Orden.Total, Url);

        Assert.Equal(EstadoAsiento.Vendido, Asiento(a1.Id).Estado);
        Assert.Equal(EstadoAsiento.Vendido, Asiento(c1.Id).Estado);
        Assert.Equal(new[] { a1.Id, c1.Id }.OrderBy(x => x), _db.Entradas.Where(e => e.OrdenId == r.Orden.Id).Select(e => e.AsientoId!.Value).OrderBy(x => x));
        Assert.Equal(1, Zona(a1.TipoEntradaId).Vendidas);
    }

    // Criterio: dos usuarios no pueden comprar el mismo asiento: el segundo recibe un aviso para elegir otro
    [Fact]
    public async Task El_segundo_usuario_que_elige_el_mismo_asiento_recibe_un_aviso_para_elegir_otro()
    {
        var (evento, asientos) = Teatro();
        var b3 = asientos.First(a => a.Fila == "B" && a.Numero == 3);

        var ana = await _compras.CrearOrdenConAsientosAsync("ana", evento.Id, new[] { b3.Id });
        var beto = await _compras.CrearOrdenConAsientosAsync("beto", evento.Id, new[] { b3.Id });

        Assert.Null(ana.Error);
        Assert.Equal("El asiento B-3 ya no está disponible: elige otro en el plano.", beto.Error);
    }

    [Fact]
    public async Task Dos_compras_simultaneas_del_mismo_asiento_venden_una_sola_vez()
    {
        var archivo = Path.Combine(Path.GetTempPath(), $"activafest-asientos-{Guid.NewGuid():N}.db");
        string Cadena() => $"Data Source={archivo};Default Timeout=60;Pooling=False";
        ApplicationDbContext Contexto() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(Cadena()).Options);
        try
        {
            int eventoId, asientoId;
            var usuarios = Enumerable.Range(1, 10).Select(i => $"u{i}").ToArray();
            using (var db = Contexto())
            {
                db.Database.EnsureCreated();
                CompraServiceTests.CrearUsuarios(db, usuarios.Append("org").ToArray());
                var evento = new Evento
                {
                    Titulo = "Concierto", Descripcion = "x", CategoriaId = 1, Ubicacion = "x", Fecha = DateTime.Now.AddDays(5), Estado = EstadoEvento.Aprobado,
                    PlantillaPlano = PlantillaPlano.Teatro, OrganizadorId = "org", Aforo = 4,
                    TiposEntrada = { new TipoEntrada { Nombre = "Platea", Precio = 50, Capacidad = 4, AsientosNumerados = true, Filas = 1, AsientosPorFila = 4 } }
                };
                db.Eventos.Add(evento);
                db.SaveChanges();
                var generados = PlanoService.GenerarAsientos(evento.TiposEntrada.ToList());
                generados.ForEach(a => a.TipoEntrada = null);
                db.Asientos.AddRange(generados);
                db.SaveChanges();
                (eventoId, asientoId) = (evento.Id, db.Asientos.First().Id);
            }

            // Sin bloqueos compartidos (cada compra con su propio "Redis"): solo protege la base de datos
            var inicio = new TaskCompletionSource();
            var tareas = usuarios.Select(u => Task.Run(async () =>
            {
                await inicio.Task;
                using var db = Contexto();
                var compras = Fabrica.Compras(db, new EmailFalso());
                var r = await compras.CrearOrdenConAsientosAsync(u, eventoId, new[] { asientoId });
                if (r.Orden != null)
                    await compras.ConfirmarPagoAsync(r.Orden.Codigo, "Simulado", "SIM-" + u, r.Orden.Total, Url);
                return r.Error == null;
            })).ToList();
            inicio.SetResult();
            var exitos = (await Task.WhenAll(tareas)).Count(ok => ok);

            using var verificacion = Contexto();
            Assert.Equal(1, exitos);
            Assert.Equal(1, verificacion.Entradas.Count(e => e.AsientoId == asientoId));
            Assert.Equal(EstadoAsiento.Vendido, verificacion.Asientos.Single(a => a.Id == asientoId).Estado);
        }
        finally
        {
            foreach (var f in new[] { archivo, archivo + "-wal", archivo + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    // Bloqueo de 10 minutos en Redis: aunque la BD aún diga Disponible, otro comprador no puede tomarlo
    [Fact]
    public async Task Un_asiento_apartado_en_redis_por_otra_compra_no_se_puede_elegir()
    {
        var (evento, asientos) = Teatro();
        var a1 = asientos.First();
        await _redis.BloquearAsync(new[] { a1.Id }, "OTRA-ORDEN", TimeSpan.FromMinutes(10));

        var r = await _compras.CrearOrdenConAsientosAsync("ana", evento.Id, new[] { a1.Id });

        Assert.Contains("ya no está disponible", r.Error);
        Assert.Equal(EstadoAsiento.Disponible, Asiento(a1.Id).Estado);
        Assert.Equal(0, Zona(a1.TipoEntradaId).Reservadas);
    }

    // Ciclo de vida del Asiento: Bloqueado → Disponible si la reserva expira o el pago se rechaza
    [Fact]
    public async Task Si_la_reserva_expira_o_el_pago_se_rechaza_el_asiento_vuelve_a_estar_disponible()
    {
        var (evento, asientos) = Teatro();
        var a1 = asientos[0];
        var a2 = asientos[1];

        var expira = (await _compras.CrearOrdenConAsientosAsync("ana", evento.Id, new[] { a1.Id })).Orden!;
        await _db.Ordenes.Where(o => o.Id == expira.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.FechaExpiracion, DateTime.UtcNow.AddSeconds(-1)));
        await _compras.LiberarExpiradasAsync();

        var rechazada = (await _compras.CrearOrdenConAsientosAsync("beto", evento.Id, new[] { a2.Id })).Orden!;
        await _compras.RechazarPagoAsync(rechazada.Codigo);

        Assert.Equal(EstadoAsiento.Disponible, Asiento(a1.Id).Estado);
        Assert.Equal(EstadoAsiento.Disponible, Asiento(a2.Id).Estado);

        // Y otra persona ya puede comprarlos (también se soltaron en Redis)
        Assert.Null((await _compras.CrearOrdenConAsientosAsync("beto", evento.Id, new[] { a1.Id })).Error);
        Assert.Null((await _compras.CrearOrdenConAsientosAsync("ana", evento.Id, new[] { a2.Id })).Error);
    }

    [Fact]
    public async Task Un_pago_tardio_no_se_queda_con_un_asiento_que_ya_compro_otro()
    {
        var (evento, asientos) = Teatro();
        var a1 = asientos[0];
        var tardia = (await _compras.CrearOrdenConAsientosAsync("ana", evento.Id, new[] { a1.Id })).Orden!;
        await _db.Ordenes.Where(o => o.Id == tardia.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.FechaExpiracion, DateTime.UtcNow.AddSeconds(-1)));
        var beto = (await _compras.CrearOrdenConAsientosAsync("beto", evento.Id, new[] { a1.Id })).Orden!;
        await _compras.ConfirmarPagoAsync(beto.Codigo, "Simulado", "SIM-B", beto.Total, Url);

        var r = await _compras.ConfirmarPagoAsync(tardia.Codigo, "MercadoPago", "123", tardia.Total, Url);

        Assert.Equal(ResultadoConfirmacion.RequiereReembolso, r);
        Assert.Equal(1, _db.Entradas.Count(e => e.AsientoId == a1.Id));
    }

    // Criterio: en eventos sin asientos numerados solo se elige la zona y la cantidad
    [Fact]
    public async Task Sin_asientos_numerados_se_elige_zona_y_cantidad_y_en_teatro_se_exige_el_asiento()
    {
        var (general, zonaGeneral, _) = CompraServiceTests.CrearEvento(_db);
        var (teatro, asientos) = Teatro();

        Assert.Null((await _compras.CrearOrdenAsync("ana", general.Id, new Dictionary<int, int> { [zonaGeneral.Id] = 3 })).Error);
        Assert.Equal("En este recinto eliges tus asientos en el plano.",
            (await _compras.CrearOrdenAsync("beto", teatro.Id, new Dictionary<int, int> { [asientos[0].TipoEntradaId] = 2 })).Error);
    }

    [Fact]
    public async Task No_se_eligen_asientos_de_otro_evento_ni_mas_de_diez()
    {
        var (evento, _) = Teatro();
        var (otro, asientosOtro) = Teatro();

        var ajeno = await _compras.CrearOrdenConAsientosAsync("ana", evento.Id, new[] { asientosOtro[0].Id });
        var muchos = await _compras.CrearOrdenConAsientosAsync("ana", otro.Id, asientosOtro.Take(11).Select(a => a.Id).ToArray());

        Assert.Equal("Algún asiento elegido no pertenece a este evento.", ajeno.Error);
        Assert.Contains("como máximo 10", muchos.Error);
    }

    // Índice único por asiento en la BD: aunque todo lo demás fallara, no hay dos entradas con el mismo asiento
    [Fact]
    public async Task La_base_de_datos_rechaza_dos_entradas_con_el_mismo_asiento()
    {
        var (evento, asientos) = Teatro();
        var r = (await _compras.CrearOrdenConAsientosAsync("ana", evento.Id, new[] { asientos[0].Id })).Orden!;
        await _compras.ConfirmarPagoAsync(r.Codigo, "Simulado", "SIM", r.Total, Url);

        _db.Entradas.Add(new Entrada
        {
            Codigo = "duplicada-" + Guid.NewGuid().ToString("N"), OrdenId = r.Id, EventoId = evento.Id, TipoEntradaId = asientos[0].TipoEntradaId,
            AsientoId = asientos[0].Id, UsuarioId = "beto"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }
}

// =============================
// 🪑 HU26: COMPRA ELIGIENDO ASIENTOS EN EL PLANO (pruebas de integración)
// =============================
public class HU26CompraConAsientosTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU26CompraConAsientosTests(AppDePrueba app)
    {
        _app = app;
    }

    private (int EventoId, List<int> Asientos) TeatroPublicado()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = new Evento
        {
            Titulo = $"Sinfónico {Guid.NewGuid():N}", Descripcion = "Concierto sinfónico.", CategoriaId = 1, Ubicacion = "Teatro Municipal",
            Fecha = DateTime.Now.AddDays(12), Estado = EstadoEvento.Aprobado, PlantillaPlano = PlantillaPlano.Teatro, Aforo = 20,
            TiposEntrada = { new TipoEntrada { Nombre = "Platea", Precio = 60, Capacidad = 20, AsientosNumerados = true, Filas = 2, AsientosPorFila = 10 } }
        };
        db.Eventos.Add(evento);
        db.SaveChanges();
        var asientos = PlanoService.GenerarAsientos(evento.TiposEntrada.ToList());
        asientos.ForEach(a => a.TipoEntrada = null);
        db.Asientos.AddRange(asientos);
        db.SaveChanges();
        return (evento.Id, db.Asientos.Where(a => a.TipoEntrada!.EventoId == evento.Id).OrderBy(a => a.Id).Select(a => a.Id).ToList());
    }

    [Fact]
    public async Task El_plano_es_interactivo_y_la_compra_lleva_los_asientos_elegidos()
    {
        var (eventoId, asientos) = TeatroPublicado();
        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, Correo());

        var ficha = await cliente.GetStringAsync($"/Eventos/Detalle/{eventoId}");
        Assert.Contains("plano-interactivo", ficha);
        Assert.Contains("id=\"asientosElegidos\"", ficha);
        Assert.Contains("/js/plano-compra.js", ficha);

        var datos = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", await TokenAsync(cliente, $"/Eventos/Detalle/{eventoId}")),
            new("eventoId", eventoId.ToString()),
            new("asientos", asientos[0].ToString()),
            new("asientos", asientos[1].ToString())
        };
        var r = await cliente.PostAsync("/Compras/Iniciar", new FormUrlEncodedContent(datos));
        var checkout = r.Headers.Location!.OriginalString;
        Assert.StartsWith("/Compras/Checkout", checkout);

        var resumen = await TextoAsync(await cliente.GetAsync(checkout));
        Assert.Contains("Platea · Fila A, asiento 1", resumen);
        Assert.Contains("Platea · Fila A, asiento 2", resumen);

        // Los vendidos o bloqueados se muestran como no disponibles para otro usuario
        var otro = _app.Cliente();
        await RegistrarAsync(otro, Correo());
        var fichaOtro = await otro.GetStringAsync($"/Eventos/Detalle/{eventoId}");
        Assert.Contains($"data-asiento=\"{asientos[0]}\" data-zona", fichaOtro);
        Assert.Matches($@"class=""asiento asiento-ocupado"" data-asiento=""{asientos[0]}""", fichaOtro);
    }

    [Fact]
    public async Task Sin_sesion_el_plano_no_es_interactivo()
    {
        var (eventoId, _) = TeatroPublicado();

        var ficha = await _app.Cliente().GetStringAsync($"/Eventos/Detalle/{eventoId}");

        Assert.DoesNotContain("plano-interactivo", ficha);
        Assert.Contains("Inicia sesión para comprar", WebUtility.HtmlDecode(ficha));
    }
}
