using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using ActivaFest.Services.Asistente;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🤖 HU24: BANCO DE 20 PREGUNTAS ANALÍTICAS (OE6) Y PREGUNTAS FUERA DE ALCANCE
// =============================
public class HU24InterpreteTests
{
    private static readonly DateTime Hoy = new(2026, 10, 18);

    // Pregunta → función esperada (y, cuando importa, período o parámetros)
    public static readonly TheoryData<string, string, string?, int?, int?, double?> Banco = new()
    {
        { "¿Cuáles fueron los 5 eventos más vendidos en noviembre?", "ObtenerTopEventos", "2025-11-01", 5, null, null },
        { "Top 3 eventos con más entradas este mes", "ObtenerTopEventos", "2026-10-01", 3, null, null },
        { "¿Qué evento vendió más la semana pasada?", "ObtenerTopEventos", "2026-10-05", null, null, null },
        { "¿Qué categoría generó más ingresos este mes?", "IngresosPorCategoria", "2026-10-01", null, null, null },
        { "Ingresos por categoría del año pasado", "IngresosPorCategoria", "2025-01-01", null, null, null },
        { "¿Qué eventos tienen la demanda más inestable?", "VariabilidadPorEvento", null, null, null, null },
        { "Muéstrame la variabilidad de las ventas de los últimos 3 meses", "VariabilidadPorEvento", "2026-07-19", null, null, null },
        { "¿Cuántos clientes VIP hay y cuánto gastan en promedio?", "ResumenSegmentos", null, null, null, null },
        { "¿Cuántos clientes en riesgo tenemos?", "ResumenSegmentos", null, null, null, null },
        { "Resumen de los segmentos RFM", "ResumenSegmentos", null, null, null, null },
        { "¿Quiénes son los 10 clientes que más gastaron este año?", "TopClientes", "2026-01-01", 10, null, null },
        { "Top 5 clientes del mes pasado", "TopClientes", "2026-09-01", 5, null, null },
        { "¿Qué eventos de la próxima semana tienen menos del 40 % de ocupación?", "EventosBajaOcupacion", null, null, 7, 0.40 },
        { "Eventos con baja venta en los próximos 15 días", "EventosBajaOcupacion", null, null, 15, 0.40 },
        { "¿Qué eventos tienen ocupación por debajo del 25 por ciento?", "EventosBajaOcupacion", null, null, 30, 0.25 },
        { "¿Cuánto vendimos este mes?", "ResumenVentas", "2026-10-01", null, null, null },
        { "¿Cuáles fueron los ingresos de septiembre?", "ResumenVentas", "2026-09-01", null, null, null },
        { "¿Cuánto cobró la plataforma en comisiones el último mes?", "ResumenVentas", "2026-09-19", null, null, null },
        { "¿Cuántas entradas se vendieron hoy?", "ResumenVentas", "2026-10-18", null, null, null },
        { "Ticket promedio de los últimos 7 días", "ResumenVentas", "2026-10-12", null, null, null }
    };

    [Theory]
    [MemberData(nameof(Banco))]
    public void Cada_pregunta_del_banco_elige_la_funcion_y_los_parametros_correctos(string pregunta, string funcion, string? desde, int? n, int? dias, double? umbral)
    {
        var c = AsistenteAnaliticoService.Interpretar(BaseConocimiento.Normalizar(pregunta), Hoy);

        Assert.NotNull(c);
        Assert.Equal(funcion, c!.Funcion);
        if (desde != null) Assert.Equal(DateTime.Parse(desde), c.Periodo.Desde);
        if (n != null) Assert.Equal(n, c.N);
        if (dias != null) Assert.Equal(dias, c.Dias);
        if (umbral != null) Assert.Equal(umbral.Value, c.Umbral, 3);
    }

    // Consultas alineadas con los gráficos del dashboard
    [Theory]
    [InlineData("¿Qué me recomiendas para vender más este mes?", "Recomendaciones")]
    [InlineData("Dame recomendaciones", "Recomendaciones")]
    [InlineData("¿Qué debería hacer para mejorar?", "Recomendaciones")]
    [InlineData("Dame consejos para los eventos con baja ocupación", "Recomendaciones")]
    [InlineData("¿Cuáles son los mejores eventos destacados?", "EventosDestacados")]
    [InlineData("¿Cómo rindieron los paquetes de publicidad?", "EventosDestacados")]
    [InlineData("¿Cuántos usuarios han comprado entradas?", "Compradores")]
    [InlineData("¿Cuántos compradores recurrentes hay?", "Compradores")]
    [InlineData("¿Qué usuarios compraron más?", "Compradores")]
    [InlineData("¿Quiénes son los mejores compradores?", "TopClientes")]
    [InlineData("¿Qué comisiones dejó cada evento?", "ComisionesPorEvento")]
    [InlineData("¿Qué eventos tienen más ventas?", "ObtenerTopEventos")]
    [InlineData("¿Qué día se vende más?", "VentasPorDia")]
    [InlineData("¿Cómo han evolucionado las ventas por mes?", "VentasPorMes")]
    [InlineData("¿Cuánto se ha vendido en total?", "TotalesPlataforma")]
    public void Entiende_las_consultas_del_analisis(string pregunta, string funcion)
    {
        Assert.Equal(funcion, AsistenteAnaliticoService.Interpretar(BaseConocimiento.Normalizar(pregunta), Hoy)?.Funcion);
    }

    // El administrador pide el gráfico que quiere: qué datos y de qué tipo
    [Theory]
    [InlineData("Gráfico de pastel de ingresos por categoría", "IngresosPorCategoria", "pie")]
    [InlineData("Hazme un gráfico de torta de las ventas", "IngresosPorCategoria", "pie")]
    [InlineData("Quiero un gráfico de barras de las ventas por mes", "VentasPorMes", "bar")]
    [InlineData("Gráfico de líneas de los ingresos", "VentasPorMes", "line")]
    [InlineData("Gráfico de dona de compradores", "Compradores", "doughnut")]
    [InlineData("Gráfico de barras horizontales de los eventos más vendidos", "ObtenerTopEventos", "barh")]
    [InlineData("Gráfico de pastel de comisiones por categoría", "ComisionesPorCategoria", "pie")]
    [InlineData("Gráfico de barras de entradas por zona", "EntradasPorZona", "bar")]
    [InlineData("Gráfico de eventos por estado", "EventosDePlataforma", null)]
    [InlineData("Gráfico de barras de las ventas por día", "VentasPorDia", "bar")]
    public void Entiende_el_grafico_que_se_pide(string pregunta, string funcion, string? tipo)
    {
        var t = BaseConocimiento.Normalizar(pregunta);

        Assert.True(AsistenteAnaliticoService.PideGrafico(t));
        Assert.Equal(funcion, AsistenteAnaliticoService.Interpretar(t, Hoy)?.Funcion);
        Assert.Equal(tipo, AsistenteAnaliticoService.TipoGraficoPedido(t));
    }

    [Theory]
    [InlineData("¿Cuántos eventos hay en total?", "EventosDePlataforma")]
    [InlineData("¿Cuántos eventos publicados hay?", "EventosDePlataforma")]
    [InlineData("¿Cuántos eventos tienen baja ocupación?", "EventosBajaOcupacion")]
    [InlineData("¿Cómo terminaron las reservas?", "OrdenesPorEstado")]
    [InlineData("Dame un resumen de la plataforma", "TotalesPlataforma")]
    [InlineData("¿Cuántos usuarios hay registrados?", "TotalesPlataforma")]
    [InlineData("¿Cuántos eventos hay en toda la página?", "EventosDePlataforma")]
    public void Distingue_las_consultas_de_eventos_y_reservas(string pregunta, string funcion)
    {
        Assert.Equal(funcion, AsistenteAnaliticoService.Interpretar(BaseConocimiento.Normalizar(pregunta), Hoy)?.Funcion);
    }

    [Fact]
    public void Los_botones_de_los_graficos_preguntan_por_el_periodo_exacto_del_dashboard()
    {
        var p = AsistenteAnaliticoService.PeriodoMencionado(BaseConocimiento.Normalizar("¿Qué día se vende más? del 01/11/2026 al 04/11/2026"), Hoy);
        Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 4)), (p.Desde, p.Hasta));

        var invalido = AsistenteAnaliticoService.PeriodoMencionado(BaseConocimiento.Normalizar("ventas del 31/02/2026 al 04/11/2026"), Hoy);
        Assert.NotEqual(new DateTime(2026, 11, 4), invalido.Hasta);
    }

    [Fact]
    public void Resuelve_al_menos_el_80_por_ciento_del_banco_OE6()
    {
        var aciertos = Banco.Count(fila => AsistenteAnaliticoService.Interpretar(BaseConocimiento.Normalizar((string)fila[0]), Hoy)?.Funcion == (string)fila[1]);
        Assert.Equal(20, Banco.Count());
        Assert.True(aciertos >= 16, $"Aciertos: {aciertos} de 20");
    }

    [Fact]
    public void Los_periodos_se_muestran_con_sus_fechas()
    {
        var p = AsistenteAnaliticoService.PeriodoMencionado("ventas de noviembre", Hoy);
        Assert.Equal((new DateTime(2025, 11, 1), new DateTime(2025, 11, 30), "de noviembre de 2025"), (p.Desde, p.Hasta, p.Etiqueta));
        Assert.Equal("de los últimos 30 días", AsistenteAnaliticoService.PeriodoMencionado("ventas", Hoy).Etiqueta);
        Assert.Equal(new DateTime(2026, 10, 18), AsistenteAnaliticoService.PeriodoMencionado("este mes", Hoy).Hasta);
    }

    [Theory]
    [InlineData("¿Quién ganará el partido del domingo?")]
    [InlineData("Cuéntame un chiste")]
    public void Lo_que_no_es_de_los_datos_queda_fuera_de_alcance(string pregunta) =>
        Assert.Null(AsistenteAnaliticoService.Interpretar(BaseConocimiento.Normalizar(pregunta), Hoy));

    [Theory]
    [InlineData("Dame el correo del cliente que más gastó")]
    [InlineData("¿Cómo se llaman los clientes VIP?")]
    [InlineData("Pásame el teléfono de los mejores clientes")]
    public void Nunca_entrega_datos_personales(string pregunta) =>
        Assert.True(AsistenteAnaliticoService.PideDatosPersonales(BaseConocimiento.Normalizar(pregunta)));

    [Fact]
    public void Si_la_IA_redacta_una_cifra_que_no_esta_en_los_datos_se_descarta()
    {
        var fuente = new RespuestaAnalitica
        {
            Texto = "El evento más vendido fue «Hamlet» con 120 entradas (S/ 4,800.00).",
            Periodo = "de noviembre de 2025",
            Tabla = new TablaCifras { Filas = { new() { "1", "Hamlet", "120", "S/ 4,800.00" } } }
        };
        Assert.True(AsistenteAnaliticoService.SoloCifrasFuente("Hamlet lideró noviembre de 2025 con 120 entradas y S/ 4,800.00.", fuente));
        Assert.False(AsistenteAnaliticoService.SoloCifrasFuente("Hamlet vendió 150 entradas.", fuente));
    }
}

// =============================
// 🔎 HU24: las funciones de solo lectura devuelven las cifras de la BD (y no datos personales)
// =============================
public class HU24FuncionesTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly AnaliticaService _analitica;
    private readonly AsistenteAnaliticoService _ia;

    public HU24FuncionesTests()
    {
        _analitica = new AnaliticaService(_e.Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        _ia = new AsistenteAnaliticoService(_analitica, Options.Create(new AsistenteOptions { UsarLlm = false }), NullLogger<AsistenteAnaliticoService>.Instance);
    }

    public void Dispose() => _e.Dispose();

    [Fact]
    public async Task La_variabilidad_reproduce_el_ejemplo_del_documento()
    {
        // 120, 40, 200 y 60 entradas en cuatro semanas: μ = 105, σ ≈ 71,8 y CV ≈ 68 % (demanda inestable)
        var concierto = _e.Evento("Concierto inestable", 1, new DateTime(2026, 12, 20, 20, 0, 0), 1000, 10);
        var semanas = new[] { 120, 40, 200, 60 };
        for (var s = 0; s < 4; s++)
            _e.Compra("ana", concierto, semanas[s], _e.Desde.AddDays(7 * s + 2).AddHours(12));
        var estable = _e.Evento("Obra estable", 2, new DateTime(2026, 12, 21, 20, 0, 0), 1000, 10);
        for (var s = 0; s < 4; s++)
            _e.Compra("beto", estable, 50 + s, _e.Desde.AddDays(7 * s + 3).AddHours(12));

        var lista = await _analitica.VariabilidadPorEventoAsync(_e.Desde, new DateTime(2026, 11, 28));

        var v = lista[0];
        Assert.Equal("Concierto inestable", v.Evento);
        Assert.Equal(semanas, v.Semanas);
        Assert.Equal(105, v.Media, 2);
        Assert.Equal(71.88, v.Desviacion, 2);
        Assert.Equal(68.5, v.Cv!.Value, 1);
        Assert.Equal("Inestable", v.Nivel);
        Assert.Equal("Estable", lista[1].Nivel);
    }

    [Fact]
    public async Task Top_de_eventos_e_ingresos_por_categoria_coinciden_con_la_consulta_de_control()
    {
        var a = _e.Evento("Rock", 1, new DateTime(2026, 12, 1), 100, 50);
        var b = _e.Evento("Teatro", 2, new DateTime(2026, 12, 2), 100, 30);
        _e.Compra("ana", a, 3, _e.Desde.AddDays(1));
        _e.Compra("beto", b, 5, _e.Desde.AddDays(2));
        _e.Compra("carla", a, 1, _e.Desde.AddDays(3));
        _e.Compra("carla", a, 9, _e.Desde.AddDays(3), estado: EstadoOrden.PendientePago);   // no pagada: no cuenta

        var top = await _analitica.TopEventosAsync(_e.Desde, _e.Hasta, 5);
        Assert.Equal(new[] { ("Teatro", 5, 150m), ("Rock", 4, 200m) }, top.Select(t => (t.Evento, t.Entradas, t.Ingresos)));

        var cats = await _analitica.IngresosPorCategoriaAsync(_e.Desde, _e.Hasta);
        var control = _e.Db.Ordenes.Where(o => o.Estado == EstadoOrden.Pagada).ToList().Sum(o => o.Total);
        Assert.Equal(control, cats.Sum(c => c.Ingresos));
        Assert.Equal(200m, cats[0].Ingresos);
    }

    [Fact]
    public async Task Detecta_los_proximos_eventos_con_baja_ocupacion()
    {
        var lleno = _e.Evento("Casi lleno", 1, DateTime.Now.AddDays(3), 10, 20);
        var vacio = _e.Evento("Casi vacío", 1, DateTime.Now.AddDays(4), 10, 20);
        _e.Evento("Más adelante", 1, DateTime.Now.AddDays(60), 10, 20);
        _e.Compra("ana", lleno, 8, DateTime.Now.AddDays(-1));
        _e.Compra("beto", vacio, 1, DateTime.Now.AddDays(-1));

        var bajos = await _analitica.EventosBajaOcupacionAsync(7, 0.4);

        var e = Assert.Single(bajos);
        Assert.Equal(("Casi vacío", 1, 10), (e.Evento, e.Vendidas, e.Aforo));
    }

    [Fact]
    public async Task La_respuesta_trae_cifras_fuente_periodo_y_queda_en_la_auditoria()
    {
        var a = _e.Evento("Festival", 1, DateTime.Now.AddDays(30), 100, 40);
        _e.Compra("ana", a, 2, DateTime.Now.AddDays(-2));

        var r = await _ia.ConsultarAsync("¿Cuáles fueron los 5 eventos más vendidos este mes?", "org");

        Assert.False(r.FueraDeAlcance);
        Assert.Equal("ObtenerTopEventos", r.Funcion);
        Assert.Matches(@"^del \d{2}/\d{2}/\d{4} al \d{2}/\d{2}/\d{4}$", r.Periodo);
        Assert.Contains("«Festival» con 2 entradas", r.Texto);
        Assert.Equal(new[] { "1", "Festival", "Conciertos", "2", "S/ 80.00", "2 %" }, r.Tabla!.Filas.Single());
        Assert.Equal(new[] { "Festival" }, r.Grafico!.Etiquetas);

        var auditoria = Assert.Single(await _analitica.AuditoriaAsync());
        Assert.Equal("IA: ¿Cuáles fueron los 5 eventos más vendidos este mes?", auditoria.Consulta);
        Assert.StartsWith("ObtenerTopEventos (del ", auditoria.FuenteDatos);
    }

    [Theory]
    [InlineData("Dame recomendaciones", "Recomendaciones")]
    [InlineData("¿Qué debería cambiar para vender más?", "Recomendaciones")]
    [InlineData("¿Cuántos usuarios compraron?", "Compradores")]
    [InlineData("¿Cuánto se ha vendido en total?", "TotalesPlataforma")]
    [InlineData("¿Qué comisiones dejó cada evento?", "ComisionesPorEvento")]
    [InlineData("¿Cómo rindieron los eventos destacados?", "EventosDestacados")]
    [InlineData("¿Qué día se vende más?", "VentasPorDia")]
    [InlineData("¿Cómo han evolucionado las ventas por mes?", "VentasPorMes")]
    public async Task Las_consultas_del_analisis_traen_tabla_y_siguientes_preguntas(string pregunta, string funcion)
    {
        var r = await _ia.ConsultarAsync(pregunta, "org");

        Assert.False(r.FueraDeAlcance);
        Assert.Equal(funcion, r.Funcion);
        Assert.NotNull(r.Tabla);
        Assert.NotEmpty(r.Texto);
        Assert.NotEmpty(r.Sugerencias);
    }

    [Fact]
    public async Task Las_comisiones_por_evento_salen_de_las_ventas_reales()
    {
        var rock = _e.Evento("Rock", 1, DateTime.Today.AddDays(30), capacidad: 100, precio: 50);
        _e.Compra("ana", rock, 2, DateTime.Today.AddHours(9));

        var r = await _ia.ConsultarAsync("¿Qué comisiones dejó cada evento?", "org");

        var fila = Assert.Single(r.Tabla!.Filas);
        Assert.Equal(new[] { "1", "Rock", Formato.Monto(100), Formato.Monto(8) }, new[] { fila[0], fila[1], fila[3], fila[4] });
        Assert.Contains(Formato.Monto(8), r.Texto);
    }

    [Fact]
    public async Task La_respuesta_incluye_recomendaciones_del_tema_y_conserva_el_periodo()
    {
        var r = await _ia.ConsultarAsync("¿Cuánto vendimos del 01/10/2026 al 31/10/2026?", "org");

        Assert.Equal("ResumenVentas", r.Funcion);
        Assert.NotEmpty(r.Recomendaciones);
        Assert.All(r.Recomendaciones, x => Assert.Equal(Recomendador.TemaVentas, x.Tema));
        Assert.Contains(r.Sugerencias, s => s == "Dame recomendaciones del 01/10/2026 al 31/10/2026");
    }

    [Fact]
    public async Task El_grafico_pedido_trae_el_tipo_y_las_cifras_reales()
    {
        var rock = _e.Evento("Rock", 1, DateTime.Today.AddDays(30), capacidad: 100, precio: 50);
        var obra = _e.Evento("Hamlet", 2, DateTime.Today.AddDays(30), capacidad: 100, precio: 80);
        _e.Compra("ana", rock, 2, DateTime.Today.AddHours(9));
        _e.Compra("beto", obra, 1, DateTime.Today.AddHours(10));

        var r = await _ia.ConsultarAsync("Crea un gráfico de pastel de ingresos por categoría", "org");

        Assert.Equal("IngresosPorCategoria", r.Funcion);
        Assert.Equal("pie", r.Grafico!.Tipo);
        Assert.Equal(new[] { 100d, 80d }, r.Grafico.Valores);
        Assert.NotNull(r.Tabla);
    }

    [Fact]
    public async Task Sin_datos_avisa_en_vez_de_dibujar_un_grafico_vacio()
    {
        var r = await _ia.ConsultarAsync("Gráfico de pastel de ingresos por categoría", "org");

        Assert.Null(r.Grafico);
        Assert.Contains("No hay datos para graficar", r.Texto);
    }

    [Fact]
    public async Task Pedir_una_modificacion_sigue_rechazandose()
    {
        var r = await _ia.ConsultarAsync("Elimina el evento Rock", "org");

        Assert.True(r.FueraDeAlcance);
        Assert.Empty(r.Recomendaciones);
    }

    [Fact]
    public async Task Los_clientes_salen_con_codigo_anonimo_y_los_datos_personales_se_rechazan()
    {
        var a = _e.Evento("Festival", 1, DateTime.Now.AddDays(30), 100, 40);
        _e.Compra("ana", a, 2, DateTime.Now.AddDays(-2));

        var top = await _ia.ConsultarAsync("¿Quiénes son los 10 clientes que más gastaron este año?", "org");
        var celdas = string.Join(" ", top.Tabla!.Filas.SelectMany(f => f)) + " " + top.Texto;
        Assert.Contains($"Cliente {ClienteAnalitica.CodigoDe("ana")}", celdas);
        Assert.DoesNotContain("ana@test.pe", celdas);
        Assert.DoesNotMatch(@"\bana\b", celdas.Replace(ClienteAnalitica.CodigoDe("ana"), ""));

        var correo = await _ia.ConsultarAsync("Dame el correo del cliente que más gastó", "org");
        Assert.True(correo.FueraDeAlcance);
        Assert.Null(correo.Tabla);
        Assert.Contains("Ley N.° 29733", correo.Texto);

        var fuera = await _ia.ConsultarAsync("¿Quién ganará el partido del domingo?", "org");
        Assert.True(fuera.FueraDeAlcance);
        Assert.Contains("fuera de lo que puedo consultar", fuera.Texto);
        Assert.Contains(await _analitica.AuditoriaAsync(), x => x.FuenteDatos == "Fuera de alcance");
    }

    [Fact]
    public async Task Resumen_de_segmentos_coincide_con_el_ranking_de_clientes()
    {
        var a = _e.Evento("Festival", 1, DateTime.Now.AddDays(30), 100, 40);
        _e.Compra("ana", a, 2, DateTime.Now.AddDays(-2));
        _e.Compra("beto", a, 1, DateTime.Now.AddDays(-100));
        _e.Compra("beto", a, 1, DateTime.Now.AddDays(-120));

        var segmentos = await _analitica.ResumenSegmentosAsync();
        var clientes = await _analitica.ClientesAsync();

        Assert.Equal(clientes.Count, segmentos.Sum(s => s.Clientes));
        Assert.Equal(clientes.Sum(c => c.Gasto), segmentos.Sum(s => s.GastoTotal));
        var r = await _ia.ConsultarAsync("¿Cuántos clientes en riesgo tenemos?", "org");
        Assert.StartsWith("Hay 1 cliente en el segmento En riesgo", r.Texto);
    }
}

// =============================
// 🤖 HU24: panel de la IA en el dashboard (solo Administrador)
// =============================
public class HU24ConsultasIaVistaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU24ConsultasIaVistaTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<HttpResponseMessage> ConsultarAsync(HttpClient cliente, string token, string pregunta)
    {
        var mensaje = new HttpRequestMessage(HttpMethod.Post, "/Analitica/ConsultarIA") { Content = JsonContent.Create(new { pregunta }) };
        mensaje.Headers.Add("RequestVerificationToken", token);
        return await cliente.SendAsync(mensaje);
    }

    [Fact]
    public async Task El_administrador_pregunta_desde_el_dashboard_y_ve_la_tabla_fuente()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        var html = await TextoAsync(await admin.GetAsync("/Analitica"));
        Assert.Contains("🤖 Pregúntale a la IA", html);
        Assert.Contains("ia-analitica.js", html);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        var r = await ConsultarAsync(admin, token, "¿Cuánto vendimos este mes?");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ResumenVentas", json.GetProperty("funcion").GetString());
        Assert.StartsWith("del ", json.GetProperty("periodo").GetString());
        Assert.Equal(5, json.GetProperty("tabla").GetProperty("filas").GetArrayLength());

        // Sin el token antifalsificación la consulta se rechaza
        Assert.Equal(HttpStatusCode.BadRequest, (await ConsultarAsync(admin, "token-falso", "¿Cuánto vendimos?")).StatusCode);
    }

    [Fact]
    public async Task Solo_el_administrador_puede_consultar_a_la_IA()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);

        var r = await ConsultarAsync(organizador, "x", "¿Cuánto vendimos este mes?");
        Assert.StartsWith("/Auth/AccesoDenegado", r.Headers.Location!.PathAndQuery);
        Assert.StartsWith("/Auth/Login", (await ConsultarAsync(_app.Cliente(), "x", "hola")).Headers.Location!.PathAndQuery);
    }
}
