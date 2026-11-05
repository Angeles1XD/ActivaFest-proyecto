using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using ActivaFest.Models;
using ActivaFest.Services;
using ActivaFest.Services.Asistente;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Recomendaciones;

namespace ActivaFest.Tests;

// El modelo ML.NET se entrena una sola vez para todas las pruebas
public class ModeloCompartido
{
    public MachineLearningService Ml { get; } = new();
}

public abstract class BaseSprint6 : BaseSprint4, IClassFixture<ModeloCompartido>
{
    protected readonly CatalogoService Catalogo;
    protected readonly RecomendacionService Recomendaciones;
    protected readonly AsistenteService Asistente;
    protected readonly Evento Rock, Hamlet, Feria;
    protected readonly ModeloCompartido Modelo;

    protected BaseSprint6(ModeloCompartido modelo)
    {
        Modelo = modelo;
        Catalogo = new CatalogoService(Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), NullLogger<CatalogoService>.Instance);
        Recomendaciones = new RecomendacionService(Db);
        Asistente = new AsistenteService(Db, modelo.Ml, Catalogo, Recomendaciones, new RedactorDesactivado(), new ActivaFest.Services.Plano.UbicacionService(Db), NullLogger<AsistenteService>.Instance);

        Rock = Crear("Rock en el Parque", 1, dias: 10, -16.40, -71.54, ("General", 50m, 100), ("VIP", 120m, 5));
        Hamlet = Crear("Hamlet", 2, dias: 6, -16.41, -71.53, ("Platea", 35m, 200));
        Feria = Crear("Feria Gastronómica", 7, dias: 2, -12.05, -77.04, ("Libre", 0m, 500));
    }

    protected Evento Crear(string titulo, int categoria, int dias, double lat, double lng, params (string Nombre, decimal Precio, int Capacidad)[] zonas)
    {
        var evento = new Evento
        {
            Titulo = titulo,
            Descripcion = $"Descripción de {titulo}: música, comida y buena onda para todos.",
            CategoriaId = categoria,
            Ubicacion = $"Recinto de {titulo}",
            Latitud = lat,
            Longitud = lng,
            Fecha = DateTime.Today.AddDays(dias).AddHours(20),
            Estado = EstadoEvento.Aprobado,
            OrganizadorId = "org",
            TiposEntrada = zonas.Select(z => new TipoEntrada { Nombre = z.Nombre, Precio = z.Precio, Capacidad = z.Capacidad }).ToList()
        };
        evento.Aforo = evento.TiposEntrada.Sum(t => t.Capacidad);
        Db.Eventos.Add(evento);
        Db.SaveChanges();
        return evento;
    }
}

// =============================
// 🤖 OE3: BANCO DE PRUEBAS DEL ASISTENTE (≥ 80 % resuelto sin ayuda humana)
// =============================
// Preguntas redactadas distinto a los ejemplos de entrenamiento, como las escribiría un usuario real.
public class BancoPruebasAsistenteTests : BaseSprint6
{
    private readonly ITestOutputHelper _salida;

    public BancoPruebasAsistenteTests(ModeloCompartido modelo, ITestOutputHelper salida) : base(modelo)
    {
        _salida = salida;
    }

    // (pregunta, intención esperada, ¿se pregunta desde la ficha de Rock en el Parque?)
    public static readonly (string Pregunta, string Esperada, bool DesdeFicha)[] Banco =
    {
        // Preguntas frecuentes de la plataforma
        ("Hola, buenas noches", Intenciones.Saludo, false),
        ("mil gracias!!", Intenciones.Agradecimiento, false),
        ("¿Qué tengo que hacer para comprar boletos?", Intenciones.ComoComprar, false),
        ("¿Aceptan tarjeta de débito?", Intenciones.MediosPago, false),
        ("¿Se puede pagar con Yape?", Intenciones.MediosPago, false),
        ("¿El pago en la web es seguro?", Intenciones.MediosPago, false),
        ("No encuentro mis entradas", Intenciones.MisEntradas, false),
        ("No me llegó ningún correo después de pagar", Intenciones.MisEntradas, false),
        ("Quiero que me devuelvan mi plata", Intenciones.ReembolsoCancelacion, false),
        ("¿Cómo cancelo mi compra?", Intenciones.ReembolsoCancelacion, false),
        ("¿Puedo regalarle mi entrada a un amigo?", Intenciones.TransferirEntrada, false),
        ("¿Necesito imprimir el QR o basta con el celular?", Intenciones.IngresoQr, false),
        ("¿A qué hora puedo ingresar con mi QR?", Intenciones.IngresoQr, false),
        ("Me cobraron dos veces", Intenciones.ProblemaCompra, false),
        ("Soy promotor, ¿cómo vendo las entradas de mi concierto acá?", Intenciones.PublicarEvento, false),
        ("¿Cuánto cuesta destacar mi evento?", Intenciones.Destacar, false),
        ("¿Qué porcentaje cobra ActivaFest por venta?", Intenciones.Comisiones, false),
        ("Olvidé mi clave", Intenciones.Cuenta, false),
        ("quiero crear una cuenta", Intenciones.Cuenta, false),
        ("¿Pueden entrar niños?", Intenciones.Edad, false),
        ("Necesito hablar con un humano", Intenciones.Contacto, false),

        // Búsqueda y recomendaciones (datos reales)
        ("¿Qué conciertos hay este mes?", Intenciones.BuscarEventos, false),
        ("¿Hay algo gratis para hacer?", Intenciones.BuscarEventos, false),
        ("Busco obras de teatro", Intenciones.BuscarEventos, false),
        ("planes para el fin de semana", Intenciones.BuscarEventos, false),
        ("¿Tienen eventos para familias?", Intenciones.BuscarEventos, false),
        ("¿Qué me recomiendas?", Intenciones.Recomendacion, false),
        ("Sugiéreme un plan", Intenciones.Recomendacion, false),

        // Ubicación, horarios, tarifas e instalaciones de un evento (OE3)
        ("¿Dónde queda Rock en el Parque?", Intenciones.Ubicacion, false),
        ("¿Cómo llego a la Feria Gastronómica?", Intenciones.Ubicacion, false),
        ("¿A qué hora empieza Hamlet?", Intenciones.Horario, false),
        ("¿Cuándo es el Rock en el Parque?", Intenciones.Horario, false),
        ("¿Cuánto cuesta la entrada a Hamlet?", Intenciones.Precio, false),
        ("¿Quedan entradas VIP para Rock en el Parque?", Intenciones.Precio, false),
        ("¿Hay estacionamiento en Hamlet?", Intenciones.Instalaciones, false),
        ("¿Cuál es el aforo de la Feria Gastronómica?", Intenciones.Instalaciones, false),

        // Preguntas de seguimiento desde la ficha del evento (sin nombrarlo)
        ("¿Dónde es?", Intenciones.Ubicacion, true),
        ("dime los precios", Intenciones.Precio, true),
        ("¿qué día es?", Intenciones.Horario, true),
        ("¿cómo es el lugar?", Intenciones.Instalaciones, true),
    };

    [Fact]
    public async Task Resuelve_al_menos_el_80_por_ciento_del_banco_de_pruebas()
    {
        var aciertos = 0;
        foreach (var (pregunta, esperada, desdeFicha) in Banco)
        {
            var r = await Asistente.ResponderAsync(new SolicitudChat { Mensaje = pregunta, EventoId = desdeFicha ? Rock.Id : null }, null);
            var ok = r.Resuelta && r.Intencion == esperada;
            if (ok) aciertos++;
            else _salida.WriteLine($"✗ \"{pregunta}\" → {r.Intencion} (esperada {esperada}, resuelta: {r.Resuelta})");
        }

        var porcentaje = (double)aciertos / Banco.Length;
        _salida.WriteLine($"Resueltas correctamente: {aciertos}/{Banco.Length} = {porcentaje:P0}");
        Assert.True(porcentaje >= 0.8, $"Solo {porcentaje:P0} del banco de pruebas resuelto");
    }

    [Theory]
    [InlineData("asdfghjk")]
    [InlineData("el perro azul del vecino")]
    public async Task Ante_algo_que_no_entiende_lo_dice_y_ofrece_ayuda(string mensaje)
    {
        var r = await Asistente.ResponderAsync(new SolicitudChat { Mensaje = mensaje }, null);

        Assert.False(r.Resuelta);
        Assert.Equal(Intenciones.Desconocida, r.Intencion);
        Assert.NotEmpty(r.Sugerencias);
    }
}

// =============================
// 🤖 HU14: CONTENIDO DE LAS RESPUESTAS
// =============================
public class AsistenteRespuestasTests : BaseSprint6
{
    public AsistenteRespuestasTests(ModeloCompartido modelo) : base(modelo) { }

    private Task<RespuestaChat> Preguntar(string mensaje, int? eventoId = null) =>
        Asistente.ResponderAsync(new SolicitudChat { Mensaje = mensaje, EventoId = eventoId }, null);

    [Fact]
    public async Task Responde_precios_reales_por_zona_y_avisa_si_quedan_pocas()
    {
        var r = await Preguntar("¿Cuánto cuesta Rock en el Parque?");

        Assert.Contains("General: S/ 50.00", r.Texto);
        Assert.Contains("VIP: S/ 120.00 (¡quedan 5!)", r.Texto);
        Assert.Equal(Rock.Id, r.EventoId);
        Assert.Contains(r.Enlaces, e => e.Url == $"/Eventos/Detalle/{Rock.Id}#comprar");
    }

    [Fact]
    public async Task Responde_ubicacion_con_enlace_al_mapa_y_a_google_maps()
    {
        var r = await Preguntar("¿dónde queda hamlet?");

        Assert.Contains("Recinto de Hamlet", r.Texto);
        Assert.Contains(r.Enlaces, e => e.Url.StartsWith("https://www.google.com/maps/dir/?api=1&destination=-16.41,-71.53"));
    }

    [Fact]
    public async Task Responde_horario_con_apertura_de_puertas()
    {
        var r = await Preguntar("¿a qué hora empieza?", Hamlet.Id);

        Assert.Contains("20:00", r.Texto);
        Assert.Contains("14:00", r.Texto); // 6 horas antes
    }

    [Fact]
    public async Task Si_no_sabe_de_que_evento_se_habla_lo_pregunta_con_sugerencias()
    {
        var r = await Preguntar("¿cuánto cuesta la entrada?");

        Assert.False(r.Resuelta);
        Assert.Contains("¿Sobre qué evento", r.Texto);
        Assert.Contains(r.Sugerencias, s => s.Contains("Feria Gastronómica"));
    }

    [Fact]
    public async Task Busca_eventos_gratis_con_datos_reales()
    {
        var r = await Preguntar("¿hay eventos gratis?");

        Assert.Contains("Feria Gastronómica", r.Texto);
        Assert.DoesNotContain("Hamlet", r.Texto);
        Assert.Contains(r.Enlaces, e => e.Url.Contains("SoloGratis=true"));
    }

    [Fact]
    public async Task Las_preguntas_frecuentes_incluyen_enlaces_utiles()
    {
        var r = await Preguntar("no encuentro mis entradas");

        Assert.Contains(r.Enlaces, e => e.Url == "/Entradas");
    }

    [Fact]
    public void Interpreta_fechas_y_precios_del_lenguaje_natural()
    {
        var miercoles = new DateTime(2026, 10, 14);

        var finde = AsistenteService.InterpretarBusqueda("conciertos este fin de semana", miercoles);
        Assert.Equal((1, new DateTime(2026, 10, 17), new DateTime(2026, 10, 18)), (finde.CategoriaId!.Value, finde.Desde!.Value, finde.Hasta!.Value));

        var hoy = AsistenteService.InterpretarBusqueda("algo gratis hoy", miercoles);
        Assert.True(hoy.SoloGratis);
        Assert.Equal(miercoles, hoy.Desde);

        var semana = AsistenteService.InterpretarBusqueda("teatro esta semana en arequipa", miercoles);
        Assert.Equal((2, new DateTime(2026, 10, 18), "arequipa"), (semana.CategoriaId!.Value, semana.Hasta!.Value, semana.Buscar));
    }

    [Fact]
    public void El_redactor_descarta_textos_que_cambian_los_datos()
    {
        var original = "Hamlet es el 20/10 a las 20:00. Entrada: S/ 35.00";

        Assert.True(RedactorOllama.ConservaLosDatos(original, "¡Genial! Hamlet será el 20/10 a las 20:00 y la entrada cuesta S/ 35.00."));
        Assert.False(RedactorOllama.ConservaLosDatos(original, "Hamlet será el 20/10 a las 21:00 y cuesta S/ 35.00."));
    }
}

// =============================
// 🎯 HU17: RECOMENDACIONES (ML.NET, filtrado por contenido)
// =============================
public class RecomendacionesTests : BaseSprint6
{
    public RecomendacionesTests(ModeloCompartido modelo) : base(modelo) { }

    private void Vender(Evento evento, int cantidad)
    {
        var tipo = Db.TiposEntrada.First(t => t.EventoId == evento.Id);
        tipo.Vendidas = cantidad;
        Db.SaveChanges();
    }

    // Criterio 2: un usuario nuevo sin historial recibe los eventos más populares cercanos
    [Fact]
    public async Task Un_usuario_nuevo_recibe_los_eventos_mas_populares_cercanos()
    {
        var popularCerca = Crear("Festival de Lima", 3, dias: 20, -12.06, -77.03, ("General", 30m, 100));
        var pocoVendidoCerca = Crear("Expo de Lima", 5, dias: 20, -12.07, -77.04, ("General", 30m, 100));
        Vender(popularCerca, 90);
        Vender(Rock, 100); // muy popular, pero en Arequipa

        var r = await Recomendaciones.RecomendarAsync("nuevo", new Ubicacion(-12.06, -77.03));

        Assert.Equal(popularCerca.Id, r[0].Id);
        // Lo cercano gana a lo lejano aunque lo lejano esté agotado
        Assert.True(r.FindIndex(e => e.Id == pocoVendidoCerca.Id) < r.FindIndex(e => e.Id == Rock.Id));
        Assert.Contains("km de ti", r[0].Motivo);
    }

    [Fact]
    public async Task Sin_historial_ni_ubicacion_recomienda_los_mas_populares()
    {
        Vender(Hamlet, 180); // 90 % vendido

        var r = await Recomendaciones.RecomendarAsync(null, null);

        Assert.Equal(Hamlet.Id, r[0].Id);
        Assert.Equal("Popular: 90 % vendido", r[0].Motivo.Replace(" ", " "));
    }

    // Criterio 1: se muestran hasta 6 eventos
    [Fact]
    public async Task Se_muestran_como_maximo_seis_eventos()
    {
        for (var i = 0; i < 8; i++)
            Crear($"Evento extra {i}", 1, dias: 3 + i, -16.4, -71.5, ("General", 10m, 50));

        Assert.Equal(6, (await Recomendaciones.RecomendarAsync(null, null)).Count);
        Assert.Equal(6, (await Recomendaciones.RecomendarAsync(null, null, maximo: 50)).Count);
    }

    // ML.NET: lo que el usuario vio acerca eventos con contenido parecido, aunque no comparta el título
    [Fact]
    public async Task Recomienda_eventos_de_contenido_parecido_a_lo_que_vio()
    {
        var obra = Crear("Romeo y Julieta", 2, dias: 25, -16.41, -71.53, ("Platea", 40m, 100));
        await Recomendaciones.RegistrarVistaAsync("ana", Hamlet.Id);

        var r = await Recomendaciones.RecomendarAsync("ana", null);

        Assert.Equal(obra.Id, r.First(e => e.Id != Hamlet.Id).Id);
        Assert.Equal("Parecido a «Hamlet»", r.First(e => e.Id == obra.Id).Motivo);
    }

    [Fact]
    public async Task Las_busquedas_de_texto_acercan_eventos_con_esas_palabras()
    {
        await Recomendaciones.RegistrarBusquedaAsync("ana", null, "Rock");

        var r = await Recomendaciones.RecomendarAsync("ana", null);

        Assert.Equal(Rock.Id, r[0].Id);
        Assert.Equal("Parecido a «rock»", r[0].Motivo);
    }

    [Fact]
    public async Task La_busqueda_por_categoria_se_guarda_como_termino_en_el_historial()
    {
        await Recomendaciones.RegistrarBusquedaAsync("ana", 2, "  ");

        Assert.Equal(new[] { "teatro" }, Db.HistorialBusquedas.Where(h => h.UsuarioId == "ana").Select(h => h.Termino));

        var r = await Recomendaciones.RecomendarAsync("ana", null);
        Assert.Equal(Hamlet.Id, r[0].Id);
    }

    [Fact]
    public async Task No_recomienda_eventos_ya_comprados_ni_los_propios()
    {
        await ComprarAsync("ana", Hamlet.Id, new() { [Db.TiposEntrada.Single(t => t.EventoId == Hamlet.Id).Id] = 1 });

        var paraAna = await Recomendaciones.RecomendarAsync("ana", null);
        var paraOrganizador = await Recomendaciones.RecomendarAsync("org", null);

        Assert.DoesNotContain(paraAna, e => e.Id == Hamlet.Id);
        Assert.Empty(paraOrganizador);
    }

    [Fact]
    public async Task Recargar_la_ficha_no_infla_el_interes_y_el_historial_se_puede_borrar()
    {
        for (var i = 0; i < 5; i++)
            await Recomendaciones.RegistrarVistaAsync("ana", Rock.Id);
        await Recomendaciones.RegistrarBusquedaAsync("ana", null, "jazz");

        Assert.Equal(1, Db.VisualizacionesEvento.Count(v => v.UsuarioId == "ana"));
        Assert.Equal(2, await Recomendaciones.ContarHistorialAsync("ana"));
        Assert.Equal(2, await Recomendaciones.BorrarHistorialAsync("ana"));
        Assert.Empty(Db.VisualizacionesEvento);
        Assert.Empty(Db.HistorialBusquedas);
    }

    [Fact]
    public async Task Con_ubicacion_muestra_la_distancia()
    {
        var r = await Recomendaciones.RecomendarAsync(null, new Ubicacion(-12.06, -77.03));

        Assert.True(r.Single(e => e.Id == Feria.Id).DistanciaKm < 3);
        Assert.True(r.Single(e => e.Id == Rock.Id).DistanciaKm > 500);
    }

    [Theory]
    [InlineData(-12.0464, -77.0428, -16.3989, -71.5369, 765, 775)] // Lima – Arequipa (~770 km)
    [InlineData(-16.40, -71.54, -16.40, -71.54, 0, 0.001)]
    public void Calcula_distancias_reales(double lat1, double lon1, double lat2, double lon2, double min, double max)
    {
        var d = RecomendacionService.DistanciaKm(lat1, lon1, lat2, lon2);
        Assert.InRange(d, min, max);
    }
}
