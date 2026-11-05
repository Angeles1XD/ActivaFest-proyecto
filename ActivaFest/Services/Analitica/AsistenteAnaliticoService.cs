using System.ClientModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;
using ActivaFest.Models;
using ActivaFest.Services.Asistente;

namespace ActivaFest.Services.Analitica;

public record PeriodoConsulta(DateTime Desde, DateTime Hasta, string Etiqueta)
{
    public static PeriodoConsulta Entre(DateTime desde, DateTime hasta) =>
        new(desde.Date, hasta.Date, string.Create(CultureInfo.InvariantCulture, $"del {desde:dd/MM/yyyy} al {hasta:dd/MM/yyyy}"));
}

public class TablaCifras
{
    public string Titulo { get; set; } = "";
    public List<string> Columnas { get; set; } = new();
    public List<List<string>> Filas { get; set; } = new();
}

public record SerieGrafico(string Nombre, List<double> Valores);

public class GraficoSugerido
{
    // bar, barh, stacked, line, area, pie, doughnut, polarArea, radar, scatter o bubble
    public string Tipo { get; set; } = "bar";
    public string Serie { get; set; } = "";
    public string? Titulo { get; set; }
    public List<string> Etiquetas { get; set; } = new();
    public List<double> Valores { get; set; } = new();

    // Solo para las barras apiladas: las partes de cada barra
    public List<SerieGrafico>? Series { get; set; }
}

// Hasta dónde llegan los datos de una consulta: un organizador solo ve lo suyo, y se puede acotar a un evento o categoría
public record AlcanceDatos(string? OrganizadorId = null, int? EventoId = null, string? Evento = null, int? CategoriaId = null, string? Categoria = null)
{
    public bool SoloPropios => OrganizadorId != null;
}

public class RespuestaAnalitica
{
    public string Texto { get; set; } = "";
    public string? Funcion { get; set; }
    public string? Periodo { get; set; }
    public TablaCifras? Tabla { get; set; }
    public GraficoSugerido? Grafico { get; set; }
    public bool FueraDeAlcance { get; set; }
    public bool RedactadaPorIa { get; set; }
    public List<string> Sugerencias { get; set; } = new();

    // Las recomendaciones relacionadas con la pregunta, además de la tabla y el gráfico
    public List<Recomendacion> Recomendaciones { get; set; } = new();

    // Fechas consultadas (null en las funciones que no dependen de un período)
    public PeriodoConsulta? Rango { get; set; }

    // Evento del que se habló, para las preguntas de seguimiento
    public int? EventoId { get; set; }
}

public record ConsultaInterpretada(string Funcion, PeriodoConsulta Periodo, int N, int Dias, double Umbral, string? Segmento, string? Estado = null);

public interface IAsistenteAnaliticoService
{
    Task<RespuestaAnalitica> ConsultarAsync(string pregunta, string administradorId, CancellationToken ct = default);

    // Organizador: lo mismo, pero solo con los eventos que él publicó
    Task<RespuestaAnalitica> ConsultarMisEventosAsync(string pregunta, string organizadorId, CancellationToken ct = default);
}

// =============================
// 🧩 PLUGIN DE SEMANTIC KERNEL: funciones de solo lectura de AnaliticaService (HU24)
// =============================
// La IA no calcula ni genera SQL: elige una de estas funciones, que devuelven datos reales y agregados.
// Cada función deja su resultado completo (texto base, tabla de cifras fuente y gráfico) en "Ultima".
public class FuncionesAnaliticas
{
    public const string NombrePlugin = "Analitica";

    private readonly IAnaliticaService _analitica;
    private readonly AlcanceDatos _alcance;

    public FuncionesAnaliticas(IAnaliticaService analitica, AlcanceDatos? alcance = null)
    {
        _analitica = analitica;
        _alcance = alcance ?? new AlcanceDatos();
    }

    public RespuestaAnalitica? Ultima { get; private set; }

    private string Ambito => _alcance.SoloPropios ? "entre tus eventos" : "en toda la plataforma";

    private FiltroAnalitica Filtro(DateTime desde, DateTime hasta) => new()
    {
        Desde = desde, Hasta = hasta, CategoriaId = _alcance.CategoriaId, EventoId = _alcance.EventoId, OrganizadorId = _alcance.OrganizadorId
    };

    private PeriodoConsulta? _rango;

    private static readonly CultureInfo Pe = CultureInfo.GetCultureInfo("es-PE");
    private static string Pct(double? v) => v == null ? "—" : $"{v.Value * 100:0.#} %";

    private PeriodoConsulta Periodo(string? desde, string? hasta) => _rango = PeriodoDe(desde, hasta);

    private static PeriodoConsulta PeriodoDe(string? desde, string? hasta)
    {
        var hoy = DateTime.Today;
        var d = DateTime.TryParseExact(desde, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : hoy.AddDays(-29);
        var h = DateTime.TryParseExact(hasta, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var y) ? y : hoy;
        return h < d ? PeriodoConsulta.Entre(h, d) : PeriodoConsulta.Entre(d, h);
    }

    private string Guardar(string funcion, string? periodo, string texto, TablaCifras tabla, GraficoSugerido? grafico)
    {
        var acotado = _alcance.Evento ?? _alcance.Categoria;
        if (acotado != null)
            tabla.Titulo += $" · {acotado}";
        if (grafico != null)
            grafico.Titulo = tabla.Titulo;

        Ultima = new RespuestaAnalitica { Funcion = funcion, Periodo = periodo, Texto = texto, Tabla = tabla, Grafico = grafico, Rango = _rango, EventoId = _alcance.EventoId };
        // Lo que recibe el modelo: el texto y la tabla (datos agregados, sin nombres ni correos)
        return JsonSerializer.Serialize(new { periodo, resumen = texto, columnas = tabla.Columnas, filas = tabla.Filas });
    }

    [KernelFunction("ObtenerTopEventos")]
    [Description("Eventos con más entradas vendidas en un período (fechas yyyy-MM-dd) y cuántos devolver.")]
    public async Task<string> ObtenerTopEventosAsync(string? desde = null, string? hasta = null, int n = 5, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var top = await _analitica.TopEventosAsync(p.Desde, p.Hasta, n, ct, Filtro(p.Desde, p.Hasta));
        var tabla = new TablaCifras
        {
            Titulo = $"Eventos más vendidos {p.Etiqueta}",
            Columnas = new() { "#", "Evento", "Categoría", "Entradas", "Ingresos", "Ocupación" },
            Filas = top.Select((e, i) => new List<string> { $"{i + 1}", e.Evento, e.Categoria, e.Entradas.ToString("N0", Pe), Formato.Monto(e.Ingresos), Pct(e.Ocupacion) }).ToList()
        };
        var texto = top.Count == 0
            ? $"No hubo ventas pagadas {p.Etiqueta}."
            : $"El evento más vendido {p.Etiqueta} fue «{top[0].Evento}» con {top[0].Entradas:N0} entradas ({Formato.Monto(top[0].Ingresos)}). " +
              (top.Count > 1 ? $"La tabla muestra los {top.Count} primeros." : "Fue el único con ventas en ese período.");
        return Guardar("ObtenerTopEventos", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Entradas vendidas", Etiquetas = top.Select(e => e.Evento).ToList(), Valores = top.Select(e => (double)e.Entradas).ToList() });
    }

    [KernelFunction("IngresosPorCategoria")]
    [Description("Ingresos y entradas vendidas por categoría de evento en un período (fechas yyyy-MM-dd).")]
    public async Task<string> IngresosPorCategoriaAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var cats = await _analitica.IngresosPorCategoriaAsync(p.Desde, p.Hasta, ct, Filtro(p.Desde, p.Hasta));
        var total = cats.Sum(c => c.Ingresos);
        var tabla = new TablaCifras
        {
            Titulo = $"Ingresos por categoría {p.Etiqueta}",
            Columnas = new() { "Categoría", "Eventos", "Entradas", "Ingresos", "% del total" },
            Filas = cats.Select(c => new List<string> { c.Categoria, $"{c.Eventos}", c.Entradas.ToString("N0", Pe), Formato.Monto(c.Ingresos), total == 0 ? "—" : $"{c.Ingresos / total * 100:0.#} %" }).ToList()
        };
        var texto = cats.Count == 0
            ? $"No hubo ventas pagadas {p.Etiqueta}."
            : $"La categoría que generó más ingresos {p.Etiqueta} fue {cats[0].Categoria} con {Formato.Monto(cats[0].Ingresos)}" +
              (total == 0 ? "." : $" ({cats[0].Ingresos / total * 100:0.#} % del total de {Formato.Monto(total)}).");
        return Guardar("IngresosPorCategoria", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Tipo = "doughnut", Serie = "Ingresos (S/)", Etiquetas = cats.Select(c => c.Categoria).ToList(), Valores = cats.Select(c => (double)c.Ingresos).ToList() });
    }

    [KernelFunction("VariabilidadPorEvento")]
    [Description("Variabilidad de las ventas semanales de cada evento (media, desviación estándar y coeficiente de variación) en un período.")]
    public async Task<string> VariabilidadPorEventoAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var lista = await _analitica.VariabilidadPorEventoAsync(p.Desde, p.Hasta, ct, Filtro(p.Desde, p.Hasta));
        var tabla = new TablaCifras
        {
            Titulo = $"Variabilidad de las ventas semanales {p.Etiqueta}",
            Columnas = new() { "Evento", "Entradas por semana", "Media (μ)", "Desv. estándar (σ)", "CV", "Demanda" },
            Filas = lista.Select(v => new List<string>
            {
                v.Evento, string.Join(" · ", v.Semanas), v.Media.ToString("0.##", Pe), v.Desviacion.ToString("0.##", Pe),
                v.Cv == null ? "—" : $"{v.Cv:0.#} %", v.Nivel
            }).ToList()
        };
        var inestables = lista.Where(v => v.Nivel == "Inestable").ToList();
        var texto = lista.Count == 0
            ? $"No hay eventos con al menos dos semanas de ventas {p.Etiqueta} para medir su variabilidad."
            : inestables.Count == 0
                ? $"Ninguno de los {lista.Count} eventos con ventas {p.Etiqueta} tiene demanda inestable (CV mayor a 50 %)."
                : $"{inestables.Count} de {lista.Count} eventos con ventas {p.Etiqueta} tienen demanda inestable (CV mayor a 50 %). " +
                  $"El más inestable es «{inestables[0].Evento}» con un CV de {inestables[0].Cv:0.#} %: conviene destacarlo o promocionarlo en las semanas de baja venta.";
        return Guardar("VariabilidadPorEvento", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Coeficiente de variación (%)", Etiquetas = lista.Take(10).Select(v => v.Evento).ToList(), Valores = lista.Take(10).Select(v => Math.Round(v.Cv ?? 0, 1)).ToList() });
    }

    [KernelFunction("ResumenSegmentos")]
    [Description("Cantidad de clientes y gasto total y promedio de cada segmento RFM (VIP, Frecuente, En riesgo, Nuevo, Ocasional).")]
    public async Task<string> ResumenSegmentosAsync([Description("Segmento del que se pregunta (opcional)")] string? segmento = null, CancellationToken ct = default)
    {
        var lista = await _analitica.ResumenSegmentosAsync(ct);
        var tabla = new TablaCifras
        {
            Titulo = "Clientes por segmento RFM (historial completo)",
            Columnas = new() { "Segmento", "Clientes", "Gasto total", "Gasto promedio", "Aceptan promociones" },
            Filas = lista.Select(s => new List<string> { s.Segmento, $"{s.Clientes}", Formato.Monto(s.GastoTotal), Formato.Monto(s.GastoPromedio), $"{s.ConConsentimiento}" }).ToList()
        };
        var foco = lista.FirstOrDefault(s => s.Segmento == segmento);
        var totalClientes = lista.Sum(s => s.Clientes);
        var texto = foco != null
            ? $"Hay {foco.Clientes} {(foco.Clientes == 1 ? "cliente" : "clientes")} en el segmento {foco.Segmento}" +
              (foco.Clientes == 0 ? "." : $", con un gasto promedio de {Formato.Monto(foco.GastoPromedio)} y {Formato.Monto(foco.GastoTotal)} en total; {foco.ConConsentimiento} aceptan recibir promociones.")
            : $"Hay {totalClientes} clientes con compras: " + string.Join(", ", lista.Where(s => s.Clientes > 0).Select(s => $"{s.Clientes} {s.Segmento}")) + ".";
        return Guardar("ResumenSegmentos", "historial completo", texto, tabla,
            new GraficoSugerido { Tipo = "doughnut", Serie = "Clientes", Etiquetas = lista.Select(s => s.Segmento).ToList(), Valores = lista.Select(s => (double)s.Clientes).ToList() });
    }

    [KernelFunction("TopClientes")]
    [Description("Clientes que más gastaron en un período, identificados solo con un código anónimo.")]
    public async Task<string> TopClientesAsync(string? desde = null, string? hasta = null, int n = 10, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var top = (await _analitica.FiltrarClientesAsync(new FiltroClientes { Desde = p.Desde, Hasta = p.Hasta }, ct)).Take(Math.Clamp(n, 1, 50)).ToList();
        var tabla = new TablaCifras
        {
            Titulo = $"Clientes que más gastaron {p.Etiqueta}",
            Columnas = new() { "#", "Cliente", "Segmento", "Gasto", "Entradas", "Compras" },
            Filas = top.Select((c, i) => new List<string> { $"{i + 1}", $"Cliente {c.Codigo}", c.Segmento, Formato.Monto(c.Gasto), $"{c.Entradas}", $"{c.Compras}" }).ToList()
        };
        var texto = top.Count == 0
            ? $"No hubo compras pagadas {p.Etiqueta}."
            : $"El cliente que más gastó {p.Etiqueta} es el {top[0].Codigo} ({top[0].Segmento}) con {Formato.Monto(top[0].Gasto)}. " +
              "Por privacidad se muestran con un código anónimo; su ficha está en Clientes.";
        return Guardar("TopClientes", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Gasto (S/)", Etiquetas = top.Select(c => c.Codigo).ToList(), Valores = top.Select(c => (double)c.Gasto).ToList() });
    }

    [KernelFunction("EventosBajaOcupacion")]
    [Description("Eventos de los próximos días con ocupación por debajo de un umbral (0 a 1).")]
    public async Task<string> EventosBajaOcupacionAsync(int dias = 30, double umbral = 0.4, CancellationToken ct = default)
    {
        dias = Math.Clamp(dias, 1, 366);
        umbral = Math.Clamp(umbral > 1 ? umbral / 100 : umbral, 0.01, 1);
        var lista = await _analitica.EventosBajaOcupacionAsync(dias, umbral, ct, Filtro(DateTime.Today, DateTime.Today));
        var periodo = $"próximos {dias} días";
        var tabla = new TablaCifras
        {
            Titulo = $"Eventos de los {periodo} con menos del {umbral * 100:0.#} % de ocupación",
            Columnas = new() { "Evento", "Categoría", "Fecha", "Vendidas / aforo", "Ocupación" },
            Filas = lista.Select(e => new List<string> { e.Evento, e.Categoria, e.Fecha.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), $"{e.Vendidas:N0} / {e.Aforo:N0}", Pct(e.Ocupacion) }).ToList()
        };
        var texto = lista.Count == 0
            ? $"Ningún evento de los {periodo} está por debajo del {umbral * 100:0.#} % de ocupación."
            : $"{lista.Count} {(lista.Count == 1 ? "evento" : "eventos")} de los {periodo} {(lista.Count == 1 ? "está" : "están")} por debajo del {umbral * 100:0.#} % de ocupación. " +
              $"El más bajo es «{lista[0].Evento}» con {Pct(lista[0].Ocupacion)}: conviene una promoción o un paquete destacado.";
        return Guardar("EventosBajaOcupacion", periodo, texto, tabla,
            new GraficoSugerido { Serie = "Ocupación (%)", Etiquetas = lista.Take(10).Select(e => e.Evento).ToList(), Valores = lista.Take(10).Select(e => Math.Round(e.Ocupacion * 100, 1)).ToList() });
    }

    [KernelFunction("ResumenVentas")]
    [Description("Ingresos, entradas vendidas, compras, ticket promedio y comisiones de la plataforma en un período (fechas yyyy-MM-dd).")]
    public async Task<string> ResumenVentasAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var d = await _analitica.DashboardAsync(Filtro(p.Desde, p.Hasta), ct);
        var tabla = new TablaCifras
        {
            Titulo = $"Resumen de ventas {p.Etiqueta}",
            Columnas = new() { "Indicador", "Valor" },
            Filas = new()
            {
                new() { "Ingresos", Formato.Monto(d.Ingresos) },
                new() { "Entradas vendidas", d.EntradasVendidas.ToString("N0", Pe) },
                new() { "Compras", d.Compras.ToString("N0", Pe) },
                new() { "Ticket promedio", Formato.Monto(d.TicketPromedio) },
                new() { "Comisiones de la plataforma", Formato.Monto(d.Comisiones) }
            }
        };
        var texto = d.Compras == 0
            ? $"No hubo ventas pagadas {p.Etiqueta}."
            : $"{char.ToUpper(p.Etiqueta[0])}{p.Etiqueta[1..]} se vendieron {d.EntradasVendidas:N0} entradas por {Formato.Monto(d.Ingresos)} en {d.Compras:N0} compras " +
              $"(ticket promedio de {Formato.Monto(d.TicketPromedio)}); la plataforma cobró {Formato.Monto(d.Comisiones)} de comisión.";
        return Guardar("ResumenVentas", p.Etiqueta, texto, tabla, null);
    }

    private Task<AnalisisVentas> AnalisisAsync(PeriodoConsulta p, CancellationToken ct) =>
        _analitica.AnalisisAsync(Filtro(p.Desde, p.Hasta), ct);

    [KernelFunction("ResumenDeEvento")]
    [Description("Ventas de un solo evento en un período (fechas yyyy-MM-dd): entradas, ingresos, compras, comisión, neto y ocupación, con el detalle por zona.")]
    public async Task<string> ResumenDeEventoAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var a = await AnalisisAsync(p, ct);
        var evento = _alcance.Evento ?? "el evento";
        var ocupacion = a.TopEventos.FirstOrDefault()?.Ocupacion;
        var tabla = new TablaCifras
        {
            Titulo = $"Ventas {p.Etiqueta}",
            Columnas = new() { "Indicador", "Valor" },
            Filas = new()
            {
                new() { "Ingresos", Formato.Monto(a.Ingresos) },
                new() { "Entradas vendidas", a.Entradas.ToString("N0", Pe) },
                new() { "Compras", a.Compras.ToString("N0", Pe) },
                new() { "Ticket promedio", Formato.Monto(a.TicketPromedio) },
                new() { "Comisión de la plataforma", Formato.Monto(a.Comisiones) },
                new() { "Neto para el organizador", Formato.Monto(a.Ingresos - a.Comisiones) },
                new() { "Ocupación", ocupacion == null ? "—" : $"{ocupacion:0.#} %" }
            }
        };
        var texto = a.Compras == 0
            ? $"«{evento}» no tiene ventas pagadas {p.Etiqueta}."
            : $"«{evento}» vendió {a.Entradas:N0} {(a.Entradas == 1 ? "entrada" : "entradas")} por {Formato.Monto(a.Ingresos)} en {a.Compras:N0} {(a.Compras == 1 ? "compra" : "compras")} {p.Etiqueta}. " +
              $"Quedan {Formato.Monto(a.Ingresos - a.Comisiones)} netos tras {Formato.Monto(a.Comisiones)} de comisión." +
              (ocupacion == null ? "" : $" Ocupación: {ocupacion:0.#} %.");

        // Con una sola zona una torta no dice nada: se muestra cómo se vendió día a día
        var ventas = a.VentasDiarias.SkipWhile(d => d.Ingresos == 0).ToList();
        var grafico = a.PorZona.Count >= 2
            ? new GraficoSugerido { Tipo = "doughnut", Serie = "Entradas por zona", Etiquetas = a.PorZona.Select(z => z.Zona).ToList(), Valores = a.PorZona.Select(z => (double)z.Entradas).ToList() }
            : new GraficoSugerido { Tipo = "line", Serie = "Ingresos por día (S/)", Etiquetas = ventas.Select(d => d.Etiqueta).ToList(), Valores = ventas.Select(d => (double)d.Ingresos).ToList() };
        return Guardar("ResumenDeEvento", p.Etiqueta, texto, tabla, grafico);
    }

    [KernelFunction("ListaDeEventos")]
    [Description("Lista los eventos según su estado: aprobados, publicados, realizados, pendientes (por aprobar), rechazados o cancelados; sin estado, todos.")]
    public async Task<string> ListaDeEventosAsync(string? estado = null, string? desde = null, string? hasta = null, int n = 15, CancellationToken ct = default)
    {
        // El período es opcional: sin fechas se listan todos los eventos en ese estado
        var p = desde == null ? null : Periodo(desde, hasta);
        var periodo = p?.Etiqueta ?? "historial completo";
        var filtro = Filtro(DateTime.Today, DateTime.Today);
        var lista = await _analitica.ListaEventosAsync(filtro, estado, p?.Desde, p?.Hasta, ct);
        var visibles = lista.Take(Math.Clamp(n, 1, 50)).ToList();
        var nombre = estado ?? "registrados";
        var moderados = estado is EstadosListado.Aprobados or EstadosListado.Rechazados;

        var columnas = new List<string> { "Evento" };
        if (!_alcance.SoloPropios) columnas.Add("Organizador");
        columnas.AddRange(new[] { "Categoría", "Fecha del evento", "Estado" });
        if (moderados) columnas.Add("Revisado el");
        var tabla = new TablaCifras
        {
            Titulo = $"Eventos {nombre}{(p == null ? "" : " " + p.Etiqueta)}",
            Columnas = columnas,
            Filas = visibles.Select(e =>
            {
                var fila = new List<string> { e.Evento };
                if (!_alcance.SoloPropios) fila.Add(string.IsNullOrWhiteSpace(e.Organizador) ? "—" : e.Organizador);
                fila.AddRange(new[] { e.Categoria, e.Fecha.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), e.Estado });
                if (moderados) fila.Add(e.Moderado?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—");
                return fila;
            }).ToList()
        };

        var cuantos = $"{lista.Count:N0} {(lista.Count == 1 ? "evento" : "eventos")}";
        var nombres = string.Join(", ", visibles.Take(3).Select(e => $"«{e.Evento}»"));
        var verbo = _alcance.SoloPropios ? null : estado switch { EstadosListado.Aprobados => "Has aprobado", EstadosListado.Rechazados => "Has rechazado", _ => null };
        var texto = lista.Count == 0
            ? $"No hay eventos {nombre} {(p == null ? Ambito : p.Etiqueta)}."
            : (verbo != null ? $"{verbo} {cuantos}" : $"Hay {cuantos} {nombre.TrimEnd('s')}{(lista.Count == 1 ? "" : "s")} {Ambito}") +
              (p == null ? "" : $" {p.Etiqueta}") + $". {(lista.Count > 3 ? "Los más recientes" : lista.Count == 1 ? "Es" : "Son")}: {nombres}." +
              (lista.Count > visibles.Count ? $" La tabla muestra los {visibles.Count} últimos." : "");

        var porCategoria = lista.GroupBy(e => e.Categoria).OrderByDescending(g => g.Count()).ToList();
        return Guardar("ListaDeEventos", periodo, texto, tabla,
            new GraficoSugerido { Tipo = "doughnut", Serie = $"Eventos {nombre} por categoría", Etiquetas = porCategoria.Select(g => g.Key).ToList(), Valores = porCategoria.Select(g => (double)g.Count()).ToList() });
    }

    [KernelFunction("VentasPorFecha")]
    [Description("Ingresos y entradas vendidas de cada día de un período (fechas yyyy-MM-dd).")]
    public async Task<string> VentasPorFechaAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var dias = (await AnalisisAsync(p, ct)).VentasDiarias;
        var conVentas = dias.Where(d => d.Ingresos > 0).ToList();
        var tabla = new TablaCifras
        {
            Titulo = $"Ventas por fecha {p.Etiqueta}",
            Columnas = new() { "Fecha", "Entradas", "Ingresos" },
            Filas = conVentas.Select(d => new List<string> { d.Etiqueta, d.Entradas.ToString("N0", Pe), Formato.Monto(d.Ingresos) }).ToList()
        };
        var mejor = conVentas.OrderByDescending(d => d.Ingresos).FirstOrDefault();
        var texto = mejor == null
            ? $"No hubo ventas pagadas {p.Etiqueta}."
            : $"Hubo ventas en {conVentas.Count} {(conVentas.Count == 1 ? "día" : "días")} {p.Etiqueta}. El mejor fue el {mejor.Etiqueta} con {Formato.Monto(mejor.Ingresos)}.";
        return Guardar("VentasPorFecha", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Tipo = "line", Serie = "Ingresos (S/)", Etiquetas = dias.Select(d => d.Etiqueta).ToList(), Valores = dias.Select(d => (double)d.Ingresos).ToList() });
    }

    [KernelFunction("VentasAcumuladas")]
    [Description("Cómo se fueron acumulando los ingresos día a día en un período (fechas yyyy-MM-dd).")]
    public async Task<string> VentasAcumuladasAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var dias = (await AnalisisAsync(p, ct)).VentasDiarias;
        var tabla = new TablaCifras
        {
            Titulo = $"Ingresos acumulados {p.Etiqueta}",
            Columnas = new() { "Fecha", "Ingresos del día", "Acumulado" },
            Filas = dias.Where(d => d.Ingresos > 0).Select(d => new List<string> { d.Etiqueta, Formato.Monto(d.Ingresos), Formato.Monto(d.Acumulado) }).ToList()
        };
        var total = dias.LastOrDefault()?.Acumulado ?? 0;
        var texto = total == 0 ? $"No hubo ventas pagadas {p.Etiqueta}." : $"{char.ToUpper(p.Etiqueta[0])}{p.Etiqueta[1..]} se acumularon {Formato.Monto(total)} en ingresos.";
        return Guardar("VentasAcumuladas", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Tipo = "area", Serie = "Ingresos acumulados (S/)", Etiquetas = dias.Select(d => d.Etiqueta).ToList(), Valores = dias.Select(d => (double)d.Acumulado).ToList() });
    }

    [KernelFunction("VentasPorSemana")]
    [Description("Ingresos de las últimas 10 semanas hasta una fecha (yyyy-MM-dd).")]
    public async Task<string> VentasPorSemanaAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var semanas = (await _analitica.DashboardAsync(Filtro(p.Desde, p.Hasta), ct)).IngresosPorSemana;
        var periodo = $"últimas {AnaliticaService.SemanasTendencia} semanas";
        var tabla = new TablaCifras
        {
            Titulo = $"Ingresos de las {periodo}",
            Columnas = new() { "Semana", "Ingresos" },
            Filas = semanas.Select(s => new List<string> { s.Etiqueta, Formato.Monto(s.Ingresos) }).ToList()
        };
        var mejor = semanas.OrderByDescending(s => s.Ingresos).First();
        var texto = mejor.Ingresos == 0
            ? $"No hubo ventas pagadas en las {periodo}."
            : $"En las {periodo} se vendieron {Formato.Monto(semanas.Sum(s => s.Ingresos))}. La mejor fue la {mejor.Etiqueta} con {Formato.Monto(mejor.Ingresos)}.";
        return Guardar("VentasPorSemana", periodo, texto, tabla,
            new GraficoSugerido { Tipo = "line", Serie = "Ingresos (S/)", Etiquetas = semanas.Select(s => s.Etiqueta).ToList(), Valores = semanas.Select(s => (double)s.Ingresos).ToList() });
    }

    [KernelFunction("VentasPorHora")]
    [Description("Compras por hora del día en un período (fechas yyyy-MM-dd): a qué hora se compra más.")]
    public async Task<string> VentasPorHoraAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var horas = (await AnalisisAsync(p, ct)).ComprasPorHora;
        var tabla = new TablaCifras
        {
            Titulo = $"Compras por hora {p.Etiqueta}",
            Columnas = new() { "Hora", "Compras" },
            Filas = horas.Where(h => h.Compras > 0).Select(h => new List<string> { h.Hora, h.Compras.ToString("N0", Pe) }).ToList()
        };
        var mejor = horas.OrderByDescending(h => h.Compras).First();
        var texto = mejor.Compras == 0
            ? $"No hubo ventas pagadas {p.Etiqueta}."
            : $"La hora con más compras {p.Etiqueta} es las {mejor.Hora}, con {mejor.Compras:N0} de {horas.Sum(h => h.Compras):N0}.";
        return Guardar("VentasPorHora", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Compras", Etiquetas = horas.Select(h => h.Hora).ToList(), Valores = horas.Select(h => (double)h.Compras).ToList() });
    }

    [KernelFunction("Recomendaciones")]
    [Description("Recomendaciones para el administrador según las ventas, eventos y clientes de un período (fechas yyyy-MM-dd): qué hacer y con qué dato se justifica.")]
    public async Task<string> RecomendacionesAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var a = await AnalisisAsync(p, ct);
        var tabla = new TablaCifras
        {
            Titulo = $"Recomendaciones {p.Etiqueta}",
            Columnas = new() { "Prioridad", "Hallazgo", "Dato", "Acción sugerida" },
            Filas = a.Recomendaciones.Select(r => new List<string> { r.Prioridad, r.Hallazgo, r.Dato, r.Accion }).ToList()
        };
        var primera = a.Recomendaciones[0];
        var texto = $"Encontré {a.Recomendaciones.Count} {(a.Recomendaciones.Count == 1 ? "punto" : "puntos")} a revisar {p.Etiqueta}. Lo primero: {primera.Hallazgo} ({primera.Dato}). {primera.Accion}";
        return Guardar("Recomendaciones", p.Etiqueta, texto, tabla, null);
    }

    [KernelFunction("TotalesPlataforma")]
    [Description("Totales acumulados de toda la plataforma desde su inicio: ingresos, comisiones, compras, entradas, compradores y eventos publicados.")]
    public async Task<string> TotalesPlataformaAsync(CancellationToken ct = default)
    {
        var hoy = DateTime.Today;
        var t = (await AnalisisAsync(PeriodoConsulta.Entre(hoy.AddDays(-29), hoy), ct)).Acumulado;
        var tabla = new TablaCifras
        {
            Titulo = _alcance.SoloPropios ? "Totales acumulados de tus eventos" : "Totales acumulados de la plataforma",
            Columnas = new() { "Indicador", "Valor" },
            Filas = new()
            {
                new() { "Ingresos", Formato.Monto(t.Ingresos) },
                new() { "Comisiones de la plataforma", Formato.Monto(t.Comisiones) },
                new() { "Compras", t.Compras.ToString("N0", Pe) },
                new() { "Entradas vendidas", t.Entradas.ToString("N0", Pe) },
                new() { "Compradores", t.Compradores.ToString("N0", Pe) },
                new() { "Eventos publicados", t.EventosPublicados.ToString("N0", Pe) }
            }
        };
        if (!_alcance.SoloPropios)
            tabla.Filas.Insert(5, new() { "Usuarios registrados", t.UsuariosRegistrados.ToString("N0", Pe) });
        var texto = _alcance.SoloPropios
            ? $"Tus eventos vendieron {t.Entradas:N0} entradas por {Formato.Monto(t.Ingresos)}, con {Formato.Monto(t.Comisiones)} de comisión y {t.Compradores:N0} compradores."
            : $"Desde su inicio la plataforma vendió {t.Entradas:N0} entradas por {Formato.Monto(t.Ingresos)} y cobró {Formato.Monto(t.Comisiones)} de comisión. " +
              $"Compraron {t.Compradores:N0} de {t.UsuariosRegistrados:N0} usuarios registrados.";
        return Guardar("TotalesPlataforma", "historial completo", texto, tabla, null);
    }

    [KernelFunction("ComisionesPorEvento")]
    [Description("Comisiones que dejó cada evento en un período (fechas yyyy-MM-dd), de mayor a menor ingreso.")]
    public async Task<string> ComisionesPorEventoAsync(string? desde = null, string? hasta = null, int n = 5, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var a = await AnalisisAsync(p, ct);
        var top = a.TopEventos.OrderByDescending(e => e.Comision).Take(Math.Clamp(n, 1, AnaliticaService.EventosEnAnalisis)).ToList();
        var tabla = new TablaCifras
        {
            Titulo = $"Comisiones por evento {p.Etiqueta}",
            Columnas = new() { "#", "Evento", "Categoría", "Ingresos", "Comisión" },
            Filas = top.Select((e, i) => new List<string> { $"{i + 1}", e.Evento, e.Categoria, Formato.Monto(e.Ingresos), Formato.Monto(e.Comision) }).ToList()
        };
        var texto = top.Count == 0
            ? $"No hubo ventas pagadas {p.Etiqueta}."
            : $"La plataforma cobró {Formato.Monto(a.Comisiones)} de comisión {p.Etiqueta}. El evento que más dejó fue «{top[0].Evento}» con {Formato.Monto(top[0].Comision)}.";
        return Guardar("ComisionesPorEvento", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Comisión (S/)", Etiquetas = top.Select(e => e.Evento).ToList(), Valores = top.Select(e => (double)e.Comision).ToList() });
    }

    [KernelFunction("EventosDestacados")]
    [Description("Eventos con un paquete destacado vigente en un período (fechas yyyy-MM-dd) y lo que vendieron mientras estuvieron destacados.")]
    public async Task<string> EventosDestacadosAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var lista = (await AnalisisAsync(p, ct)).Destacados;
        var tabla = new TablaCifras
        {
            Titulo = $"Eventos destacados {p.Etiqueta}",
            Columnas = new() { "Evento", "Paquete", "Vigencia", "Precio del paquete", "Entradas", "Ingresos" },
            Filas = lista.Select(d => new List<string> { d.Evento, d.Paquete, d.Vigencia, Formato.Monto(d.PrecioPaquete), d.Entradas.ToString("N0", Pe), Formato.Monto(d.Ingresos) }).ToList()
        };
        var texto = lista.Count == 0
            ? $"Ningún evento tuvo un paquete destacado vigente {p.Etiqueta}. Si buscas los que más vendieron, pregunta por los eventos más vendidos."
            : $"{lista.Count} {(lista.Count == 1 ? "evento estuvo destacado" : "eventos estuvieron destacados")} {p.Etiqueta}. El que más vendió mientras estuvo destacado fue «{lista[0].Evento}» con {Formato.Monto(lista[0].Ingresos)}.";
        return Guardar("EventosDestacados", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Ingresos mientras estuvo destacado (S/)", Etiquetas = lista.Select(d => d.Evento).ToList(), Valores = lista.Select(d => (double)d.Ingresos).ToList() });
    }

    [KernelFunction("Compradores")]
    [Description("Cuántos usuarios compraron en un período (fechas yyyy-MM-dd): compradores únicos, nuevos y recurrentes, y los que más gastaron con código anónimo.")]
    public async Task<string> CompradoresAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var a = await AnalisisAsync(p, ct);
        var tabla = new TablaCifras
        {
            Titulo = $"Compradores {p.Etiqueta}",
            Columnas = new() { "Indicador", "Valor" },
            Filas = new()
            {
                new() { "Compradores únicos", a.Compradores.ToString("N0", Pe) },
                new() { "Nuevos", a.CompradoresNuevos.ToString("N0", Pe) },
                new() { "Recurrentes", a.CompradoresRecurrentes.ToString("N0", Pe) },
                new() { "Compras", a.Compras.ToString("N0", Pe) },
                new() { "Ticket promedio", Formato.Monto(a.TicketPromedio) }
            }
        };
        var texto = a.Compradores == 0
            ? $"Nadie compró {p.Etiqueta}."
            : $"{a.Compradores:N0} {(a.Compradores == 1 ? "usuario compró" : "usuarios compraron")} {p.Etiqueta}: {a.CompradoresNuevos:N0} por primera vez y {a.CompradoresRecurrentes:N0} con más de una compra en el período.";
        return Guardar("Compradores", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Compradores", Etiquetas = new() { "Nuevos", "Recurrentes" }, Valores = new() { a.CompradoresNuevos, a.CompradoresRecurrentes } });
    }

    [KernelFunction("EventosDePlataforma")]
    [Description("Cuántos eventos hay en toda la plataforma según su estado: publicados, realizados, pendientes, rechazados y cancelados.")]
    public async Task<string> EventosDePlataformaAsync(CancellationToken ct = default)
    {
        var hoy = DateTime.Today;
        var estados = (await AnalisisAsync(PeriodoConsulta.Entre(hoy.AddDays(-29), hoy), ct)).EventosPorEstado;
        var total = estados.Sum(e => e.Total);
        var tabla = new TablaCifras
        {
            Titulo = _alcance.SoloPropios ? "Tus eventos por estado" : "Eventos de toda la plataforma",
            Columnas = new() { "Estado", "Eventos", "% del total" },
            Filas = estados.Select(e => new List<string> { e.Etiqueta, e.Total.ToString("N0", Pe), total == 0 ? "—" : $"{100.0 * e.Total / total:0.#} %" }).ToList()
        };
        var texto = $"Hay {total:N0} {(total == 1 ? "evento" : "eventos")} {Ambito}: " +
                    string.Join(", ", estados.Where(e => e.Total > 0).Select(e => $"{e.Total:N0} {e.Etiqueta.ToLowerInvariant()}")) + ".";
        return Guardar("EventosDePlataforma", "historial completo", texto, tabla,
            new GraficoSugerido { Tipo = "doughnut", Serie = "Eventos por estado", Etiquetas = estados.Select(e => e.Etiqueta).ToList(), Valores = estados.Select(e => (double)e.Total).ToList() });
    }

    [KernelFunction("ComisionesPorCategoria")]
    [Description("Comisiones que dejó cada categoría de evento en un período (fechas yyyy-MM-dd).")]
    public async Task<string> ComisionesPorCategoriaAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var a = await AnalisisAsync(p, ct);
        var cats = a.PorCategoria.OrderByDescending(c => c.Comisiones).ToList();
        var tabla = new TablaCifras
        {
            Titulo = $"Comisiones por categoría {p.Etiqueta}",
            Columnas = new() { "Categoría", "Ingresos", "Comisiones" },
            Filas = cats.Select(c => new List<string> { c.Categoria, Formato.Monto(c.Ingresos), Formato.Monto(c.Comisiones) }).ToList()
        };
        var texto = cats.Count == 0
            ? $"No hubo ventas pagadas {p.Etiqueta}."
            : $"La plataforma cobró {Formato.Monto(a.Comisiones)} de comisión {p.Etiqueta}; {cats[0].Categoria} aportó {Formato.Monto(cats[0].Comisiones)}.";
        return Guardar("ComisionesPorCategoria", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Tipo = "doughnut", Serie = "Comisiones (S/)", Etiquetas = cats.Select(c => c.Categoria).ToList(), Valores = cats.Select(c => (double)c.Comisiones).ToList() });
    }

    [KernelFunction("EntradasPorZona")]
    [Description("Entradas vendidas e ingresos por zona o tipo de entrada (General, VIP…) en un período (fechas yyyy-MM-dd).")]
    public async Task<string> EntradasPorZonaAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var zonas = (await AnalisisAsync(p, ct)).PorZona;
        var tabla = new TablaCifras
        {
            Titulo = $"Entradas por zona {p.Etiqueta}",
            Columnas = new() { "Zona", "Entradas", "Ingresos" },
            Filas = zonas.Select(z => new List<string> { z.Zona, z.Entradas.ToString("N0", Pe), Formato.Monto(z.Ingresos) }).ToList()
        };
        var texto = zonas.Count == 0
            ? $"No hubo ventas pagadas {p.Etiqueta}."
            : $"La zona más vendida {p.Etiqueta} fue {zonas[0].Zona} con {zonas[0].Entradas:N0} entradas ({Formato.Monto(zonas[0].Ingresos)}).";
        return Guardar("EntradasPorZona", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Entradas vendidas", Etiquetas = zonas.Select(z => z.Zona).ToList(), Valores = zonas.Select(z => (double)z.Entradas).ToList() });
    }

    [KernelFunction("OrdenesPorEstado")]
    [Description("En qué terminaron las órdenes y reservas creadas en un período (fechas yyyy-MM-dd): pagadas, activas, vencidas, canceladas o rechazadas.")]
    public async Task<string> OrdenesPorEstadoAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var estados = (await AnalisisAsync(p, ct)).OrdenesPorEstado;
        var total = estados.Sum(e => e.Total);
        var pagadas = estados.First().Total;
        var tabla = new TablaCifras
        {
            Titulo = $"Órdenes y reservas {p.Etiqueta}",
            Columnas = new() { "Estado", "Órdenes" },
            Filas = estados.Select(e => new List<string> { e.Etiqueta, e.Total.ToString("N0", Pe) }).ToList()
        };
        var texto = total == 0
            ? $"No se crearon órdenes {p.Etiqueta}."
            : $"De {total:N0} {(total == 1 ? "orden creada" : "órdenes creadas")} {p.Etiqueta}, {pagadas:N0} terminaron en compra ({100.0 * pagadas / total:0.#} %).";
        return Guardar("OrdenesPorEstado", p.Etiqueta, texto, tabla,
            new GraficoSugerido { Serie = "Órdenes", Etiquetas = estados.Select(e => e.Etiqueta).ToList(), Valores = estados.Select(e => (double)e.Total).ToList() });
    }

    [KernelFunction("VentasPorDia")]
    [Description("Compras e ingresos por día de la semana desde julio de 2026 hasta hoy.")]
    public async Task<string> VentasPorDiaAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var a = await AnalisisAsync(p, ct);
        var tabla = new TablaCifras
        {
            Titulo = $"Ventas por día de la semana {p.Etiqueta}",
            Columnas = new() { "Día", "Compras", "Ingresos" },
            Filas = a.DiasSemana.Select(d => new List<string> { d.Dia, d.Compras.ToString("N0", Pe), Formato.Monto(d.Ingresos) }).ToList()
        };
        var mejor = a.DiasSemana.OrderByDescending(d => d.Compras).First();
        var periodoHistorico = $"desde {AnaliticaService.InicioSerieHistorica.ToString("MMMM 'de' yyyy", Pe)} hasta hoy";
        var compras = a.DiasSemana.Sum(d => d.Compras);
        var texto = compras == 0
            ? $"No hubo ventas pagadas {periodoHistorico}."
            : $"{mejor.Dia} es el día con más compras {periodoHistorico}: {mejor.Compras:N0} de {compras:N0}, por {Formato.Monto(mejor.Ingresos)}.";
        return Guardar("VentasPorDia", periodoHistorico, texto, tabla,
            new GraficoSugerido { Serie = "Compras", Etiquetas = a.DiasSemana.Select(d => d.Dia).ToList(), Valores = a.DiasSemana.Select(d => (double)d.Compras).ToList() });
    }

    [KernelFunction("VentasPorMes")]
    [Description("Ingresos y comisiones mensuales desde julio de 2026 hasta hoy.")]
    public async Task<string> VentasPorMesAsync(string? desde = null, string? hasta = null, CancellationToken ct = default)
    {
        var p = Periodo(desde, hasta);
        var meses = (await AnalisisAsync(p, ct)).Meses;
        var periodo = $"desde {AnaliticaService.InicioSerieHistorica.ToString("MMMM 'de' yyyy", Pe)} hasta hoy";
        var tabla = new TablaCifras
        {
            Titulo = $"Ingresos y comisiones de los {periodo}",
            Columnas = new() { "Mes", "Ingresos", "Comisiones" },
            Filas = meses.Select(m => new List<string> { m.Etiqueta, Formato.Monto(m.Ingresos), Formato.Monto(m.Comisiones) }).ToList()
        };
        var mejor = meses.OrderByDescending(m => m.Ingresos).First();
        var texto = mejor.Ingresos == 0
            ? $"No hubo ventas pagadas en los {periodo}."
            : $"En los {periodo} se vendieron {Formato.Monto(meses.Sum(m => m.Ingresos))}. El mejor mes fue {mejor.Etiqueta} con {Formato.Monto(mejor.Ingresos)}.";
        return Guardar("VentasPorMes", periodo, texto, tabla,
            new GraficoSugerido
            {
                Serie = "Ingresos (S/)", Etiquetas = meses.Select(m => m.Etiqueta).ToList(), Valores = meses.Select(m => (double)m.Ingresos).ToList(),
                Series = new()
                {
                    new("Neto para organizadores (S/)", meses.Select(m => (double)(m.Ingresos - m.Comisiones)).ToList()),
                    new("Comisiones (S/)", meses.Select(m => (double)m.Comisiones).ToList())
                }
            });
    }
}

// =============================
// 🤖 CONSULTAS EN LENGUAJE NATURAL PARA EL ADMINISTRADOR (HU24 / OE6)
// =============================
// Con Llama 3.2 (Ollama) activado, el modelo elige la función (function calling de Semantic Kernel) y redacta.
// Sin LLM (o si no responde), un intérprete de la pregunta elige la función y sus parámetros: el panel funciona
// igual y las cifras son las mismas, porque siempre salen de las funciones de solo lectura.
public class AsistenteAnaliticoService : IAsistenteAnaliticoService
{
    public static readonly string[] Ejemplos =
    {
        "¿Cuáles fueron los 5 eventos más vendidos este mes?",
        "¿Qué categoría generó más ingresos este año?",
        "¿Qué eventos tienen la demanda más inestable?",
        "¿Cuántos clientes VIP hay y cuánto gastan en promedio?",
        "¿Quiénes son los 10 clientes que más gastaron este año?",
        "¿Qué eventos de la próxima semana tienen menos del 40 % de ocupación?",
        "¿Qué me recomiendas para vender más este mes?",
        "¿Cuántos usuarios compraron este mes?",
        "¿Cómo rindieron los eventos destacados este año?",
        "¿Cuánto se ha vendido en total?",
        "¿Cuántos eventos hay en total?",
        "Gráfico de pastel de ingresos por categoría",
        "Gráfico de barras de ventas por mes",
        "Dime los eventos que ya he aprobado",
        "¿Qué eventos faltan aprobar?",
        "Gráfico de área de los ingresos acumulados",
        "Gráfico de radar de las compras por día de la semana"
    };

    // Funciones con datos de toda la plataforma: un organizador no las puede consultar
    private static readonly string[] SoloPlataforma = { "ResumenSegmentos", "TopClientes" };

    // Con un evento nombrado siguen teniendo sentido estas; cualquier otra pregunta es el resumen de ese evento
    private static readonly string[] SobreUnEvento =
    {
        "ResumenDeEvento", "EntradasPorZona", "VentasPorDia", "VentasPorMes", "VentasPorFecha", "VentasPorSemana", "VentasPorHora", "VentasAcumuladas",
        "OrdenesPorEstado", "Compradores", "Recomendaciones", "EventosDestacados"
    };

    // Temas de las recomendaciones que acompañan a cada función
    private static readonly Dictionary<string, string> TemaDe = new()
    {
        ["ObtenerTopEventos"] = Recomendador.TemaEventos, ["VariabilidadPorEvento"] = Recomendador.TemaEventos,
        ["EventosBajaOcupacion"] = Recomendador.TemaEventos, ["EventosDestacados"] = Recomendador.TemaEventos,
        ["ComisionesPorEvento"] = Recomendador.TemaEventos, ["EventosDePlataforma"] = Recomendador.TemaEventos,
        ["ResumenSegmentos"] = Recomendador.TemaClientes, ["TopClientes"] = Recomendador.TemaClientes, ["Compradores"] = Recomendador.TemaClientes,
        ["IngresosPorCategoria"] = Recomendador.TemaVentas, ["ResumenVentas"] = Recomendador.TemaVentas, ["TotalesPlataforma"] = Recomendador.TemaVentas,
        ["VentasPorDia"] = Recomendador.TemaVentas, ["VentasPorMes"] = Recomendador.TemaVentas, ["ComisionesPorCategoria"] = Recomendador.TemaVentas,
        ["EntradasPorZona"] = Recomendador.TemaVentas, ["OrdenesPorEstado"] = Recomendador.TemaVentas,
        ["ResumenDeEvento"] = Recomendador.TemaEventos, ["VentasPorFecha"] = Recomendador.TemaVentas, ["VentasAcumuladas"] = Recomendador.TemaVentas,
        ["VentasPorSemana"] = Recomendador.TemaVentas, ["VentasPorHora"] = Recomendador.TemaVentas
    };

    // Siguientes preguntas que se ofrecen después de cada respuesta
    private static readonly Dictionary<string, string[]> Siguientes = new()
    {
        ["ObtenerTopEventos"] = new[] { "¿Qué comisiones dejó cada evento?", "¿Cómo rindieron los eventos destacados?" },
        ["IngresosPorCategoria"] = new[] { "¿Qué eventos tienen más ventas?", "¿Qué día se vende más?" },
        ["VariabilidadPorEvento"] = new[] { "¿Qué eventos tienen más ventas?" },
        ["ResumenSegmentos"] = new[] { "¿Quiénes son los clientes que más gastaron?", "¿Cuántos usuarios compraron?" },
        ["TopClientes"] = new[] { "¿Cuántos usuarios compraron?", "Resumen de los segmentos" },
        ["EventosBajaOcupacion"] = new[] { "¿Cómo rindieron los eventos destacados?" },
        ["ResumenVentas"] = new[] { "¿Qué eventos tienen más ventas?", "¿Cuántos usuarios compraron?", "¿Cómo han evolucionado las ventas por mes?" },
        ["TotalesPlataforma"] = new[] { "¿Cómo han evolucionado las ventas por mes?", "¿Qué categoría generó más ingresos?" },
        ["ComisionesPorEvento"] = new[] { "¿Qué categoría generó más ingresos?", "Resumen de ventas" },
        ["EventosDestacados"] = new[] { "¿Qué eventos tienen más ventas?" },
        ["Compradores"] = new[] { "¿Quiénes son los clientes que más gastaron?", "Resumen de los segmentos" },
        ["VentasPorDia"] = new[] { "¿Cómo han evolucionado las ventas por mes?" },
        ["VentasPorMes"] = new[] { "¿Qué día se vende más?", "Resumen de ventas" },
        ["Recomendaciones"] = new[] { "¿Qué eventos tienen más ventas?", "¿Cuántos usuarios compraron?" },
        ["EventosDePlataforma"] = new[] { "¿Qué eventos tienen más ventas?", "Gráfico de barras de eventos por estado" },
        ["ComisionesPorCategoria"] = new[] { "¿Qué comisiones dejó cada evento?" },
        ["EntradasPorZona"] = new[] { "¿Qué eventos tienen más ventas?" },
        ["OrdenesPorEstado"] = new[] { "¿Cuántos usuarios compraron?" },
        ["ResumenDeEvento"] = new[] { "Gráfico de ventas por fecha", "¿Qué eventos tienen más ventas?" },
        ["ListaDeEventos"] = new[] { "Gráfico de eventos por estado" },
        ["VentasPorFecha"] = new[] { "Gráfico de área de los ingresos acumulados", "¿A qué hora se compra más?" },
        ["VentasAcumuladas"] = new[] { "Gráfico de ventas por fecha" },
        ["VentasPorSemana"] = new[] { "¿Cómo han evolucionado las ventas por mes?" },
        ["VentasPorHora"] = new[] { "¿Qué día se vende más?" }
    };

    private readonly IAnaliticaService _analitica;
    private readonly AsistenteOptions _opciones;
    private readonly ILogger<AsistenteAnaliticoService> _logger;

    public AsistenteAnaliticoService(IAnaliticaService analitica, IOptions<AsistenteOptions> opciones, ILogger<AsistenteAnaliticoService> logger)
    {
        _analitica = analitica;
        _opciones = opciones.Value;
        _logger = logger;
    }

    public Task<RespuestaAnalitica> ConsultarAsync(string pregunta, string administradorId, CancellationToken ct = default) =>
        ConsultarConAlcanceAsync(pregunta, administradorId, new AlcanceDatos(), ct);

    public Task<RespuestaAnalitica> ConsultarMisEventosAsync(string pregunta, string organizadorId, CancellationToken ct = default) =>
        ConsultarConAlcanceAsync(pregunta, organizadorId, new AlcanceDatos(organizadorId), ct);

    private async Task<RespuestaAnalitica> ConsultarConAlcanceAsync(string pregunta, string usuarioId, AlcanceDatos alcance, CancellationToken ct)
    {
        pregunta = (pregunta ?? "").Trim();
        if (pregunta.Length > 300)
            pregunta = pregunta[..300];

        var (respuesta, acotado, texto) = await ResolverAsync(pregunta, alcance, ct);
        if (respuesta.Funcion != null)
        {
            AplicarGraficoPedido(respuesta, texto);
            await CompletarAsync(respuesta, acotado, ct);
        }

        // Cada consulta a la IA queda en la auditoría (Ley N.° 29733), también las que no se pudieron responder
        await _analitica.RegistrarAuditoriaAsync(usuarioId, $"IA: {pregunta}",
            respuesta.Funcion != null ? $"{respuesta.Funcion} ({respuesta.Periodo})" : "Fuera de alcance", ct);
        return respuesta;
    }

    private static bool SinDatos(RespuestaAnalitica r) => r.Grafico == null || r.Grafico.Valores.All(v => v == 0);

    // Se elige el tipo de gráfico: "de pastel", "de barras", "de líneas"… Sin pedir uno, una lista queda como tabla
    private static void AplicarGraficoPedido(RespuestaAnalitica r, string texto)
    {
        if (!PideGrafico(texto))
        {
            if (r.Funcion == "ListaDeEventos")
                r.Grafico = null;
            return;
        }

        if (SinDatos(r))
        {
            r.Grafico = null;
            r.Texto += " No hay datos para graficar en este período.";
            return;
        }

        var tipo = TipoGraficoPedido(texto) ?? r.Grafico!.Tipo;
        r.Grafico!.Tipo = tipo == "stacked" && r.Grafico.Series == null ? "bar" : tipo;
        r.Texto = "📊 Aquí tienes el gráfico. " + r.Texto;
    }

    // Además de la tabla: recomendaciones del tema consultado y las siguientes preguntas, en el mismo período
    private async Task CompletarAsync(RespuestaAnalitica r, AlcanceDatos alcance, CancellationToken ct)
    {
        var hoy = DateTime.Today;
        var rango = r.Rango ?? PeriodoConsulta.Entre(hoy.AddDays(-29), hoy);
        if (TemaDe.TryGetValue(r.Funcion!, out var tema))
        {
            var filtro = new FiltroAnalitica { Desde = rango.Desde, Hasta = rango.Hasta, CategoriaId = alcance.CategoriaId, EventoId = alcance.EventoId, OrganizadorId = alcance.OrganizadorId };
            var analisis = await _analitica.AnalisisAsync(filtro, ct);
            r.Recomendaciones = analisis.Recomendaciones.Where(x => x.Tema == tema).Take(3).ToList();
        }

        // Las siguientes preguntas siguen hablando del mismo evento y del mismo período
        var periodo = r.Rango == null ? "" : string.Create(CultureInfo.InvariantCulture, $" del {rango.Desde:dd/MM/yyyy} al {rango.Hasta:dd/MM/yyyy}");
        var evento = alcance.Evento == null ? "" : BaseConocimiento.Normalizar(alcance.Evento).StartsWith("evento ") ? $" del {alcance.Evento}" : $" del evento {alcance.Evento}";
        var recomendar = alcance.SoloPropios ? "¿Qué me recomiendas para vender más?" : "Dame recomendaciones";
        r.Sugerencias = Siguientes.GetValueOrDefault(r.Funcion!, Array.Empty<string>())
            .Where(s => !alcance.SoloPropios || !(s.Contains("clientes") || s.Contains("segmentos")))
            .Where(s => alcance.Evento == null || s.StartsWith("Gráfico"))
            .Concat(r.Funcion == "Recomendaciones" ? Array.Empty<string>() : new[] { recomendar })
            .Select(s => s.TrimEnd('?') + evento + periodo + (s.EndsWith('?') ? "?" : ""))
            .ToList();
    }

    private static RespuestaAnalitica Aclarar(string texto, IEnumerable<string> sugerencias) =>
        new() { Texto = texto, Sugerencias = sugerencias.ToList() };

    // Si el nombre no coincide con nada se dice, en vez de responder con los datos de otra cosa
    private static RespuestaAnalitica EventoNoEncontrado(string mencion, List<OpcionEvento> eventos, AlcanceDatos alcance)
    {
        var buscadas = mencion.Split(' ');
        var parecidos = eventos
            .Where(e => BaseConocimiento.Normalizar(e.Titulo).Split(' ').Any(p => p.Length > 3 && buscadas.Any(b => ReconocedorEventos.Parecida(p, b))))
            .Concat(eventos)
            .DistinctBy(e => e.Id)
            .Take(3);
        return Aclarar(alcance.SoloPropios
                ? $"No encontré «{mencion}» entre tus eventos. Solo puedo mostrarte datos de los eventos que tú publicaste."
                : $"No encontré ningún evento llamado «{mencion}». Revisa el nombre o elige uno de estos.",
            parecidos.Select(e => $"Ventas del evento {e.Titulo}"));
    }

    // Categoría nombrada en la pregunta: "ventas de la categoría Conciertos", "gráfico de ventas de teatro"
    private static OpcionCategoria? CategoriaMencionada(string texto, List<OpcionCategoria> categorias)
    {
        var palabras = texto.Split(' ');
        return categorias.FirstOrDefault(c => BaseConocimiento.Normalizar(c.Nombre).Split(' ').Where(p => p.Length > 2)
            .All(n => palabras.Any(p => p == n || p + "s" == n || n + "s" == p || p + "es" == n || n + "es" == p)));
    }

    private static string SinCategoria(string texto, string categoria)
    {
        var nombre = BaseConocimiento.Normalizar(categoria).Split(' ');
        return string.Join(' ', texto.Split(' ').Where(p => p is not ("categoria" or "categorias") && !nombre.Any(n => ReconocedorEventos.Parecida(n, p))));
    }

    // Devuelve también el alcance final (con el evento o la categoría nombrados) y la frase sin esos nombres
    private async Task<(RespuestaAnalitica Respuesta, AlcanceDatos Alcance, string Texto)> ResolverAsync(string pregunta, AlcanceDatos alcance, CancellationToken ct)
    {
        var texto = BaseConocimiento.Normalizar(pregunta);
        if (texto.Length == 0)
            return (FueraDeAlcance("Escribe una pregunta sobre los eventos o las ventas."), alcance, texto);

        // La IA solo lee datos agregados: nunca entrega datos personales ni modifica nada
        if (PideDatosPersonales(texto))
            return (FueraDeAlcance("Por privacidad (Ley N.° 29733) no puedo entregar nombres, correos ni datos de contacto de los clientes. " +
                                   "En los resultados se identifican con un código anónimo; su ficha, con registro de auditoría, está en Clientes."), alcance, texto);
        if (PideModificar(texto) && !PideRecomendacion(texto) && !PideGrafico(texto) && !PideListaDeEventos(texto))
            return (FueraDeAlcance("Solo puedo consultar datos, no crear, modificar ni eliminar nada. Para eso usa las pantallas de administración."), alcance, texto);

        // El nombre del evento se saca de la frase antes de interpretarla: sus palabras no deben confundirse con la pregunta
        var eventos = await _analitica.OpcionesEventoAsync(alcance.OrganizadorId, ct);
        var reconocido = ReconocedorEventos.Reconocer(texto, eventos.Select(e => new EventoCandidato(e.Id, e.Titulo, e.Fecha)));
        if (reconocido.Ambiguo)
            return (Aclarar("Encontré varios eventos con ese nombre. ¿De cuál quieres los datos?",
                reconocido.Opciones.Select(e => $"{(PideGrafico(texto) ? "Gráfico de ventas" : "Ventas")} del evento {e.Titulo}")), alcance, texto);

        var interpretado = reconocido.TextoSinEvento;
        var categoria = reconocido.Encontrado == null ? CategoriaMencionada(interpretado, await _analitica.OpcionesCategoriaAsync(ct)) : null;
        if (reconocido.Encontrado == null && categoria == null && (reconocido.NoEncontrado ?? reconocido.Mencion) is { } mencion)
            return (EventoNoEncontrado(mencion, eventos, alcance), alcance, texto);

        if (reconocido.Encontrado != null)
            alcance = alcance with { EventoId = reconocido.Encontrado.Id, Evento = reconocido.Encontrado.Titulo };
        if (categoria != null)
        {
            alcance = alcance with { CategoriaId = categoria.Id, Categoria = categoria.Nombre };
            interpretado = SinCategoria(interpretado, categoria.Nombre);
        }

        var funciones = new FuncionesAnaliticas(_analitica, alcance);
        var kernel = Kernel.CreateBuilder().Build();
        kernel.Plugins.AddFromObject(funciones, FuncionesAnaliticas.NombrePlugin);

        // Los gráficos, los eventos por nombre y lo del organizador los resuelve siempre el intérprete: sus datos no dependen del modelo
        if (_opciones.UsarLlm && !PideGrafico(texto) && alcance == new AlcanceDatos())
        {
            var conLlm = await ConLlmAsync(pregunta, funciones, ct);
            if (conLlm != null)
                return (conLlm, alcance, interpretado);
        }

        var consulta = Interpretar(interpretado, DateTime.Today, alcance);
        if (consulta == null)
            return (FueraDeAlcance(alcance.SoloPropios
                ? "Puedo mostrarte de tus eventos: ventas por evento, categoría, zona, mes, semana, día y hora, comisiones, compradores, ocupación y su estado, " +
                  "con el gráfico que quieras (pastel, dona, barras, líneas, área, radar o polar)."
                : "Esa pregunta está fuera de lo que puedo consultar. Puedo responder sobre eventos más vendidos, destacados, aprobados, pendientes y rechazados, ingresos por categoría, " +
                  "comisiones, ventas por mes, semana, día y hora, totales acumulados, eventos de la plataforma, compradores, segmentos y mejores clientes, variabilidad de la demanda " +
                  "y ocupación de los próximos eventos. También puedo generar gráficos (pastel, dona, barras, líneas, área, radar o polar) y darte recomendaciones. " +
                  "También puedes usar los filtros del dashboard."), alcance, interpretado);

        if (alcance.SoloPropios && SoloPlataforma.Contains(consulta.Funcion))
            return (FueraDeAlcance("Esa información es de los clientes de toda la plataforma y solo la ve el administrador. Puedo mostrarte cuántos compradores tuvieron tus eventos."), alcance, interpretado);

        var argumentos = Argumentos(consulta, consulta.Periodo);
        await kernel.InvokeAsync(FuncionesAnaliticas.NombrePlugin, consulta.Funcion, argumentos, ct);

        // Pidió un gráfico sin decir de cuándo y el último mes está vacío: mejor mostrar el último año que no mostrar nada
        if (PideGrafico(interpretado) && SinDatos(funciones.Ultima!) && funciones.Ultima!.Rango != null && EsPeriodoPorDefecto(consulta.Periodo))
        {
            var hoy = DateTime.Today;
            await kernel.InvokeAsync(FuncionesAnaliticas.NombrePlugin, consulta.Funcion, Argumentos(consulta, PeriodoConsulta.Entre(hoy.AddDays(-365), hoy)), ct);
            if (!SinDatos(funciones.Ultima!))
                funciones.Ultima!.Texto = "En los últimos 30 días no hubo datos, así que te muestro el último año. " + funciones.Ultima.Texto;
        }
        return (funciones.Ultima!, alcance, interpretado);
    }

    private static KernelArguments Argumentos(ConsultaInterpretada consulta, PeriodoConsulta periodo)
    {
        var argumentos = new KernelArguments
        {
            ["desde"] = periodo.Desde.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["hasta"] = periodo.Hasta.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["n"] = consulta.N,
            ["dias"] = consulta.Dias,
            ["umbral"] = consulta.Umbral,
            ["segmento"] = consulta.Segmento
        };
        if (consulta.Funcion == "ListaDeEventos")
        {
            argumentos["estado"] = consulta.Estado;
            if (EsPeriodoPorDefecto(periodo))
                argumentos["desde"] = null;
        }
        return argumentos;
    }

    // Llama 3.2 elige la función y redacta; si no llama a ninguna, falla o cambia alguna cifra, se usa el intérprete
    private async Task<RespuestaAnalitica?> ConLlmAsync(string pregunta, FuncionesAnaliticas funciones, CancellationToken ct)
    {
        try
        {
            var cliente = new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions { Endpoint = new Uri(_opciones.UrlLlm) });
            var builder = Kernel.CreateBuilder();
            builder.AddOpenAIChatCompletion(_opciones.Modelo, cliente);
            var kernel = builder.Build();
            kernel.Plugins.AddFromObject(funciones, FuncionesAnaliticas.NombrePlugin);

            var historia = new ChatHistory(
                $"Eres el analista de datos de ActivaFest. Hoy es {DateTime.Today:yyyy-MM-dd}. Responde en español en máximo 3 oraciones. " +
                "Para cualquier cifra DEBES llamar a una de las funciones disponibles y usar solo los datos que devuelva; nunca calcules ni inventes números. " +
                "Si piden consejos o qué hacer, llama a Recomendaciones y resume las acciones que devuelva. " +
                "Si ninguna función sirve para la pregunta, responde exactamente: FUERA_DE_ALCANCE.");
            historia.AddUserMessage(pregunta);

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(_opciones.TiempoMaximoSegundos * 3));
            var ajustes = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), Temperature = 0 };
            var mensaje = await kernel.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(historia, ajustes, kernel, limite.Token);

            var resultado = funciones.Ultima;
            if (resultado == null)
                return null;

            var redactado = mensaje.Content?.Trim();
            if (!string.IsNullOrEmpty(redactado) && SoloCifrasFuente(redactado, resultado))
            {
                resultado.Texto = redactado;
                resultado.RedactadaPorIa = true;
            }
            return resultado;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning("La IA analítica no respondió ({Motivo}); se usa el intérprete de preguntas.", ex.GetBaseException().Message);
            return null;
        }
    }

    // Toda cifra del texto de la IA debe aparecer en el texto base o en la tabla: si inventó un número, se descarta
    public static bool SoloCifrasFuente(string redactado, RespuestaAnalitica fuente)
    {
        var permitido = fuente.Texto + " " + string.Join(" ", fuente.Tabla?.Filas.SelectMany(f => f) ?? Enumerable.Empty<string>()) + " " + fuente.Periodo;
        return Regex.Matches(redactado, @"\d+(?:[.,]\d+)*").All(m => permitido.Contains(m.Value));
    }

    private static RespuestaAnalitica FueraDeAlcance(string texto) => new()
    {
        Texto = texto,
        FueraDeAlcance = true,
        Sugerencias = Ejemplos.Take(4).ToList()
    };

    private static readonly string[] Personales =
        { "correo", "correos", "email", "e mail", "telefono", "celular", "dni", "direccion de", "como se llama", "como se llaman", "nombre del cliente", "nombres de los clientes", "nombre de los clientes", "datos personales", "datos de contacto", "quien es el cliente", "nombre completo" };

    private static readonly string[] Modificar =
        { "borra", "borrar", "elimina", "eliminar", "modifica", "modificar", "cambia", "cambiar", "actualiza", "actualizar", "crea", "crear", "aprueba", "aprobar", "aprobalo", "apruebalo", "cancela", "cancelar" };

    public static bool PideDatosPersonales(string t) => Personales.Any(p => BaseConocimiento.Contiene(t, p));
    public static bool PideModificar(string t) => Modificar.Any(p => BaseConocimiento.Contiene(t, p));

    private static readonly string[] Consejos =
    {
        "recomendacion", "recomendaciones", "recomiendas", "recomienda", "recomiendan", "recomendarias", "consejo", "consejos", "aconsejas",
        "sugerencia", "sugerencias", "sugieres", "que deberia", "que debo", "que puedo hacer", "que hago", "que hacer", "mejorar", "plan de accion",
        "aumentar las ventas", "subir las ventas", "vender mas"
    };

    public static bool PideRecomendacion(string t) => Consejos.Any(p => BaseConocimiento.Contiene(t, p));

    // Las formas ("de pastel", "en barras") solo cuentan con su preposición: "pastel" solo puede ser parte de un título
    private static readonly string[] Graficos =
    {
        "grafico", "graficos", "grafica", "graficas", "graficame", "graficar", "graficalo", "diagrama", "diagramas", "chart", "charts", "histograma", "estadisticas", "estadistica",
        "dashboard", "plot", "visualiza", "visualizame", "visualizacion", "dibuja", "dibujame", "representa", "representacion grafica",
        "de pastel", "en pastel", "un pastel", "de torta", "en torta", "una torta", "de tarta", "circular", "de dona", "en dona", "una dona", "donut", "de anillo",
        "de barras", "en barras", "barras horizontales", "barras apiladas", "de columnas", "en columnas", "de lineas", "en lineas", "de linea",
        "de area", "en area", "radar", "polar", "radial", "de dispersion", "de burbujas", "pie chart"
    };

    public static bool PideGrafico(string t) => Graficos.Any(p => BaseConocimiento.Contiene(t, p));

    // null: quiere un gráfico pero no dijo de qué tipo, se usa el que mejor le va a esos datos
    public static string? TipoGraficoPedido(string t)
    {
        bool Hay(params string[] frases) => frases.Any(f => BaseConocimiento.Contiene(t, f));
        return Hay("pastel", "torta", "tarta", "circular", "pie") ? "pie"
            : Hay("dona", "donut", "anillo") ? "doughnut"
            : Hay("polar", "radial") ? "polarArea"
            : Hay("radar", "telarana", "arana") ? "radar"
            : Hay("burbujas", "burbuja") ? "bubble"
            : Hay("dispersion", "puntos") ? "scatter"
            : Hay("apiladas", "apilado", "apiladas", "apilada", "apilar") ? "stacked"
            : Hay("barras horizontales", "horizontal", "horizontales") ? "barh"
            : Hay("area", "areas") ? "area"
            : Hay("barras", "columnas", "histograma") ? "bar"
            : Hay("lineas", "linea", "curva", "evolucion") ? "line"
            : null;
    }

    // Estado de los eventos que se quieren listar ("los que ya aprobé"); null si no menciona ninguno
    public static string? EstadoPedido(string t)
    {
        bool Hay(params string[] frases) => frases.Any(f => BaseConocimiento.Contiene(t, f));
        return Hay("por aprobar", "faltan aprobar", "falta aprobar", "sin aprobar", "pendientes", "pendiente", "en revision", "por revisar", "esperando aprobacion", "por moderar") ? EstadosListado.Pendientes
            : Hay("aprobado", "aprobados", "aprobada", "aprobadas", "aprobe", "aprobamos", "aprobaron") ? EstadosListado.Aprobados
            : Hay("rechazado", "rechazados", "rechazada", "rechazadas", "rechace", "rechazamos", "rechazaron") ? EstadosListado.Rechazados
            : Hay("cancelado", "cancelados", "cancelada", "canceladas") ? EstadosListado.Cancelados
            : Hay("realizados", "realizadas", "finalizados", "finalizadas", "terminados", "terminadas", "pasados", "pasadas") ? EstadosListado.Realizados
            : Hay("publicados", "publicadas", "activos", "activas", "vigentes") ? EstadosListado.Publicados
            : null;
    }

    public static bool PideListaDeEventos(string t)
    {
        bool Hay(params string[] frases) => frases.Any(f => BaseConocimiento.Contiene(t, f));
        if (Hay("lista de eventos", "listame los eventos", "lista los eventos", "todos los eventos", "dime los eventos", "muestrame los eventos", "ver los eventos", "listado de eventos"))
            return true;
        return Hay("evento", "eventos") && EstadoPedido(t) != null
            && !Hay("cuantos", "cuantas", "por estado", "total", "mas vendidos", "mas vendido", "mas ventas", "mas ingresos");
    }

    // =============================
    // 🧠 INTÉRPRETE DE PREGUNTAS (sin LLM)
    // =============================
    public static ConsultaInterpretada? Interpretar(string t, DateTime hoy, AlcanceDatos? alcance = null)
    {
        bool Hay(params string[] frases) => frases.Any(f => BaseConocimiento.Contiene(t, f));
        var grafico = PideGrafico(t);

        var periodo = PeriodoMencionado(t, hoy);

        // De un evento concreto interesa toda su venta, no solo el último mes
        if (alcance?.EventoId != null && EsPeriodoPorDefecto(periodo))
            periodo = PeriodoConsulta.Entre(hoy.AddDays(-365), hoy);

        var segmento = SegmentoRfm.Todos.FirstOrDefault(s => Hay(BaseConocimiento.Normalizar(s), BaseConocimiento.Normalizar(s) + "s"))
                       ?? (Hay("en riesgo") ? SegmentoRfm.EnRiesgo : Hay("frecuentes") ? SegmentoRfm.Frecuente : Hay("nuevos") ? SegmentoRfm.Nuevo : Hay("ocasionales") ? SegmentoRfm.Ocasional : null);

        string? funcion =
            PideRecomendacion(t) && !grafico
                ? "Recomendaciones"
            : PideListaDeEventos(t)
                ? "ListaDeEventos"
            : Hay("destacado", "destacados", "destacada", "destacadas", "paquete", "paquetes", "publicidad")
                ? "EventosDestacados"
            : Hay("ocupacion", "poca venta", "baja venta", "bajas ventas", "vendiendo poco", "se venden poco", "medio vacios", "vacios", "llenos")
                ? "EventosBajaOcupacion"
            : Hay("cuantos eventos", "eventos hay en total", "eventos en total", "total de eventos", "eventos publicados", "eventos por estado", "estado de los eventos", "eventos de la plataforma", "eventos en la plataforma")
                ? "EventosDePlataforma"
            : Hay("inestable", "inestables", "variabilidad", "variacion", "estable", "estables", "coeficiente", "volatil", "volatiles", "demanda")
                ? "VariabilidadPorEvento"
            : Hay("clientes", "usuarios", "compradores") && Hay("mas gastaron", "mas gastan", "mas gasto", "mejores", "top", "mas compraron", "mas compran", "ranking", "mas valiosos", "que mas")
                ? "TopClientes"
            : Hay("usuarios registrados", "todos los usuarios", "cuantos usuarios hay")
                ? "TotalesPlataforma"
            : Hay("comprador", "compradores", "usuarios compraron", "usuarios que compraron", "usuarios han comprado", "usuarios que han comprado", "cuantos usuarios", "personas compraron", "gente compro", "recurrentes")
                ? "Compradores"
            : segmento != null || Hay("segmento", "segmentos", "rfm", "cuantos clientes", "tipos de clientes")
                ? "ResumenSegmentos"
            : Hay("comision", "comisiones") && Hay("categoria", "categorias")
                ? "ComisionesPorCategoria"
            : Hay("categoria", "categorias", "tipo de evento", "tipos de evento", "rubro")
                ? "IngresosPorCategoria"
            : Hay("zona", "zonas", "tipo de entrada", "tipos de entrada")
                ? "EntradasPorZona"
            : Hay("reservas", "ordenes", "compras por estado", "estado de las compras", "estado de las ordenes")
                ? "OrdenesPorEstado"
            : Hay("comision", "comisiones") && Hay("evento", "eventos", "cada evento", "por evento")
                ? "ComisionesPorEvento"
            : Hay("eventos", "evento", "conciertos", "obras") && Hay("mas vendidos", "mas vendido", "mas vendieron", "vendieron mas", "vendio mas", "top", "mejores", "ranking", "mas exitosos", "mas entradas", "mas populares", "mas vendidas", "mas ventas", "mas ingresos")
                ? "ObtenerTopEventos"
            : grafico && Hay("acumulado", "acumulados", "acumuladas", "acumulada")
                ? "VentasAcumuladas"
            : Hay("por hora", "por horas", "que hora", "que horas", "mejor hora", "hora del dia", "horas del dia", "horario de compra")
                ? "VentasPorHora"
            : Hay("por fecha", "por fechas", "diarias", "diario", "diarios", "dia a dia", "cada dia")
                ? "VentasPorFecha"
            : Hay("dia de la semana", "dias de la semana", "que dia", "que dias", "mejor dia", "por dia")
                ? "VentasPorDia"
            : Hay("por semana", "por semanas", "semana a semana", "cada semana", "semanal", "semanales")
                ? "VentasPorSemana"
            : Hay("por mes", "por meses", "mes a mes", "cada mes", "mensual", "mensuales", "evolucion", "evolucionado", "tendencia")
                ? "VentasPorMes"
            : Hay("en total", "acumulado", "acumulados", "historico", "desde el inicio", "panorama general", "resumen general", "como vamos", "resumen de la plataforma", "toda la plataforma", "toda la pagina", "toda la web")
                ? "TotalesPlataforma"
            : Hay("ingresos", "ingreso", "cuanto se vendio", "cuanto vendimos", "cuanto vendio", "cuanto he vendido", "cuanto vendi", "he vendido", "ventas", "vendimos", "recaudado", "recaudamos", "facturamos", "facturado", "comision", "comisiones", "ticket promedio", "entradas vendidas", "cuantas entradas", "ganamos", "ganancia")
                ? "ResumenVentas"
            : null;

        // Con un evento nombrado, todo lo que no tenga sentido para un solo evento es su resumen
        if (alcance?.EventoId != null && !SobreUnEvento.Contains(funcion))
            funcion = "ResumenDeEvento";

        // "Hazme un gráfico de las ventas": si no dice de qué, por mes o día, o por categoría en una torta
        if (grafico && funcion is null or "ResumenVentas" or "TotalesPlataforma")
            funcion = Hay("eventos", "evento") ? "ObtenerTopEventos"
                : Hay("dia", "dias", "semana") ? "VentasPorDia"
                : !Hay("mes", "meses", "tendencia", "evolucion") && TipoGraficoPedido(t) is "pie" or "doughnut" or "polarArea" ? "IngresosPorCategoria"
                : TipoGraficoPedido(t) == "radar" ? "VentasPorDia"
                : "VentasPorMes";

        if (funcion == null)
            return null;

        var n = NumeroDe(t, funcion == "TopClientes" ? 10 : funcion == "ListaDeEventos" ? 15 : 5);
        var dias = DiasMencionados(t);
        var umbral = UmbralMencionado(t);
        return new ConsultaInterpretada(funcion, periodo, n, dias, umbral, funcion == "ResumenSegmentos" ? segmento : null, funcion == "ListaDeEventos" ? EstadoPedido(t) : null);
    }

    private static readonly string[] Meses = { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" };

    private const string EtiquetaPorDefecto = "de los últimos 30 días";

    public static bool EsPeriodoPorDefecto(PeriodoConsulta p) => p.Etiqueta == EtiquetaPorDefecto;

    // Período de la pregunta (fechas de pago); si no menciona ninguno, los últimos 30 días
    public static PeriodoConsulta PeriodoMencionado(string t, DateTime hoy)
    {
        bool Hay(string f) => BaseConocimiento.Contiene(t, f);

        // "del 01/11/2026 al 04/11/2026": así preguntan los botones del dashboard, con su período exacto
        var exacto = Regex.Match(t, @"\bdel (\d{2}) (\d{2}) (\d{4}) al (\d{2}) (\d{2}) (\d{4})\b");
        if (exacto.Success
            && DateTime.TryParseExact($"{exacto.Groups[1]}/{exacto.Groups[2]}/{exacto.Groups[3]}", "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var inicio)
            && DateTime.TryParseExact($"{exacto.Groups[4]}/{exacto.Groups[5]}/{exacto.Groups[6]}", "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fin))
            return fin < inicio ? PeriodoConsulta.Entre(fin, inicio) : PeriodoConsulta.Entre(inicio, fin);

        var anio = Regex.Match(t, @"\b(20\d{2})\b") is { Success: true } a ? int.Parse(a.Groups[1].Value) : (int?)null;

        var ultimos = Regex.Match(t, @"\bultim[oa]s (\d{1,3}) (dias|semanas|meses)\b");
        if (ultimos.Success)
        {
            var k = int.Parse(ultimos.Groups[1].Value);
            var desde = ultimos.Groups[2].Value switch { "dias" => hoy.AddDays(-(k - 1)), "semanas" => hoy.AddDays(-(7 * k - 1)), _ => hoy.AddMonths(-k).AddDays(1) };
            return new PeriodoConsulta(desde, hoy, $"de los últimos {k} {ultimos.Groups[2].Value.Replace("dias", "días")}");
        }
        if (Hay("ultima semana")) return new PeriodoConsulta(hoy.AddDays(-6), hoy, "de la última semana");
        if (Hay("ultimo mes")) return new PeriodoConsulta(hoy.AddDays(-29), hoy, "del último mes");
        if (Hay("ultimo ano")) return new PeriodoConsulta(hoy.AddYears(-1).AddDays(1), hoy, "del último año");
        if (Hay("hoy")) return new PeriodoConsulta(hoy, hoy, "de hoy");
        if (Hay("ayer")) return new PeriodoConsulta(hoy.AddDays(-1), hoy.AddDays(-1), "de ayer");

        var lunes = hoy.AddDays(-(((int)hoy.DayOfWeek + 6) % 7));
        if (Hay("semana pasada")) return new PeriodoConsulta(lunes.AddDays(-7), lunes.AddDays(-1), "de la semana pasada");
        if (Hay("esta semana")) return new PeriodoConsulta(lunes, hoy, "de esta semana");

        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);
        if (Hay("mes pasado")) return new PeriodoConsulta(inicioMes.AddMonths(-1), inicioMes.AddDays(-1), $"de {Meses[inicioMes.AddMonths(-1).Month - 1]} de {inicioMes.AddMonths(-1).Year}");
        if (Hay("este mes")) return new PeriodoConsulta(inicioMes, hoy, $"de {Meses[hoy.Month - 1]} de {hoy.Year}");

        for (var m = 0; m < Meses.Length; m++)
        {
            if (!Hay(Meses[m]) && !(m == 8 && Hay("setiembre")))
                continue;
            // Sin año: la ocurrencia más reciente de ese mes que no sea futura
            var y = anio ?? (m + 1 > hoy.Month ? hoy.Year - 1 : hoy.Year);
            var desde = new DateTime(y, m + 1, 1);
            var hasta = desde.AddMonths(1).AddDays(-1);
            return new PeriodoConsulta(desde, hasta > hoy && y == hoy.Year ? hoy : hasta, $"de {Meses[m]} de {y}");
        }

        if (Hay("ano pasado")) return new PeriodoConsulta(new DateTime(hoy.Year - 1, 1, 1), new DateTime(hoy.Year - 1, 12, 31), $"de {hoy.Year - 1}");
        if (anio != null) return new PeriodoConsulta(new DateTime(anio.Value, 1, 1), anio == hoy.Year ? hoy : new DateTime(anio.Value, 12, 31), $"de {anio}");
        if (Hay("este ano") || Hay("en el ano") || Hay("del ano") || Hay("en lo que va del ano"))
            return new PeriodoConsulta(new DateTime(hoy.Year, 1, 1), hoy, $"de {hoy.Year}");

        return new PeriodoConsulta(hoy.AddDays(-29), hoy, EtiquetaPorDefecto);
    }

    // "los 5 eventos", "top 10", "10 clientes"
    private static int NumeroDe(string t, int porDefecto)
    {
        var m = Regex.Match(t, @"\b(?:top )?(\d{1,2}) (?:eventos|evento|clientes|cliente|mejores|primeros|mas|conciertos|obras)\b");
        if (!m.Success)
            m = Regex.Match(t, @"\btop (\d{1,2})\b");
        return m.Success ? Math.Clamp(int.Parse(m.Groups[1].Value), 1, 50) : porDefecto;
    }

    private static int DiasMencionados(string t)
    {
        var m = Regex.Match(t, @"\bproxim[oa]s (\d{1,3}) (dias|semanas)\b");
        if (m.Success)
            return int.Parse(m.Groups[1].Value) * (m.Groups[2].Value == "semanas" ? 7 : 1);
        if (BaseConocimiento.Contiene(t, "proxima semana") || BaseConocimiento.Contiene(t, "siguiente semana") || BaseConocimiento.Contiene(t, "esta semana"))
            return 7;
        if (BaseConocimiento.Contiene(t, "proximo mes") || BaseConocimiento.Contiene(t, "siguiente mes"))
            return 30;
        return 30;
    }

    // "menos del 40 %" (la normalización quita el signo %), "40 por ciento"; por defecto 40 %
    private static double UmbralMencionado(string t)
    {
        var m = Regex.Match(t, @"\b(?:menos de(?:l)?|menor(?: a(?:l)?| que el)?|debajo de(?:l)?|bajo el|inferior a(?:l)?|por debajo de(?:l)?)\s+(\d{1,3})\b");
        if (!m.Success)
            m = Regex.Match(t, @"\b(\d{1,3}) por ciento\b");
        return m.Success ? Math.Clamp(int.Parse(m.Groups[1].Value), 1, 100) / 100.0 : 0.40;
    }
}
