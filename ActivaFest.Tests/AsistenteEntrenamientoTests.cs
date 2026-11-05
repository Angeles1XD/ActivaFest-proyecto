using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using ActivaFest.Services.Asistente;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🏷️ NOMBRES DE EVENTOS DENTRO DE UNA FRASE
// =============================
public class ReconocedorEventosTests
{
    private static readonly EventoCandidato[] Eventos =
    {
        new(1, "Cumbia Peruana"), new(2, "Gran Noche de Salsa Brava en Lima"), new(3, "Rock en Español 2026"),
        new(4, "Feria del Libro"), new(5, "Feria Gastronómica"), new(6, "Festival de la Zona Norte")
    };

    private static EventoReconocido Reconocer(string frase) => ReconocedorEventos.Reconocer(BaseConocimiento.Normalizar(frase), Eventos);

    [Theory]
    [InlineData("Dime cuánto he vendido en el evento de cumbia peruana", 1)]
    [InlineData("cuanto vendi en cumbia peruana", 1)]
    [InlineData("¿Cómo van las ventas del evento Cumbia Peruana?", 1)]
    [InlineData("gráfico de ventas de cumbia peruana este mes", 1)]
    [InlineData("ventas del evento cunbia peruana", 1)]
    [InlineData("ventas del evento cumbias peruanas", 1)]
    [InlineData("el evento llamado salsa brava", 2)]
    [InlineData("gráfico de barras del evento salsa brava por zona", 2)]
    [InlineData("Resérvame 2 entradas para rock en español", 3)]
    [InlineData("quiero comprar entradas para el evento feria del libro", 4)]
    [InlineData("Ventas del evento Festival de la Zona Norte", 6)]
    public void Encuentra_el_evento_por_su_nombre(string frase, int id)
    {
        Assert.Equal(id, Reconocer(frase).Encontrado?.Id);
    }

    [Theory]
    [InlineData("¿Cuántos eventos hay en total?")]
    [InlineData("¿Cuál es el evento más vendido?")]
    [InlineData("¿Qué evento vendió más la semana pasada?")]
    [InlineData("Gráfico del evento con más ventas")]
    [InlineData("¿Qué comisiones dejó cada evento?")]
    [InlineData("Dime los eventos que ya he aprobado")]
    [InlineData("Ventas de este mes")]
    [InlineData("Gráfico de pastel de ingresos por categoría")]
    [InlineData("Gráfico de radar de las ventas")]
    public void La_palabra_evento_sola_no_es_un_nombre(string frase)
    {
        var r = Reconocer(frase);

        Assert.Null(r.Encontrado);
        Assert.Null(r.NoEncontrado);
        Assert.False(r.Ambiguo);
    }

    [Fact]
    public void Si_el_nombre_no_existe_lo_dice_en_vez_de_inventar()
    {
        Assert.Equal("merengue caribeno", Reconocer("cuánto vendí en el evento de merengue caribeño").NoEncontrado);
    }

    [Fact]
    public void Con_dos_eventos_posibles_pregunta_cual()
    {
        var r = Reconocer("ventas del evento feria");

        Assert.True(r.Ambiguo);
        Assert.Equal(new[] { 4, 5 }, r.Opciones.Select(e => e.Id).OrderBy(i => i));
    }

    [Fact]
    public void El_nombre_se_quita_de_la_frase_para_no_confundirlo_con_la_pregunta()
    {
        var r = Reconocer("gráfico de ventas del evento Festival de la Zona Norte");

        Assert.Equal(6, r.Encontrado?.Id);
        Assert.DoesNotContain("zona", r.TextoSinEvento);
    }
}

// =============================
// 🧠 BANCO DE FRASES POR ROL
// =============================
public class FrasesPorRolTests
{
    private static readonly DateTime Hoy = new(2026, 11, 4);

    private static string Intencion(string frase) => frase switch
    {
        _ when frase.Contains("carrito") => Intenciones.MiCarrito,
        _ when frase.Contains("mis ventas") => Intenciones.VentasOrganizador,
        _ when frase.Contains("mis eventos") => Intenciones.EstadoMisEventos,
        _ => Intenciones.Desconocida
    };

    [Theory]
    [InlineData("Genera un gráfico de ventas")]
    [InlineData("Hazme un diagrama de los eventos")]
    [InlineData("Muéstrame un gráfico de mis ventas")]
    [InlineData("Quiero un gráfico de mis eventos")]
    [InlineData("Dibújame las ventas en barras")]
    [InlineData("Grafica los ingresos")]
    [InlineData("Dame estadísticas de ventas")]
    [InlineData("Un histograma de entradas")]
    [InlineData("Visualiza las comisiones")]
    public void Todo_pedido_de_grafico_del_administrador_va_a_la_analitica(string frase)
    {
        var t = BaseConocimiento.Normalizar(frase);

        Assert.Equal(AlcanceConsulta.Analitica, AlcanceChat.Clasificar(t, Intencion(t), Roles.Administrador, Hoy));
        Assert.NotNull(AsistenteAnaliticoService.Interpretar(t, Hoy));
    }

    [Theory]
    [InlineData("Hazme un gráfico de mis ventas")]
    [InlineData("Gráfico de pastel de mis eventos")]
    [InlineData("Diagrama de barras de lo que vendí")]
    [InlineData("Quiero una gráfica de ingresos por categoría")]
    public void El_organizador_recibe_graficos_de_lo_suyo(string frase)
    {
        var t = BaseConocimiento.Normalizar(frase);

        Assert.Equal(AlcanceConsulta.AnaliticaPropia, AlcanceChat.Clasificar(t, Intencion(t), Roles.Organizador, Hoy));
    }

    [Theory]
    [InlineData("Gráfico de los eventos de toda la plataforma")]
    [InlineData("Gráfico de usuarios registrados")]
    public void El_organizador_no_grafica_la_plataforma(string frase)
    {
        var t = BaseConocimiento.Normalizar(frase);

        Assert.Equal(AlcanceConsulta.SoloAdministrador, AlcanceChat.Clasificar(t, Intencion(t), Roles.Organizador, Hoy));
    }

    [Theory]
    [InlineData("Dime los eventos que ya he aprobado", EstadosListado.Aprobados)]
    [InlineData("¿Cuáles eventos aprobé?", EstadosListado.Aprobados)]
    [InlineData("Lista de eventos aprobados", EstadosListado.Aprobados)]
    [InlineData("¿Qué eventos faltan aprobar?", EstadosListado.Pendientes)]
    [InlineData("Muéstrame los eventos pendientes", EstadosListado.Pendientes)]
    [InlineData("Eventos que he rechazado", EstadosListado.Rechazados)]
    [InlineData("Dime los eventos cancelados", EstadosListado.Cancelados)]
    [InlineData("¿Qué eventos ya se realizados?", EstadosListado.Realizados)]
    [InlineData("Lista los eventos publicados", EstadosListado.Publicados)]
    [InlineData("Muéstrame los eventos", null)]
    public void Entiende_los_eventos_que_se_quieren_listar(string frase, string? estado)
    {
        var t = BaseConocimiento.Normalizar(frase);
        var c = AsistenteAnaliticoService.Interpretar(t, Hoy);

        Assert.Equal("ListaDeEventos", c?.Funcion);
        Assert.Equal(estado, c?.Estado);
        Assert.False(AsistenteAnaliticoService.PideModificar(t) && !AsistenteAnaliticoService.PideListaDeEventos(t));
    }

    [Theory]
    [InlineData("Gráfico de área de los ingresos", "area")]
    [InlineData("Gráfico de radar de las ventas", "radar")]
    [InlineData("Gráfico polar de ingresos por categoría", "polarArea")]
    [InlineData("Barras apiladas de ventas por mes", "stacked")]
    [InlineData("Gráfico de dispersión de ventas", "scatter")]
    [InlineData("Gráfico de burbujas de ventas", "bubble")]
    [InlineData("Gráfico de barras horizontales de eventos", "barh")]
    [InlineData("Gráfico de dona de compradores", "doughnut")]
    public void Entiende_cada_tipo_de_grafico(string frase, string tipo)
    {
        var t = BaseConocimiento.Normalizar(frase);

        Assert.True(AsistenteAnaliticoService.PideGrafico(t));
        Assert.Equal(tipo, AsistenteAnaliticoService.TipoGraficoPedido(t));
    }

    [Theory]
    [InlineData("Ventas por hora", "VentasPorHora")]
    [InlineData("¿A qué hora se compra más?", "VentasPorHora")]
    [InlineData("Ventas por semana", "VentasPorSemana")]
    [InlineData("Ventas diarias de este mes", "VentasPorFecha")]
    [InlineData("Gráfico de área de los ingresos acumulados", "VentasAcumuladas")]
    public void Entiende_las_nuevas_series_de_tiempo(string frase, string funcion)
    {
        Assert.Equal(funcion, AsistenteAnaliticoService.Interpretar(BaseConocimiento.Normalizar(frase), Hoy)?.Funcion);
    }

    [Theory]
    [InlineData("Feria de la Torta")]
    [InlineData("Noche de Pastel y Café")]
    public void Un_titulo_con_palabras_de_graficos_no_es_un_grafico(string titulo)
    {
        Assert.False(AsistenteAnaliticoService.PideGrafico(BaseConocimiento.Normalizar($"¿A qué hora empieza {titulo.Replace(" de ", " ")}?")));
    }
}

// =============================
// 📊 ALCANCE DE LOS DATOS Y GRÁFICOS POR ROL (servicio)
// =============================
public class AlcanceDeDatosTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly AnaliticaService _analitica;
    private readonly AsistenteAnaliticoService _ia;

    public AlcanceDeDatosTests()
    {
        _analitica = new AnaliticaService(_e.Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        _ia = new AsistenteAnaliticoService(_analitica, Options.Create(new AsistenteOptions { UsarLlm = false }), NullLogger<AsistenteAnaliticoService>.Instance);
    }

    public void Dispose() => _e.Dispose();

    // "org" publicó Cumbia Peruana y "beto" publicó Rock Ajeno
    private (Evento Propio, Evento Ajeno) DosOrganizadores()
    {
        var propio = _e.Evento("Cumbia Peruana", 1, DateTime.Today.AddDays(30), 100, 50);
        var ajeno = _e.Evento("Rock Ajeno", 2, DateTime.Today.AddDays(30), 100, 200);
        ajeno.OrganizadorId = "beto";
        _e.Db.SaveChanges();
        _e.Compra("ana", propio, 2, DateTime.Today.AddHours(9));
        _e.Compra("carla", ajeno, 3, DateTime.Today.AddHours(10));
        return (propio, ajeno);
    }

    [Fact]
    public async Task El_nombre_del_evento_acota_las_cifras_del_administrador()
    {
        DosOrganizadores();

        var r = await _ia.ConsultarAsync("Dime cuánto he vendido en el evento de cumbia peruana", "org");

        Assert.Equal("ResumenDeEvento", r.Funcion);
        Assert.Contains("«Cumbia Peruana» vendió 2 entradas", r.Texto);
        Assert.Contains("S/ 100.00", r.Texto);
        Assert.DoesNotContain("600", r.Texto);
    }

    [Fact]
    public async Task El_evento_nombrado_incluye_ventas_antiguas_aunque_no_se_diga_el_periodo()
    {
        var evento = _e.Evento("Cumbia Peruana", 1, DateTime.Today.AddDays(30), 100, 50);
        _e.Compra("ana", evento, 4, DateTime.Today.AddDays(-90));

        var r = await _ia.ConsultarAsync("¿Cuánto vendió el evento cumbia peruana?", "org");

        Assert.Contains("vendió 4 entradas", r.Texto);
    }

    [Fact]
    public async Task Un_evento_que_no_existe_se_avisa_sin_dar_otras_cifras()
    {
        DosOrganizadores();

        var r = await _ia.ConsultarAsync("Ventas del evento merengue caribeño", "org");

        Assert.Null(r.Funcion);
        Assert.Contains("No encontré ningún evento llamado «merengue caribeno»", r.Texto);
        Assert.NotEmpty(r.Sugerencias);
    }

    [Fact]
    public async Task El_organizador_solo_grafica_sus_eventos()
    {
        DosOrganizadores();

        var r = await _ia.ConsultarMisEventosAsync("Gráfico de barras de los eventos más vendidos", "org");

        Assert.Equal("bar", r.Grafico!.Tipo);
        Assert.Equal(new[] { "Cumbia Peruana" }, r.Grafico.Etiquetas);
        Assert.DoesNotContain(r.Tabla!.Filas.SelectMany(f => f), c => c.Contains("Rock Ajeno"));
    }

    [Theory]
    [InlineData("Gráfico de ventas del evento Rock Ajeno")]
    [InlineData("Cuánto vendió el evento Rock Ajeno")]
    [InlineData("Gráfico de pastel de las ventas de Rock Ajeno")]
    public async Task El_organizador_no_ve_los_eventos_de_otro(string pregunta)
    {
        DosOrganizadores();

        var r = await _ia.ConsultarMisEventosAsync(pregunta, "org");

        Assert.Null(r.Grafico);
        Assert.Null(r.Tabla);
        Assert.Contains("entre tus eventos", r.Texto);
        Assert.DoesNotContain("600", r.Texto);
    }

    [Theory]
    [InlineData("¿Quiénes son los clientes que más gastaron?")]
    [InlineData("Gráfico de los segmentos de clientes")]
    public async Task El_organizador_no_ve_los_rankings_de_clientes_de_la_plataforma(string pregunta)
    {
        DosOrganizadores();

        var r = await _ia.ConsultarMisEventosAsync(pregunta, "org");

        Assert.True(r.FueraDeAlcance);
        Assert.Null(r.Tabla);
    }

    [Fact]
    public async Task El_analisis_del_organizador_no_trae_datos_de_la_plataforma()
    {
        DosOrganizadores();
        var hoy = DateTime.Today;

        var a = await _analitica.AnalisisAsync(new FiltroAnalitica { Desde = hoy, Hasta = hoy, OrganizadorId = "org" });

        Assert.Equal(100, a.Ingresos);
        Assert.Equal(new[] { "Cumbia Peruana" }, a.TopEventos.Select(e => e.Evento));
        Assert.Equal(1, a.EventosPorEstado.Sum(e => e.Total));
        Assert.Equal(0, a.Acumulado.UsuariosRegistrados);
        Assert.Empty(a.TopCompradores);
        Assert.Empty(a.TopOrganizadores);
        Assert.Empty(a.Segmentos);
        Assert.True(a.ParaOrganizador);
    }

    [Fact]
    public async Task El_filtro_por_evento_acota_todo_el_dashboard()
    {
        var (propio, _) = DosOrganizadores();
        var hoy = DateTime.Today;

        var filtro = new FiltroAnalitica { Desde = hoy, Hasta = hoy, EventoId = propio.Id };
        var d = await _analitica.DashboardAsync(filtro);
        var a = await _analitica.AnalisisAsync(filtro);

        Assert.Equal(100, d.Ingresos);
        Assert.Equal(2, a.Entradas);
        Assert.Equal(1, a.Compradores);
        Assert.Equal(2, a.PorZona.Sum(z => z.Entradas));
        Assert.Equal(100, a.VentasDiarias.Last().Acumulado);
        Assert.Equal(1, a.ComprasPorHora.Sum(h => h.Compras));
    }

    [Fact]
    public async Task La_cache_no_mezcla_los_alcances()
    {
        var (propio, ajeno) = DosOrganizadores();
        var hoy = DateTime.Today;

        var uno = await _analitica.AnalisisAsync(new FiltroAnalitica { Desde = hoy, Hasta = hoy, EventoId = propio.Id });
        var otro = await _analitica.AnalisisAsync(new FiltroAnalitica { Desde = hoy, Hasta = hoy, EventoId = ajeno.Id });
        var todos = await _analitica.AnalisisAsync(new FiltroAnalitica { Desde = hoy, Hasta = hoy });

        Assert.Equal((100m, 600m, 700m), (uno.Ingresos, otro.Ingresos, todos.Ingresos));
    }

    [Fact]
    public async Task Si_el_ultimo_mes_esta_vacio_el_grafico_muestra_el_ultimo_ano()
    {
        var evento = _e.Evento("Cumbia Peruana", 1, DateTime.Today.AddDays(30), 100, 50);
        _e.Compra("ana", evento, 2, DateTime.Today.AddDays(-100));

        var r = await _ia.ConsultarAsync("Gráfico de pastel de ingresos por categoría", "org");

        Assert.Equal("pie", r.Grafico!.Tipo);
        Assert.Equal(new[] { 100d }, r.Grafico.Valores);
        Assert.Contains("te muestro el último año", r.Texto);
    }

    [Fact]
    public async Task Si_pide_un_periodo_concreto_vacio_no_cambia_de_periodo()
    {
        var evento = _e.Evento("Cumbia Peruana", 1, DateTime.Today.AddDays(30), 100, 50);
        _e.Compra("ana", evento, 2, DateTime.Today.AddDays(-100));

        var r = await _ia.ConsultarAsync("Gráfico de pastel de ingresos por categoría de hoy", "org");

        Assert.Null(r.Grafico);
        Assert.Contains("No hay datos para graficar", r.Texto);
    }

    [Fact]
    public async Task Lista_los_eventos_aprobados_con_tabla_y_sin_grafico_si_no_lo_piden()
    {
        DosOrganizadores();
        var pendiente = _e.Evento("Evento Pendiente", 1, DateTime.Today.AddDays(30), 100, 50);
        pendiente.Estado = EstadoEvento.Pendiente;
        _e.Db.SaveChanges();

        var r = await _ia.ConsultarAsync("Dime los eventos que ya he aprobado", "org");

        Assert.Equal("ListaDeEventos", r.Funcion);
        Assert.Contains("Has aprobado 2 eventos", r.Texto);
        Assert.Equal(new[] { "Cumbia Peruana", "Rock Ajeno" }, r.Tabla!.Filas.Select(f => f[0]).OrderBy(x => x));
        Assert.Null(r.Grafico);

        var conGrafico = await _ia.ConsultarAsync("Gráfico de pastel de los eventos aprobados", "org");
        Assert.Equal("pie", conGrafico.Grafico!.Tipo);
    }

    [Fact]
    public async Task Lista_los_eventos_que_faltan_aprobar()
    {
        var pendiente = _e.Evento("Evento Pendiente", 1, DateTime.Today.AddDays(30), 100, 50);
        pendiente.Estado = EstadoEvento.Pendiente;
        _e.Db.SaveChanges();

        var r = await _ia.ConsultarAsync("¿Qué eventos faltan aprobar?", "org");

        Assert.False(r.FueraDeAlcance);
        Assert.Equal("Evento Pendiente", r.Tabla!.Filas.Single()[0]);
    }

    [Fact]
    public async Task Los_tipos_nuevos_de_grafico_llevan_sus_datos()
    {
        DosOrganizadores();

        var apiladas = await _ia.ConsultarAsync("Barras apiladas de ventas por mes", "org");
        Assert.Equal("stacked", apiladas.Grafico!.Tipo);
        Assert.Equal(2, apiladas.Grafico.Series!.Count);

        var area = await _ia.ConsultarAsync("Gráfico de área de los ingresos acumulados", "org");
        Assert.Equal("area", area.Grafico!.Tipo);
        Assert.Equal(700, area.Grafico.Valores.Last());

        var radar = await _ia.ConsultarAsync("Gráfico de radar de las ventas", "org");
        Assert.Equal(("radar", "VentasPorDia"), (radar.Grafico!.Tipo, radar.Funcion));
    }

    [Fact]
    public async Task La_categoria_nombrada_acota_el_grafico()
    {
        DosOrganizadores();

        var r = await _ia.ConsultarAsync("Gráfico de los eventos más vendidos de la categoría Conciertos", "org");

        Assert.Equal(new[] { "Cumbia Peruana" }, r.Grafico!.Etiquetas);
    }
}

// =============================
// 🤖 CONVERSACIONES COMPLETAS POR /api/asistente-ia
// =============================
public class ChatEntrenadoTests : PruebasDeChat, IClassFixture<AppDePrueba>
{
    public ChatEntrenadoTests(AppDePrueba app) : base(app) { }

    private static string[] Sugerencias(JsonElement r) => r.GetProperty("sugerencias").EnumerateArray().Select(s => s.GetString()!).ToArray();

    [Fact]
    public async Task El_grafico_de_mis_ventas_del_administrador_llega_con_grafico()
    {
        var admin = await AdminAsync();
        var r = await PreguntarAsync(admin, "Muéstrame un gráfico de eventos por estado");

        Assert.Equal(Intenciones.ConsultaPlataforma, Intencion(r));
        Assert.NotEqual(JsonValueKind.Null, r.GetProperty("grafico").ValueKind);
        Assert.StartsWith("📊 Aquí tienes el gráfico", Texto(r));
    }

    [Fact]
    public async Task Reservar_pregunta_el_evento_la_entrada_y_no_hace_nada_sin_confirmar()
    {
        var (ana, id) = await UsuarioAsync();
        var titulo = Titulo("Conversado");
        var evento = Evento(titulo);

        var paso1 = await PreguntarAsync(ana, "Quiero reservar");
        Assert.Contains("¿Para qué evento", Texto(paso1));

        var paso2 = await PreguntarAsync(ana, titulo, paso1.GetProperty("pendiente"));
        Assert.Contains("¿Qué tipo de entrada", Texto(paso2));
        Assert.Contains(Sugerencias(paso2), s => s.StartsWith("VIP"));

        var paso3 = await PreguntarAsync(ana, Sugerencias(paso2).First(s => s.StartsWith("VIP")), paso2.GetProperty("pendiente"));
        Assert.Contains("Confirma tu reserva", Texto(paso3));
        Assert.Contains("Total: S/ 120.00", Texto(paso3));
        Assert.False(Con(db => db.Ordenes.Any(o => o.UsuarioId == id)));

        var hecho = await PreguntarAsync(ana, "✅ Sí, reservar", paso3.GetProperty("pendiente"));
        Assert.Contains("Reservé 1 entrada VIP", Texto(hecho));
        Assert.Equal(JsonValueKind.Null, hecho.GetProperty("pendiente").ValueKind);
        Assert.True(Con(db => db.Ordenes.Any(o => o.UsuarioId == id && o.EventoId == evento.Id && o.Estado == EstadoOrden.PendientePago)));
    }

    [Fact]
    public async Task Con_todo_en_una_frase_basta_confirmar()
    {
        var (ana, _) = await UsuarioAsync();
        var titulo = Titulo("Directo");
        Evento(titulo);

        var resumen = await PreguntarAsync(ana, $"Resérvame 2 entradas VIP para el evento {titulo}");

        Assert.Contains("2 × VIP", Texto(resumen));
        Assert.Contains("Total: S/ 240.00", Texto(resumen));
    }

    [Fact]
    public async Task Comprar_lleva_a_la_pasarela_solo_despues_de_confirmar()
    {
        var (ana, id) = await UsuarioAsync();
        var titulo = Titulo("Comprado");
        Evento(titulo);

        var resumen = await PreguntarAsync(ana, $"Quiero comprar una entrada VIP para {titulo}");
        Assert.Contains("Confirma tu compra", Texto(resumen));
        Assert.Equal(JsonValueKind.Null, resumen.GetProperty("redirigir").ValueKind);

        var hecho = await PreguntarAsync(ana, "💳 Sí, ir a pagar", resumen.GetProperty("pendiente"));
        var codigo = Con(db => db.Ordenes.Single(o => o.UsuarioId == id).Codigo);
        Assert.Equal($"/Compras/Checkout?codigo={codigo}", hecho.GetProperty("redirigir").GetString());
    }

}

// El chat limita los mensajes por minuto: cada clase tiene su propia app
public class ChatEntrenadoCambiosTests : PruebasDeChat, IClassFixture<AppDePrueba>
{
    public ChatEntrenadoCambiosTests(AppDePrueba app) : base(app) { }

    [Theory]
    [InlineData("Cancelar")]
    [InlineData("no")]
    [InlineData("mejor no")]
    [InlineData("no gracias")]
    public async Task Cancelar_no_reserva_nada(string respuesta)
    {
        var (ana, id) = await UsuarioAsync();
        var titulo = Titulo("Cancelado");
        Evento(titulo);

        var resumen = await PreguntarAsync(ana, $"Resérvame una entrada VIP para {titulo}");
        var r = await PreguntarAsync(ana, respuesta, resumen.GetProperty("pendiente"));

        Assert.Contains("no reservé nada", Texto(r));
        Assert.False(Con(db => db.Ordenes.Any(o => o.UsuarioId == id)));
    }

    [Fact]
    public async Task Antes_de_confirmar_se_puede_cambiar_la_cantidad_y_la_zona()
    {
        var (ana, _) = await UsuarioAsync();
        var titulo = Titulo("Cambiado");
        Evento(titulo);

        var resumen = await PreguntarAsync(ana, $"Resérvame una entrada VIP para {titulo}");
        var cambio = await PreguntarAsync(ana, "mejor 3", resumen.GetProperty("pendiente"));

        Assert.Contains("3 × VIP", Texto(cambio));
    }

    [Fact]
    public async Task Sin_el_si_del_usuario_el_resumen_no_se_ejecuta()
    {
        var (ana, id) = await UsuarioAsync();
        var titulo = Titulo("Dudoso");
        Evento(titulo);

        var resumen = await PreguntarAsync(ana, $"Resérvame una entrada VIP para {titulo}");
        var r = await PreguntarAsync(ana, "¿y dónde queda?", resumen.GetProperty("pendiente"));

        Assert.DoesNotContain("Reservé", Texto(r));
        Assert.False(Con(db => db.Ordenes.Any(o => o.UsuarioId == id)));
    }

    [Fact]
    public async Task Un_evento_que_no_existe_no_rompe_la_conversacion()
    {
        var (ana, _) = await UsuarioAsync();

        var r = await PreguntarAsync(ana, "Resérvame 2 entradas para el evento Merengue Inexistente");

        Assert.Contains("No encontré", Texto(r));
        Assert.NotEqual(JsonValueKind.Null, r.GetProperty("pendiente").ValueKind);
    }
}
