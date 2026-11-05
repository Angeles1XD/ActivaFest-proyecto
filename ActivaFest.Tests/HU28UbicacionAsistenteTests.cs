using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services;
using ActivaFest.Services.Asistente;
using ActivaFest.Services.Plano;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// Crea eventos con entradas emitidas directamente en la BD (teatro con asientos o recinto por zonas)
internal static class EntradasDePrueba
{
    public static Evento Evento(ApplicationDbContext db, string titulo, PlantillaPlano plantilla, DateTime fecha, EstadoEvento estado = EstadoEvento.Aprobado)
    {
        var zonas = plantilla == PlantillaPlano.Teatro
            ? new List<TipoEntrada>
            {
                new() { Nombre = "Platea A", Precio = 120, Filas = 5, AsientosPorFila = 18, Capacidad = 90, PuertaIngreso = "Puerta 1", AsientosNumerados = true },
                new() { Nombre = "Platea B", Precio = 80, Filas = 5, AsientosPorFila = 18, Capacidad = 90, PuertaIngreso = "Puerta 2", AsientosNumerados = true }
            }
            : new List<TipoEntrada>
            {
                new() { Nombre = "General", Precio = 40, Capacidad = 300, PuertaIngreso = "Ingreso" },
                new() { Nombre = "VIP", Precio = 120, Capacidad = 50, PuertaIngreso = "Ingreso VIP" }
            };
        var evento = new Evento
        {
            Titulo = titulo, Descripcion = "Evento de prueba de ubicación.", CategoriaId = 1, Ubicacion = "Teatro Municipal",
            Fecha = fecha, Estado = estado, PlantillaPlano = plantilla, Aforo = zonas.Sum(z => z.Capacidad), TiposEntrada = zonas
        };
        if (plantilla == PlantillaPlano.Teatro)
            foreach (var a in PlanoService.GenerarAsientos(zonas))
                a.TipoEntrada!.Asientos.Add(a);
        db.Eventos.Add(evento);
        db.SaveChanges();
        return evento;
    }

    // Entrada de "usuario" en la zona indicada; en teatro, en el asiento fila/número
    public static Entrada Entrada(ApplicationDbContext db, string usuario, Evento evento, string zona, string? fila = null, int? numero = null,
        EstadoEntrada estado = EstadoEntrada.Activa)
    {
        var tipo = db.TiposEntrada.Single(t => t.EventoId == evento.Id && t.Nombre == zona);
        var asiento = fila == null ? null : db.Asientos.Single(a => a.TipoEntradaId == tipo.Id && a.Fila == fila && a.Numero == numero);
        var orden = new Orden
        {
            Codigo = Guid.NewGuid().ToString("N")[..12], UsuarioId = usuario, EventoId = evento.Id, Estado = EstadoOrden.Pagada,
            Total = tipo.Precio, FechaPago = DateTime.UtcNow, FechaExpiracion = DateTime.UtcNow,
            Items = { new OrdenItem { TipoEntradaId = tipo.Id, Cantidad = 1, PrecioUnitario = tipo.Precio, AsientoId = asiento?.Id } }
        };
        db.Ordenes.Add(orden);
        db.SaveChanges();
        var entrada = new Entrada
        {
            Codigo = ActivaFest.Services.Entradas.CodigoSeguro.Generar(), OrdenId = orden.Id, EventoId = evento.Id, TipoEntradaId = tipo.Id,
            AsientoId = asiento?.Id, UsuarioId = usuario, Estado = estado
        };
        if (asiento != null)
            asiento.Estado = EstadoAsiento.Vendido;
        db.Entradas.Add(entrada);
        db.SaveChanges();
        return entrada;
    }
}

// =============================
// 🧭 HU28: indicaciones y ubicación de mis entradas (pruebas unitarias)
// =============================
public class HU28UbicacionTests : IDisposable
{
    private static readonly Lazy<MachineLearningService> Ml = new(() => new MachineLearningService());
    private readonly SqliteConnection _conexion;
    private readonly ApplicationDbContext _db;
    private readonly UbicacionService _ubicacion;

    public HU28UbicacionTests()
    {
        _conexion = new SqliteConnection("Data Source=:memory:");
        _conexion.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conexion).Options);
        _db.Database.EnsureCreated();
        CompraServiceTests.CrearUsuarios(_db, "ana", "beto");
        _ubicacion = new UbicacionService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexion.Dispose();
    }

    [Fact]
    public void La_letra_de_fila_se_convierte_en_su_posicion()
    {
        Assert.Equal((0, 25, 26), (PlanoService.IndiceFila("A"), PlanoService.IndiceFila("Z"), PlanoService.IndiceFila("AA")));
        for (var i = 0; i < 80; i++)
            Assert.Equal(i, PlanoService.IndiceFila(PlanoService.LetraFila(i)));
        Assert.Equal(-1, PlanoService.IndiceFila("a1"));
    }

    [Fact]
    public void En_teatro_indica_puerta_fila_y_el_lado_del_pasillo()
    {
        var zonas = new List<TipoEntrada>
        {
            new() { Id = 1, Nombre = "Platea A", Filas = 5, AsientosPorFila = 18, PuertaIngreso = "Puerta 1" },
            new() { Id = 2, Nombre = "Platea B", Filas = 5, AsientosPorFila = 18, PuertaIngreso = "Puerta 2" }
        };

        var derecha = PlanoService.Indicaciones(PlantillaPlano.Teatro, zonas, zonas[1], "F", 12);
        Assert.Equal(new[]
        {
            "Ingresa por la Puerta 2 y muestra tu QR.",
            "Avanza hacia el escenario por el pasillo central hasta la fila F (6.ª fila desde el escenario), en la zona Platea B.",
            "Tu asiento es el 12: el 3.º a la derecha del pasillo central, mirando al escenario."
        }, derecha);

        var izquierda = PlanoService.Indicaciones(PlantillaPlano.Teatro, zonas, zonas[0], "A", 2);
        Assert.Equal("Tu asiento es el 2: el 8.º a la izquierda del pasillo central, mirando al escenario.", izquierda[2]);
    }

    [Theory]
    [InlineData(PlantillaPlano.Discoteca, 1, "Dirígete al área VIP, a la izquierda de la pista: es la zona Zona 2.")]
    [InlineData(PlantillaPlano.Coliseo, 2, "Dirígete al 2.º anillo de tribuna desde el escenario: es la zona Zona 3.")]
    [InlineData(PlantillaPlano.CampoAbierto, 1, "Dirígete a la 2.ª franja desde el escenario, detrás de Zona 1: es la zona Zona 2.")]
    [InlineData(PlantillaPlano.Estadio, 0, "Dirígete a la tribuna del lado izquierdo de la cancha: es la zona Zona 1.")]
    public void En_recintos_por_zona_indica_donde_queda_la_zona(PlantillaPlano plantilla, int indice, string esperado)
    {
        var zonas = Enumerable.Range(1, 3).Select(i => new TipoEntrada { Id = i, Nombre = $"Zona {i}" }).ToList();

        var pasos = PlanoService.Indicaciones(plantilla, zonas, zonas[indice], null, null);

        Assert.Equal("Ingresa por la entrada principal del recinto y muestra tu QR.", pasos[0]);
        Assert.Equal(esperado, pasos[1]);
        Assert.Contains("no tiene asiento numerado", pasos[2]);
    }

    [Fact]
    public void Entiende_las_fechas_de_la_pregunta()
    {
        var hoy = new DateTime(2026, 10, 18); // domingo
        Assert.Equal(hoy, AsistenteService.FechaMencionada("donde esta mi asiento hoy", hoy));
        Assert.Equal(hoy.AddDays(1), AsistenteService.FechaMencionada("mi asiento para el concierto de manana", hoy));
        Assert.Equal(hoy.AddDays(2), AsistenteService.FechaMencionada("pasado manana", hoy));
        Assert.Equal(new DateTime(2026, 10, 24), AsistenteService.FechaMencionada("el sabado por que puerta entro", hoy));
        Assert.Equal(new DateTime(2026, 11, 28), AsistenteService.FechaMencionada("mi asiento del 28 11", hoy));
        Assert.Null(AsistenteService.FechaMencionada("donde esta mi asiento", hoy));
    }

    [Theory]
    [InlineData("¿Dónde está mi asiento?")]
    [InlineData("¿Por qué puerta entro?")]
    [InlineData("¿Cómo llego a mi asiento para el concierto de mañana?")]
    [InlineData("en qué fila estoy")]
    [InlineData("¿dónde me toca sentarme?")]
    public void Reconoce_la_pregunta_por_mi_ubicacion(string pregunta)
    {
        var asistente = new AsistenteService(_db, Ml.Value, null!, null!, new RedactorDesactivado(), _ubicacion,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AsistenteService>.Instance);
        Assert.Equal(Intenciones.MiUbicacion, asistente.DetectarIntencion(pregunta));
        Assert.Equal(Intenciones.Ubicacion, asistente.DetectarIntencion("¿Cómo llego al estadio?"));
    }

    [Fact]
    public async Task Solo_devuelve_las_entradas_activas_del_propio_usuario()
    {
        var teatro = EntradasDePrueba.Evento(_db, "Gala de Primavera", PlantillaPlano.Teatro, DateTime.Today.AddDays(1).AddHours(20));
        var mia = EntradasDePrueba.Entrada(_db, "ana", teatro, "Platea B", "F", 12);
        EntradasDePrueba.Entrada(_db, "beto", teatro, "Platea B", "F", 13);                          // de otra persona
        EntradasDePrueba.Entrada(_db, "ana", teatro, "Platea A", "A", 1, EstadoEntrada.Usada);        // ya usada
        var cancelado = EntradasDePrueba.Evento(_db, "Cancelado", PlantillaPlano.Discoteca, DateTime.Today.AddDays(1), EstadoEvento.Cancelado);
        EntradasDePrueba.Entrada(_db, "ana", cancelado, "VIP");
        var pasado = EntradasDePrueba.Evento(_db, "Ya pasó", PlantillaPlano.Discoteca, DateTime.Now.AddDays(-3));
        EntradasDePrueba.Entrada(_db, "ana", pasado, "VIP");

        var ubicaciones = await _ubicacion.ObtenerUbicacionDeMiEntradaAsync("ana");

        var u = Assert.Single(ubicaciones);
        Assert.Equal((mia.Id, "Platea B", "F", 12, "Puerta 2"), (u.EntradaId, u.Zona, u.Fila, u.Asiento, u.Puerta));
        Assert.Equal("Platea B · fila F · asiento 12 · Puerta 2", u.Resumen);
        Assert.Equal(3, u.Indicaciones.Count);

        Assert.Single(await _ubicacion.ObtenerUbicacionDeMiEntradaAsync("ana", DateTime.Today.AddDays(1)));
        Assert.Empty(await _ubicacion.ObtenerUbicacionDeMiEntradaAsync("ana", DateTime.Today));

        // La función de Semantic Kernel usa al usuario fijado en el servidor, no uno que elija la IA
        var kernel = PluginUbicacion.KernelPara(_ubicacion, "beto");
        var resultado = await kernel.InvokeAsync(PluginUbicacion.NombrePlugin, PluginUbicacion.NombreFuncion);
        Assert.Equal(13, Assert.Single(resultado.GetValue<List<UbicacionMiEntrada>>()!).Asiento);
    }
}

// =============================
// 🧭 HU28: el asistente responde "¿dónde está mi asiento?" (integración por /api/chat)
// =============================
public class HU28AsistenteChatTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU28AsistenteChatTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<JsonElement> PreguntarAsync(HttpClient cliente, string mensaje, int? eventoId = null)
    {
        var r = await cliente.PostAsJsonAsync("/api/chat", new { mensaje, eventoId });
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(HttpClient Cliente, string Id)> UsuarioAsync()
    {
        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email);
        using var scope = _app.Services.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return (cliente, id);
    }

    private T Con<T>(Func<ApplicationDbContext, T> accion)
    {
        using var scope = _app.Services.CreateScope();
        return accion(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    [Fact]
    public async Task Sin_sesion_pide_iniciar_sesion()
    {
        var r = await PreguntarAsync(_app.Cliente(), "¿Dónde está mi asiento?");

        Assert.Equal("MiUbicacion", r.GetProperty("intencion").GetString());
        Assert.Contains("inicia sesión", r.GetProperty("reply").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("plano").ValueKind);
    }

    [Fact]
    public async Task Responde_zona_fila_asiento_puerta_y_como_llegar_con_el_plano_resaltado()
    {
        var (ana, anaId) = await UsuarioAsync();
        var (beto, _) = await UsuarioAsync();
        var titulo = $"Gala {Guid.NewGuid():N}"[..13];
        var entrada = Con(db =>
        {
            var e = EntradasDePrueba.Evento(db, titulo, PlantillaPlano.Teatro, DateTime.Today.AddDays(1).AddHours(20));
            return EntradasDePrueba.Entrada(db, anaId, e, "Platea B", "F", 12);
        });

        var r = await PreguntarAsync(ana, "¿Dónde está mi asiento para mañana?");
        var texto = r.GetProperty("reply").GetString()!;

        Assert.Contains($"Tu entrada para «{titulo}»", texto);
        Assert.Contains("Platea B · fila F · asiento 12 · Puerta 2", texto);
        Assert.Contains("1. Ingresa por la Puerta 2", texto);
        Assert.Contains("el 3.º a la derecha del pasillo central", texto);
        var plano = r.GetProperty("plano").GetString()!;
        Assert.Equal($"/Entradas/Plano/{entrada.Id}", plano);

        var svg = await ana.GetStringAsync(plano);
        Assert.Matches(@"class=""asiento asiento-tuyo""[^>]*data-fila=""F"" data-numero=""12""", svg);

        // Otro usuario no ve esa entrada ni su plano
        var otro = await PreguntarAsync(beto, "¿Dónde está mi asiento para mañana?");
        Assert.Contains("No encontré entradas activas", otro.GetProperty("reply").GetString());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await beto.GetAsync(plano)).StatusCode);
    }

    [Fact]
    public async Task Con_varias_entradas_para_la_fecha_pregunta_a_cual_se_refiere()
    {
        var (ana, anaId) = await UsuarioAsync();
        var sufijo = Guid.NewGuid().ToString("N")[..6];
        Con(db =>
        {
            var teatro = EntradasDePrueba.Evento(db, $"Hamlet {sufijo}", PlantillaPlano.Teatro, DateTime.Today.AddDays(2).AddHours(18));
            EntradasDePrueba.Entrada(db, anaId, teatro, "Platea A", "B", 4);
            var disco = EntradasDePrueba.Evento(db, $"Neon Party {sufijo}", PlantillaPlano.Discoteca, DateTime.Today.AddDays(2).AddHours(23));
            return EntradasDePrueba.Entrada(db, anaId, disco, "VIP");
        });

        var r = await PreguntarAsync(ana, "¿Por qué puerta entro pasado mañana?");
        Assert.Contains("¿A cuál te refieres?", r.GetProperty("reply").GetString());
        var sugerencias = r.GetProperty("sugerencias").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains($"¿Dónde está mi asiento para Neon Party {sufijo}?", sugerencias);

        // Al elegir el evento responde con la zona y cómo llegar
        var elegido = await PreguntarAsync(ana, $"¿Dónde está mi asiento para Neon Party {sufijo}?");
        var texto = elegido.GetProperty("reply").GetString()!;
        Assert.Contains("VIP · Ingreso VIP", texto);
        Assert.Contains("Dirígete al área VIP, a la izquierda de la pista", texto);
    }

    [Fact]
    public async Task Sin_entradas_activas_ofrece_el_catalogo()
    {
        var (ana, _) = await UsuarioAsync();

        var r = await PreguntarAsync(ana, "¿Dónde está mi asiento?");

        Assert.Contains("No encontré entradas activas", r.GetProperty("reply").GetString());
        var enlaces = r.GetProperty("enlaces").EnumerateArray().Select(e => e.GetProperty("url").GetString()).ToList();
        Assert.Contains("/Eventos", enlaces);
    }
}
