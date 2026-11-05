using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using ActivaFest.Services.Catalogo;

namespace ActivaFest.Controllers
{
    // =============================
    // 📊 ANALÍTICA DEL ADMINISTRADOR (HU21 dashboard)
    // =============================
    // Solo el rol Administrador (criterio de HU21 y RNF07)
    [Authorize(Roles = Roles.Administrador)]
    public class AnaliticaController : Controller
    {
        private readonly IAnaliticaService _analitica;
        private readonly ICatalogoService _catalogo;
        private readonly IAsistenteAnaliticoService _ia;
        private readonly ActivaFest.Services.Asistente.IAsistenteService _asistente;
        private readonly ActivaFest.Services.Asistente.IRedactorLlm _llm;

        public AnaliticaController(IAnaliticaService analitica, ICatalogoService catalogo, IAsistenteAnaliticoService ia,
            ActivaFest.Services.Asistente.IAsistenteService asistente, ActivaFest.Services.Asistente.IRedactorLlm llm)
        {
            _analitica = analitica;
            _catalogo = catalogo;
            _ia = ia;
            _asistente = asistente;
            _llm = llm;
        }

        // Período: uno rápido (hoy, últimos 7 días, este mes…) o personalizado con fechas. Por defecto, este mes hasta hoy.
        private static FiltroAnalitica Filtro(string? rango, DateTime? desde, DateTime? hasta, int? categoriaId, int? eventoId) =>
            FiltroAnalitica.DeRango(rango, desde, hasta, categoriaId, DateTime.Today, eventoId).Filtro;

        public async Task<IActionResult> Index(string? rango, DateTime? desde, DateTime? hasta, int? categoriaId, int? eventoId, CancellationToken ct)
        {
            ViewBag.Categorias = await _catalogo.ObtenerCategoriasAsync(ct);
            ViewBag.Eventos = await _analitica.OpcionesEventoAsync(null, ct);
            ViewBag.Rango = FiltroAnalitica.DeRango(rango, desde, hasta, categoriaId, DateTime.Today).Rango;
            // HU34: alcance de los eventos gratuitos en el mismo período (visitas, «Me interesa» y tarifas cobradas)
            var filtro = Filtro(rango, desde, hasta, categoriaId, eventoId);
            ViewBag.Gratuitos = await HttpContext.RequestServices.GetRequiredService<Services.Eventos.IEventoGratuitoService>()
                .ResumenAsync(filtro.Desde, filtro.Hasta, ct);
            return View(await _analitica.DashboardAsync(filtro, ct));
        }

        // Datos del dashboard en JSON: la página los vuelve a pedir cada pocos segundos (actualización en tiempo real)
        public async Task<IActionResult> Datos(string? rango, DateTime? desde, DateTime? hasta, int? categoriaId, int? eventoId, CancellationToken ct)
        {
            var d = await _analitica.DashboardAsync(Filtro(rango, desde, hasta, categoriaId, eventoId), ct);
            return Json(new
            {
                calculadoEn = d.CalculadoEn,
                ingresos = d.Ingresos, ingresosVariacion = DashboardAnalitica.Variacion(d.Ingresos, d.IngresosAnterior),
                entradas = d.EntradasVendidas, entradasVariacion = DashboardAnalitica.Variacion(d.EntradasVendidas, d.EntradasAnterior),
                ticket = d.TicketPromedio, compras = d.Compras,
                ocupacion = d.Ocupacion, vendidasEventos = d.VendidasEventos, aforoEventos = d.AforoEventos, eventos = d.EventosConsiderados,
                asistencia = d.Asistencia, realizados = d.EventosRealizados,
                comisiones = d.Comisiones, conversion = d.Conversion, activos = d.UsuariosActivos,
                semanas = d.IngresosPorSemana.Select(s => new { etiqueta = s.Etiqueta, ingresos = s.Ingresos }),
                categorias = d.VentasPorCategoria.Select(c => new { nombre = c.Categoria, entradas = c.Entradas, ingresos = c.Ingresos, color = c.Color }),
                ultimas = d.UltimasCompras.Select(c => new { fecha = c.Fecha.ToString("dd/MM HH:mm"), evento = c.Evento, cliente = c.Cliente, entradas = c.Entradas, total = c.Total })
            });
        }

        // Gráficos, totales y recomendaciones. Incluye el ranking de compradores (con código anónimo): se audita como Clientes.
        public async Task<IActionResult> Analisis(string? rango, DateTime? desde, DateTime? hasta, int? categoriaId, int? eventoId, CancellationToken ct)
        {
            var filtro = Filtro(rango, desde, hasta, categoriaId, eventoId);
            var analisis = await _analitica.AnalisisAsync(filtro, ct);
            await _analitica.RegistrarAuditoriaAsync(AdministradorId,
                $"Análisis del dashboard ({filtro.Desde:dd/MM/yyyy} - {filtro.Hasta:dd/MM/yyyy}{(categoriaId != null ? $", categoría {categoriaId}" : "")}{(eventoId != null ? $", evento {eventoId}" : "")})",
                "Ordenes, Eventos, ContratosDestacado", ct);
            return Json(analisis);
        }

        // Ventas de una semana, quincena, mes o trimestre. Sin fecha (o con una futura) se usa el período en curso.
        public async Task<IActionResult> VentasPeriodo(string? tipo, DateTime? fecha, int? categoriaId, int? eventoId, int pagina = 1, CancellationToken ct = default)
        {
            var hoy = DateTime.Today;
            var periodo = PeriodoVentas.De(tipo, fecha is { Year: >= 2000 } f && f <= hoy ? f : hoy);
            var r = await _analitica.VentasPeriodoAsync(periodo, categoriaId, pagina, hoy, ct, eventoId);
            return Json(new
            {
                tipo = periodo.Tipo, etiqueta = periodo.Etiqueta,
                desde = periodo.Desde.ToString("dd/MM/yyyy"), hasta = periodo.Hasta.ToString("dd/MM/yyyy"),
                anterior = periodo.Anterior().Desde.ToString("yyyy-MM-dd"),
                siguiente = periodo.Hasta < hoy ? periodo.Siguiente().Desde.ToString("yyyy-MM-dd") : null,
                ingresos = r.Totales.Ingresos, compras = r.Totales.Compras, entradas = r.Totales.Entradas, comisiones = r.Totales.Comisiones,
                ingresosAnterior = r.IngresosAnterior,
                resumenes = r.Resumenes.Select(p => new { tipo = p.Tipo, etiqueta = p.Etiqueta, ingresos = p.Totales.Ingresos, compras = p.Totales.Compras, ingresosAnterior = p.IngresosAnterior }),
                ventas = r.Ventas.Select(c => new { fecha = c.Fecha.ToString("dd/MM/yyyy HH:mm"), evento = c.Evento, cliente = c.Cliente, entradas = c.Entradas, total = c.Total }),
                pagina = r.Pagina, paginas = r.Paginas
            });
        }

        // CSV para Excel en español: separador ";", coma decimal y BOM UTF-8
        public async Task<IActionResult> Exportar(string? rango, DateTime? desde, DateTime? hasta, int? categoriaId, int? eventoId, CancellationToken ct)
        {
            var d = await _analitica.DashboardAsync(Filtro(rango, desde, hasta, categoriaId, eventoId), ct);
            string Num(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
            string Pct(double? v) => v?.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') ?? "";

            var sb = new StringBuilder();
            sb.AppendLine($"Dashboard ActivaFest;{d.Desde:dd/MM/yyyy} - {d.Hasta:dd/MM/yyyy}");
            sb.AppendLine("Indicador;Valor");
            sb.AppendLine($"Ingresos (S/);{Num(d.Ingresos)}");
            sb.AppendLine($"Entradas vendidas;{d.EntradasVendidas}");
            sb.AppendLine($"Compras;{d.Compras}");
            sb.AppendLine($"Ticket promedio (S/);{Num(d.TicketPromedio)}");
            sb.AppendLine($"Ocupación (%);{Pct(d.Ocupacion)}");
            sb.AppendLine($"Asistencia (%);{Pct(d.Asistencia)}");
            sb.AppendLine($"Conversión (%);{Pct(d.Conversion)}");
            sb.AppendLine($"Comisiones (S/);{Num(d.Comisiones)}");
            sb.AppendLine($"Usuarios activos (90 días);{d.UsuariosActivos}");
            sb.AppendLine();
            sb.AppendLine("Semana;Ingresos (S/)");
            foreach (var s in d.IngresosPorSemana)
                sb.AppendLine($"{s.Etiqueta};{Num(s.Ingresos)}");
            sb.AppendLine();
            sb.AppendLine("Categoría;Entradas;Ingresos (S/)");
            foreach (var c in d.VentasPorCategoria)
                sb.AppendLine($"{Services.Comisiones.ComisionService.Celda(c.Categoria)};{c.Entradas};{Num(c.Ingresos)}");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv; charset=utf-8", $"dashboard-{d.Desde:yyyyMMdd}-{d.Hasta:yyyyMMdd}.csv");
        }

        // =============================
        // 👥 CLIENTES (HU22): ranking RFM y ficha por usuario
        // =============================
        private string AdministradorId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Ranking seudonimizado (código en lugar de nombre) con los filtros de HU29; la consulta también se audita (Ley 29733)
        public async Task<IActionResult> Clientes([FromQuery] FiltroClientes filtro, CancellationToken ct)
        {
            var clientes = await _analitica.FiltrarClientesAsync(filtro, ct);
            await _analitica.RegistrarAuditoriaAsync(AdministradorId, $"Filtro de clientes ({filtro.Descripcion()})", "Ordenes, Eventos", ct);

            ViewBag.Filtro = filtro;
            ViewBag.Categorias = await _catalogo.ObtenerCategoriasAsync(ct);
            return View(clientes);
        }

        // CSV del listado filtrado (solo códigos, sin nombres ni correos)
        public async Task<IActionResult> ExportarClientes([FromQuery] FiltroClientes filtro, CancellationToken ct)
        {
            var clientes = await _analitica.FiltrarClientesAsync(filtro, ct);
            await _analitica.RegistrarAuditoriaAsync(AdministradorId, $"Exportación de clientes ({filtro.Descripcion()})", "Ordenes, Eventos", ct);

            string Num(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
            var sb = new StringBuilder();
            sb.AppendLine($"Clientes ActivaFest;{filtro.Descripcion()}");
            sb.AppendLine("Posición;Cliente;Segmento;Gasto (S/);Entradas;Compras;Última compra;Acepta promociones");
            for (var i = 0; i < clientes.Count; i++)
            {
                var c = clientes[i];
                sb.AppendLine($"{i + 1};{c.Codigo};{c.Segmento};{Num(c.Gasto)};{c.Entradas};{c.Compras};{c.UltimaCompra:dd/MM/yyyy};{(c.AceptaPromociones ? "Sí" : "No")}");
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv; charset=utf-8", $"clientes-{DateTime.Now:yyyyMMdd}.csv");
        }

        public async Task<IActionResult> Cliente(string id, CancellationToken ct)
        {
            var ficha = await _analitica.FichaClienteAsync(id, AdministradorId, ct);
            return ficha == null ? NotFound() : View(ficha);
        }

        public async Task<IActionResult> Auditoria(CancellationToken ct) =>
            View(await _analitica.AuditoriaAsync(100, ct));

        // =============================
        // 📈 VARIABILIDAD Y SEMÁFORO DE DECISIÓN (HU23)
        // =============================
        public async Task<IActionResult> Variabilidad(DateTime? desde, DateTime? hasta, int? categoriaId, CancellationToken ct)
        {
            // Por defecto, las últimas 12 semanas: con un solo mes hay muy pocas semanas para medir la variabilidad
            var filtro = new FiltroAnalitica { Desde = desde ?? DateTime.Today.AddDays(-83), Hasta = hasta ?? DateTime.Today, CategoriaId = categoriaId };
            var reporte = await _analitica.VariabilidadAsync(filtro, ct);

            // Muestra clientes con gasto atípico (con código): la consulta se audita como el resto de la analítica individual
            await _analitica.RegistrarAuditoriaAsync(AdministradorId,
                $"Variabilidad y atípicos ({reporte.Desde:dd/MM/yyyy} - {reporte.Hasta:dd/MM/yyyy}{(categoriaId != null ? $", categoría {categoriaId}" : "")})",
                "Ordenes, Entradas, Eventos", ct);

            ViewBag.Categorias = await _catalogo.ObtenerCategoriasAsync(ct);
            return View(reporte);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Umbrales(UmbralesVariabilidad umbrales, DateTime? desde, DateTime? hasta, int? categoriaId, CancellationToken ct)
        {
            var error = await _analitica.GuardarUmbralesAsync(umbrales, AdministradorId, ct);
            if (error != null)
                TempData["Error"] = error;
            else
                TempData["Mensaje"] = "Umbrales del semáforo actualizados.";
            return RedirectToAction(nameof(Variabilidad), new { desde = desde?.ToString("yyyy-MM-dd"), hasta = hasta?.ToString("yyyy-MM-dd"), categoriaId });
        }

        // =============================
        // 💬 MÉTRICAS DEL ASISTENTE VIRTUAL (HU14 / OE3) Y ESTADO DE LLAMA 3.2
        // =============================
        public async Task<IActionResult> Asistente(int dias = 30, CancellationToken ct = default)
        {
            ViewBag.Llm = await _llm.EstadoAsync(ct);
            return View(await _asistente.MetricasAsync(dias, ct));
        }

        // =============================
        // 🤖 CONSULTAS A LA IA (HU24): solo lectura, cifras fuente y auditoría
        // =============================
        public record PreguntaIa(string Pregunta);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConsultarIA([FromBody] PreguntaIa datos, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(datos?.Pregunta))
                return BadRequest("Escribe una pregunta.");

            var r = await _ia.ConsultarAsync(datos.Pregunta, AdministradorId, ct);
            return Ok(new
            {
                texto = r.Texto,
                funcion = r.Funcion,
                periodo = r.Periodo,
                fueraDeAlcance = r.FueraDeAlcance,
                redactadaPorIa = r.RedactadaPorIa,
                tabla = r.Tabla == null ? null : new { titulo = r.Tabla.Titulo, columnas = r.Tabla.Columnas, filas = r.Tabla.Filas },
                grafico = r.Grafico == null || r.Grafico.Valores.Count == 0 ? null : r.Grafico,
                recomendaciones = r.Recomendaciones,
                sugerencias = r.Sugerencias
            });
        }
    }
}
