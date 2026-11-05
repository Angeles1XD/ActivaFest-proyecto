using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using ActivaFest.Data;
using ActivaFest.Models;

namespace ActivaFest.Services.Analitica;

public class FiltroAnalitica
{
    // Período por fecha de pago (fechas locales, "hasta" incluye todo el día)
    public DateTime Desde { get; set; }
    public DateTime Hasta { get; set; }
    public int? CategoriaId { get; set; }

    // Un solo evento y, para un organizador, solo sus eventos (null: toda la plataforma)
    public int? EventoId { get; set; }
    public string? OrganizadorId { get; set; }

    public static FiltroAnalitica MesActual(DateTime hoy) =>
        new() { Desde = new DateTime(hoy.Year, hoy.Month, 1), Hasta = new DateTime(hoy.Year, hoy.Month, 1).AddMonths(1).AddDays(-1) };

    public FiltroAnalitica Normalizado()
    {
        var desde = Desde.Date;
        var hasta = Hasta.Date;
        if (hasta < desde) (desde, hasta) = (hasta, desde);
        if ((hasta - desde).TotalDays > 366) desde = hasta.AddDays(-366);
        return new FiltroAnalitica { Desde = desde, Hasta = hasta, CategoriaId = CategoriaId, EventoId = EventoId, OrganizadorId = OrganizadorId };
    }

    public string Clave => $"{Desde:yyyyMMdd}-{Hasta:yyyyMMdd}-{CategoriaId}-{EventoId}-{OrganizadorId}";

    // Períodos rápidos del dashboard. Si vienen fechas, es un período personalizado.
    public static readonly (string Clave, string Nombre)[] Rangos =
    {
        ("hoy", "Hoy"), ("7d", "Últimos 7 días"), ("mes", "Este mes"), ("mespasado", "Mes pasado"),
        ("30d", "Últimos 30 días"), ("anio", "Este año"), ("personalizado", "Personalizado")
    };

    public static (FiltroAnalitica Filtro, string Rango) DeRango(string? rango, DateTime? desde, DateTime? hasta, int? categoriaId, DateTime hoy,
        int? eventoId = null, string? organizadorId = null)
    {
        hoy = hoy.Date;
        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);
        rango = Rangos.Any(r => r.Clave == rango) ? rango : desde != null || hasta != null ? "personalizado" : "mes";
        var (d, h) = rango switch
        {
            "hoy" => (hoy, hoy),
            "7d" => (hoy.AddDays(-6), hoy),
            "30d" => (hoy.AddDays(-29), hoy),
            "mespasado" => (inicioMes.AddMonths(-1), inicioMes.AddDays(-1)),
            "anio" => (new DateTime(hoy.Year, 1, 1), hoy),
            "personalizado" => (desde ?? inicioMes, hasta ?? hoy),
            _ => (inicioMes, hoy)   // este mes, hasta hoy (sin días futuros, para comparar con el período anterior)
        };
        return (new FiltroAnalitica { Desde = d, Hasta = h, CategoriaId = categoriaId, EventoId = eventoId, OrganizadorId = organizadorId }.Normalizado(), rango!);
    }
}

public record PuntoSemana(string Etiqueta, decimal Ingresos);
public record VentaCategoria(string Categoria, string? Color, int Entradas, decimal Ingresos);

// Compra reciente para el dashboard (el cliente va con su código anónimo, nunca con su nombre)
public record CompraReciente(DateTime Fecha, string Evento, string Cliente, int Entradas, decimal Total);

// =============================
// 🗓️ VENTAS POR PERÍODO DEL CALENDARIO (semana, quincena, mes y trimestre)
// =============================
// Semanas de lunes a domingo, quincenas del 1 al 15 y del 16 a fin de mes, trimestres de enero a marzo, etc.
public record PeriodoVentas(string Tipo, DateTime Desde, DateTime Hasta, string Etiqueta)
{
    private static readonly CultureInfo Peru = CultureInfo.GetCultureInfo("es-PE");

    public static readonly (string Clave, string Nombre)[] Tipos =
    {
        ("semana", "Esta semana"), ("quincena", "Esta quincena"), ("mes", "Este mes"), ("trimestre", "Este trimestre")
    };

    // El período del tipo indicado que contiene la fecha (un tipo desconocido se toma como semana)
    public static PeriodoVentas De(string? tipo, DateTime fecha)
    {
        fecha = fecha.Date;
        var inicioMes = new DateTime(fecha.Year, fecha.Month, 1);
        var finMes = inicioMes.AddMonths(1).AddDays(-1);
        var mes = fecha.ToString("MMMM 'de' yyyy", Peru);

        switch (tipo)
        {
            case "quincena":
                return fecha.Day <= 15
                    ? new("quincena", inicioMes, inicioMes.AddDays(14), $"1.ª quincena de {mes}")
                    : new("quincena", inicioMes.AddDays(15), finMes, $"2.ª quincena de {mes}");
            case "mes":
                return new("mes", inicioMes, finMes, Formato.Fecha(fecha, "MMMM 'de' yyyy"));
            case "trimestre":
                var trimestre = (fecha.Month - 1) / 3;
                var inicio = new DateTime(fecha.Year, trimestre * 3 + 1, 1);
                return new("trimestre", inicio, inicio.AddMonths(3).AddDays(-1), $"Trimestre {trimestre + 1} de {fecha.Year}");
            default:
                var lunes = fecha.AddDays(-(((int)fecha.DayOfWeek + 6) % 7));
                return new("semana", lunes, lunes.AddDays(6), $"Semana {ISOWeek.GetWeekOfYear(fecha)} de {ISOWeek.GetYear(fecha)}");
        }
    }

    public PeriodoVentas Anterior() => De(Tipo, Desde.AddDays(-1));
    public PeriodoVentas Siguiente() => De(Tipo, Hasta.AddDays(1));
}

public record TotalesVentas(decimal Ingresos, int Compras, int Entradas, decimal Comisiones);

// Tarjeta de un período en curso, con los ingresos del período anterior completo como referencia
public record ResumenPeriodo(string Tipo, string Nombre, string Etiqueta, TotalesVentas Totales, decimal IngresosAnterior);

public class ReporteVentas
{
    public PeriodoVentas Periodo { get; set; } = null!;
    public TotalesVentas Totales { get; set; } = null!;
    public decimal IngresosAnterior { get; set; }
    public List<ResumenPeriodo> Resumenes { get; set; } = new();

    // Una página de las ventas del período, de la más reciente a la más antigua
    public List<CompraReciente> Ventas { get; set; } = new();
    public int Pagina { get; set; }
    public int Paginas { get; set; }
}

// =============================
// 📊 INDICADORES DEL DASHBOARD (HU21)
// =============================
public class DashboardAnalitica
{
    public DateTime Desde { get; set; }
    public DateTime Hasta { get; set; }
    public int? CategoriaId { get; set; }
    public int? EventoId { get; set; }
    public DateTime CalculadoEn { get; set; }

    // Del período (por fecha de pago)
    public decimal Ingresos { get; set; }
    public int EntradasVendidas { get; set; }
    public int Compras { get; set; }
    public decimal Comisiones { get; set; }
    public decimal TicketPromedio => Compras == 0 ? 0 : Math.Round(Ingresos / Compras, 2);

    // Período anterior de igual duración (para "+12 % vs. el período anterior")
    public decimal IngresosAnterior { get; set; }
    public int EntradasAnterior { get; set; }
    public decimal TicketAnterior { get; set; }

    // De los eventos que vendieron en el período o que se realizan en él (por fecha del evento)
    public int EventosConsiderados { get; set; }
    public int EventosRealizados { get; set; }
    public int AforoEventos { get; set; }
    public int VendidasEventos { get; set; }
    public int VendidasEventosRealizados { get; set; }
    public int UsadasEventosRealizados { get; set; }
    public int ComprasEventos { get; set; }
    public int VisitasEventos { get; set; }

    public double? Ocupacion => AforoEventos == 0 ? null : Math.Round(100.0 * VendidasEventos / AforoEventos, 1);
    public double? Asistencia => VendidasEventosRealizados == 0 ? null : Math.Round(100.0 * UsadasEventosRealizados / VendidasEventosRealizados, 1);
    public double? Conversion => VisitasEventos == 0 ? null : Math.Round(100.0 * ComprasEventos / VisitasEventos, 1);

    // Usuarios con al menos una compra en los 90 días previos al fin del período
    public int UsuariosActivos { get; set; }

    public List<PuntoSemana> IngresosPorSemana { get; set; } = new();
    public List<VentaCategoria> VentasPorCategoria { get; set; } = new();

    // Las 10 compras pagadas más recientes del período (se actualizan en cuanto se paga una compra)
    public List<CompraReciente> UltimasCompras { get; set; } = new();

    public static double? Variacion(decimal actual, decimal anterior) =>
        anterior == 0 ? null : Math.Round((double)((actual - anterior) / anterior * 100), 1);
}

public interface IAnaliticaService
{
    Task<DashboardAnalitica> DashboardAsync(FiltroAnalitica filtro, CancellationToken ct = default);

    // Gráficos, totales y recomendaciones del dashboard: la misma fuente que usa la IA
    Task<AnalisisVentas> AnalisisAsync(FiltroAnalitica filtro, CancellationToken ct = default);

    // Ventas de una semana, quincena, mes o trimestre, con las tarjetas de los períodos en curso a la fecha "hoy"
    Task<ReporteVentas> VentasPeriodoAsync(PeriodoVentas periodo, int? categoriaId, int pagina, DateTime hoy, CancellationToken ct = default, int? eventoId = null);

    // HU22: clientes (usuarios con compras pagadas) con su segmento RFM, ordenados por gasto
    Task<List<ClienteAnalitica>> ClientesAsync(CancellationToken ct = default);

    // HU29: filtros combinables (gasto, entradas, compras, período, categoría y segmento), de mayor a menor
    Task<List<ClienteAnalitica>> FiltrarClientesAsync(FiltroClientes filtro, CancellationToken ct = default);

    // HU22: ficha analítica de un cliente; cada acceso queda en AUDITORIA_ANALITICA
    Task<FichaCliente?> FichaClienteAsync(string usuarioId, string administradorId, CancellationToken ct = default);

    Task RegistrarAuditoriaAsync(string administradorId, string consulta, string fuenteDatos, CancellationToken ct = default);

    Task<List<AuditoriaAnalitica>> AuditoriaAsync(int maximo = 50, CancellationToken ct = default);

    // =============================
    // 🔎 CONSULTAS DE SOLO LECTURA PARA LA IA (HU24) Y LA VARIABILIDAD (HU23)
    // =============================
    // "alcance": categoría, evento u organizador que limita los datos (null: toda la plataforma)
    Task<List<VentaEvento>> TopEventosAsync(DateTime desde, DateTime hasta, int n, CancellationToken ct = default, FiltroAnalitica? alcance = null);
    Task<List<VentaCategoriaPeriodo>> IngresosPorCategoriaAsync(DateTime desde, DateTime hasta, CancellationToken ct = default, FiltroAnalitica? alcance = null);
    Task<List<VariabilidadEvento>> VariabilidadPorEventoAsync(DateTime desde, DateTime hasta, CancellationToken ct = default, FiltroAnalitica? alcance = null);
    Task<List<ResumenSegmento>> ResumenSegmentosAsync(CancellationToken ct = default);
    Task<List<EventoOcupacion>> EventosBajaOcupacionAsync(int dias, double umbral, CancellationToken ct = default, FiltroAnalitica? alcance = null);

    // Eventos según su estado, para listarlos ("los que ya aprobé", "los pendientes"…)
    Task<List<EventoListado>> ListaEventosAsync(FiltroAnalitica alcance, string? estado, DateTime? desde, DateTime? hasta, CancellationToken ct = default);

    // Eventos para elegir en los filtros y para reconocer un nombre en una pregunta (organizadorId: solo los suyos)
    Task<List<OpcionEvento>> OpcionesEventoAsync(string? organizadorId, CancellationToken ct = default);
    Task<List<OpcionCategoria>> OpcionesCategoriaAsync(CancellationToken ct = default);

    // HU23: variabilidad de ventas, precios y gasto con semáforo de decisión (umbrales configurables)
    Task<ReporteVariabilidad> VariabilidadAsync(FiltroAnalitica filtro, CancellationToken ct = default);
    Task<UmbralesVariabilidad> UmbralesAsync(CancellationToken ct = default);
    Task<string?> GuardarUmbralesAsync(UmbralesVariabilidad umbrales, string administradorId, CancellationToken ct = default);
}

// =============================
// 📈 ANALÍTICA DEL ADMINISTRADOR (HU21, HU22, HU29)
// =============================
// Consultas LINQ de solo lectura sobre EF Core. El dashboard se guarda en caché (Redis o memoria) 5 minutos
// para cumplir el tiempo de carga (RNF02); por eso muestra cuándo se calculó.
public class AnaliticaService : IAnaliticaService
{
    public const int SemanasTendencia = 10;
    public const int VentasPorPagina = 20;
    public const int EventosEnAnalisis = 10;
    public const int CompradoresEnAnalisis = 5;
    public const int ZonasEnAnalisis = 8;
    public static readonly DateTime InicioSerieHistorica = new(2026, 7, 1);
    private static readonly TimeSpan DuracionCache = TimeSpan.FromMinutes(5);

    private readonly ApplicationDbContext _db;
    private readonly IDistributedCache _cache;

    public AnaliticaService(ApplicationDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<DashboardAnalitica> DashboardAsync(FiltroAnalitica filtro, CancellationToken ct = default)
    {
        filtro = filtro.Normalizado();
        // La clave incluye una "huella" de los datos: una compra pagada o una entrada validada invalida la caché al instante
        var clave = "analitica:dashboard:" + filtro.Clave + ":" + await HuellaAsync(ct);

        var enCache = await _cache.GetStringAsync(clave, ct);
        if (enCache != null)
            return JsonSerializer.Deserialize<DashboardAnalitica>(enCache)!;

        var d = await CalcularAsync(filtro, ct);
        await _cache.SetStringAsync(clave, JsonSerializer.Serialize(d),
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(DuracionCache), ct);
        return d;
    }

    // Órdenes pagadas del período dentro del alcance del filtro: categoría, evento y organizador
    private IQueryable<Orden> PagadasDe(DateTime desde, DateTime hastaInclusive, FiltroAnalitica f)
    {
        var q = Pagadas(desde, hastaInclusive, f.CategoriaId);
        if (f.EventoId != null)
            q = q.Where(o => o.EventoId == f.EventoId);
        return f.OrganizadorId == null ? q : q.Where(o => o.Evento!.OrganizadorId == f.OrganizadorId);
    }

    private IQueryable<Evento> EventosDe(FiltroAnalitica f)
    {
        var q = _db.Eventos.AsNoTracking();
        if (f.CategoriaId != null)
            q = q.Where(e => e.CategoriaId == f.CategoriaId);
        if (f.EventoId != null)
            q = q.Where(e => e.Id == f.EventoId);
        return f.OrganizadorId == null ? q : q.Where(e => e.OrganizadorId == f.OrganizadorId);
    }

    // Órdenes pagadas en un rango de fechas locales (FechaPago se guarda en UTC)
    private IQueryable<Orden> Pagadas(DateTime desde, DateTime hastaInclusive, int? categoriaId)
    {
        var desdeUtc = desde.Date.ToUniversalTime();
        var hastaUtc = hastaInclusive.Date.AddDays(1).ToUniversalTime();
        var q = _db.Ordenes.AsNoTracking()
            .Where(o => o.Estado == EstadoOrden.Pagada && o.FechaPago >= desdeUtc && o.FechaPago < hastaUtc);
        return categoriaId == null ? q : q.Where(o => o.Evento!.CategoriaId == categoriaId);
    }

    private async Task<DashboardAnalitica> CalcularAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var d = new DashboardAnalitica { Desde = f.Desde, Hasta = f.Hasta, CategoriaId = f.CategoriaId, EventoId = f.EventoId, CalculadoEn = DateTime.Now };

        // ---------- Ventas del período ----------
        var periodo = PagadasDe(f.Desde, f.Hasta, f);
        d.Compras = await periodo.CountAsync(ct);
        d.Ingresos = await periodo.SumAsync(o => (decimal?)o.Total, ct) ?? 0;
        d.Comisiones = await periodo.SumAsync(o => (decimal?)o.Comision, ct) ?? 0;
        d.EntradasVendidas = await periodo.SelectMany(o => o.Items).SumAsync(i => (int?)i.Cantidad, ct) ?? 0;

        var dias = (f.Hasta - f.Desde).Days + 1;
        var anterior = PagadasDe(f.Desde.AddDays(-dias), f.Desde.AddDays(-1), f);
        var comprasAnterior = await anterior.CountAsync(ct);
        d.IngresosAnterior = await anterior.SumAsync(o => (decimal?)o.Total, ct) ?? 0;
        d.EntradasAnterior = await anterior.SelectMany(o => o.Items).SumAsync(i => (int?)i.Cantidad, ct) ?? 0;
        d.TicketAnterior = comprasAnterior == 0 ? 0 : Math.Round(d.IngresosAnterior / comprasAnterior, 2);

        // ---------- Ocupación, asistencia y conversión ----------
        // De los eventos que vendieron en el período o que se realizan en él: así una compra de hoy para un evento
        // del próximo mes también cuenta en la ocupación (antes solo se miraban los eventos con fecha en el período)
        var hastaEvento = f.Hasta.AddDays(1);
        var conVentas = periodo.Select(o => o.EventoId);
        var eventos = EventosDe(f)
            .Where(e => e.Estado == EstadoEvento.Aprobado && ((e.Fecha >= f.Desde && e.Fecha < hastaEvento) || conVentas.Contains(e.Id)));

        var ahora = DateTime.Now;
        var porEvento = await eventos
            .Select(e => new
            {
                e.Fecha,
                e.Visitas,
                Aforo = e.TiposEntrada.Sum(t => t.Capacidad),
                Vendidas = _db.Entradas.Count(x => x.EventoId == e.Id && x.Estado != EstadoEntrada.Anulada),
                Usadas = _db.Entradas.Count(x => x.EventoId == e.Id && x.Estado == EstadoEntrada.Usada),
                Compras = _db.Ordenes.Count(o => o.EventoId == e.Id && o.Estado == EstadoOrden.Pagada)
            })
            .ToListAsync(ct);

        d.EventosConsiderados = porEvento.Count;
        d.EventosRealizados = porEvento.Count(e => e.Fecha <= ahora);
        d.AforoEventos = porEvento.Sum(e => e.Aforo);
        d.VendidasEventos = porEvento.Sum(e => e.Vendidas);
        d.VendidasEventosRealizados = porEvento.Where(e => e.Fecha <= ahora).Sum(e => e.Vendidas);
        d.UsadasEventosRealizados = porEvento.Where(e => e.Fecha <= ahora).Sum(e => e.Usadas);
        d.ComprasEventos = porEvento.Sum(e => e.Compras);
        d.VisitasEventos = porEvento.Sum(e => e.Visitas);

        // ---------- Usuarios activos (compra en los últimos 90 días) ----------
        d.UsuariosActivos = await PagadasDe(f.Hasta.AddDays(-89), f.Hasta, f).Select(o => o.UsuarioId).Distinct().CountAsync(ct);

        // ---------- Ingresos por semana (últimas 10 semanas hasta el fin del período) ----------
        var finSemana = f.Hasta.Date;
        var inicioTendencia = finSemana.AddDays(-7 * SemanasTendencia + 1);
        var pagos = await PagadasDe(inicioTendencia, finSemana, f)
            .Select(o => new { o.FechaPago, o.Total })
            .ToListAsync(ct);
        for (var s = 0; s < SemanasTendencia; s++)
        {
            var ini = inicioTendencia.AddDays(7 * s);
            var fin = ini.AddDays(7);
            var semana = ISOWeek.GetWeekOfYear(ini);
            d.IngresosPorSemana.Add(new PuntoSemana($"S{semana}",
                pagos.Where(p => p.FechaPago!.Value.ToLocalTime() >= ini && p.FechaPago.Value.ToLocalTime() < fin).Sum(p => p.Total)));
        }

        // ---------- Ventas por categoría en el período (sin el filtro de categoría, para comparar) ----------
        var sinCategoria = new FiltroAnalitica { EventoId = f.EventoId, OrganizadorId = f.OrganizadorId };
        var porCategoria = await PagadasDe(f.Desde, f.Hasta, sinCategoria)
            .SelectMany(o => o.Items, (o, i) => new { o.Evento!.CategoriaId, i.Cantidad, Subtotal = i.PrecioUnitario * i.Cantidad })
            .GroupBy(x => x.CategoriaId)
            .Select(g => new { CategoriaId = g.Key, Entradas = g.Sum(x => x.Cantidad), Ingresos = g.Sum(x => x.Subtotal) })
            .ToListAsync(ct);
        var categorias = await _db.Categorias.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        d.VentasPorCategoria = porCategoria
            .Select(c => new VentaCategoria(categorias[c.CategoriaId].Nombre, categorias[c.CategoriaId].Color, c.Entradas, c.Ingresos))
            .OrderByDescending(c => c.Entradas)
            .ToList();

        // ---------- Últimas compras del período ----------
        d.UltimasCompras = (await periodo
                .OrderByDescending(o => o.FechaPago)
                .Take(10)
                .Select(o => new { o.FechaPago, Evento = o.Evento!.Titulo, o.UsuarioId, Entradas = o.Items.Sum(i => i.Cantidad), o.Total })
                .ToListAsync(ct))
            .Select(o => new CompraReciente(o.FechaPago!.Value.ToLocalTime(), o.Evento, ClienteAnalitica.CodigoDe(o.UsuarioId), o.Entradas, o.Total))
            .ToList();

        return d;
    }

    public async Task<ReporteVentas> VentasPeriodoAsync(PeriodoVentas periodo, int? categoriaId, int pagina, DateTime hoy, CancellationToken ct = default, int? eventoId = null)
    {
        // Misma caché que el dashboard: una compra pagada cambia la huella y se ve al instante
        var clave = $"analitica:ventas:{periodo.Tipo}:{periodo.Desde:yyyyMMdd}:{categoriaId}:{eventoId}:{pagina}:{hoy:yyyyMMdd}:{await HuellaAsync(ct)}";
        var enCache = await _cache.GetStringAsync(clave, ct);
        if (enCache != null)
            return JsonSerializer.Deserialize<ReporteVentas>(enCache)!;

        var r = await CalcularVentasAsync(periodo, new FiltroAnalitica { CategoriaId = categoriaId, EventoId = eventoId }, pagina, hoy, ct);
        await _cache.SetStringAsync(clave, JsonSerializer.Serialize(r),
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(DuracionCache), ct);
        return r;
    }

    private async Task<ReporteVentas> CalcularVentasAsync(PeriodoVentas periodo, FiltroAnalitica alcance, int pagina, DateTime hoy, CancellationToken ct)
    {
        var r = new ReporteVentas { Periodo = periodo };
        r.Totales = await TotalesAsync(periodo, alcance, ct);
        r.IngresosAnterior = (await TotalesAsync(periodo.Anterior(), alcance, ct)).Ingresos;

        foreach (var (clave, nombre) in PeriodoVentas.Tipos)
        {
            var actual = PeriodoVentas.De(clave, hoy);
            r.Resumenes.Add(new ResumenPeriodo(clave, nombre, actual.Etiqueta, await TotalesAsync(actual, alcance, ct),
                (await TotalesAsync(actual.Anterior(), alcance, ct)).Ingresos));
        }

        r.Paginas = Math.Max(1, (r.Totales.Compras + VentasPorPagina - 1) / VentasPorPagina);
        r.Pagina = Math.Clamp(pagina, 1, r.Paginas);
        r.Ventas = (await PagadasDe(periodo.Desde, periodo.Hasta, alcance)
                .OrderByDescending(o => o.FechaPago).ThenByDescending(o => o.Id)
                .Skip((r.Pagina - 1) * VentasPorPagina)
                .Take(VentasPorPagina)
                .Select(o => new { o.FechaPago, Evento = o.Evento!.Titulo, o.UsuarioId, Entradas = o.Items.Sum(i => i.Cantidad), o.Total })
                .ToListAsync(ct))
            .Select(o => new CompraReciente(o.FechaPago!.Value.ToLocalTime(), o.Evento, ClienteAnalitica.CodigoDe(o.UsuarioId), o.Entradas, o.Total))
            .ToList();
        return r;
    }

    private async Task<TotalesVentas> TotalesAsync(PeriodoVentas periodo, FiltroAnalitica alcance, CancellationToken ct)
    {
        var ventas = PagadasDe(periodo.Desde, periodo.Hasta, alcance);
        return new TotalesVentas(
            await ventas.SumAsync(o => (decimal?)o.Total, ct) ?? 0,
            await ventas.CountAsync(ct),
            await ventas.SelectMany(o => o.Items).SumAsync(i => (int?)i.Cantidad, ct) ?? 0,
            await ventas.SumAsync(o => (decimal?)o.Comision, ct) ?? 0);
    }

    // =============================
    // 📈 ANÁLISIS DE VENTAS (gráficos del dashboard y respuestas de la IA)
    // =============================
    public async Task<AnalisisVentas> AnalisisAsync(FiltroAnalitica filtro, CancellationToken ct = default)
    {
        filtro = filtro.Normalizado();
        var contratos = await _db.ContratosDestacado.AsNoTracking().CountAsync(ct);
        var clave = $"analitica:analisis:{filtro.Clave}:{DateTime.Today:yyyyMMdd}:{contratos}:{await HuellaAsync(ct)}";

        var enCache = await _cache.GetStringAsync(clave, ct);
        if (enCache != null)
            return JsonSerializer.Deserialize<AnalisisVentas>(enCache)!;

        var a = await CalcularAnalisisAsync(filtro, ct);
        await _cache.SetStringAsync(clave, JsonSerializer.Serialize(a),
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(DuracionCache), ct);
        return a;
    }

    private async Task<AnalisisVentas> CalcularAnalisisAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var d = await DashboardAsync(f, ct);
        var a = new AnalisisVentas
        {
            Desde = f.Desde, Hasta = f.Hasta, CategoriaId = f.CategoriaId, EventoId = f.EventoId, ParaOrganizador = f.OrganizadorId != null, CalculadoEn = DateTime.Now,
            Ingresos = d.Ingresos, IngresosAnterior = d.IngresosAnterior, Comisiones = d.Comisiones, Compras = d.Compras,
            Entradas = d.EntradasVendidas, TicketPromedio = d.TicketPromedio, Conversion = d.Conversion, VisitasEventos = d.VisitasEventos
        };

        a.Acumulado = await AcumuladoAsync(f, ct);
        a.Meses = await MesesAsync(f, ct);
        a.DiasSemana = await DiasSemanaAsync(f, ct);
        (a.VentasDiarias, a.ComprasPorHora) = await SeriesDelPeriodoAsync(f, ct);

        // ---------- Compradores únicos: nuevos y recurrentes dentro del período ----------
        var periodo = PagadasDe(f.Desde, f.Hasta, f);
        var frecuenciaCompradores = await periodo
            .GroupBy(o => o.UsuarioId)
            .Select(g => new { UsuarioId = g.Key, Compras = g.Count() })
            .ToListAsync(ct);
        a.Compradores = frecuenciaCompradores.Count;
        a.CompradoresRecurrentes = frecuenciaCompradores.Count(c => c.Compras > 1);

        // ---------- Eventos destacados, eventos y categorías con más ventas ----------
        a.Destacados = await DestacadosAsync(f, ct);
        var destacados = a.Destacados.Select(x => x.EventoId).ToHashSet();
        var porEvento = (await periodo
                .GroupBy(o => new { o.EventoId, o.Evento!.Titulo, o.Evento.CategoriaId, Categoria = o.Evento.Categoria!.Nombre, o.Evento.Aforo })
                .Select(g => new
                {
                    g.Key.EventoId, g.Key.Titulo, g.Key.CategoriaId, g.Key.Categoria, g.Key.Aforo, Compras = g.Count(),
                    Entradas = g.SelectMany(o => o.Items).Sum(i => i.Cantidad), Ingresos = g.Sum(o => o.Total), Comision = g.Sum(o => o.Comision)
                })
                .ToListAsync(ct))
            .OrderByDescending(e => e.Ingresos).ThenByDescending(e => e.Entradas)
            .ToList();
        a.TopEventos = porEvento.Take(EventosEnAnalisis)
            .Select(e => new EventoAnalisis(e.EventoId, e.Titulo, e.Categoria, e.Compras, e.Entradas, e.Ingresos, e.Comision, e.Aforo, destacados.Contains(e.EventoId)))
            .ToList();
        a.PorCategoria = porEvento.GroupBy(e => new { e.CategoriaId, e.Categoria })
            .Select(g => new CategoriaAnalisis(g.Key.CategoriaId, g.Key.Categoria, g.Sum(e => e.Ingresos), g.Sum(e => e.Comision)))
            .OrderByDescending(c => c.Ingresos)
            .ToList();

        // ---------- Distribuciones: estado de los eventos, zonas, organizadores y órdenes ----------
        a.EventosPorEstado = await EventosPorEstadoAsync(f, ct);
        a.OrdenesPorEstado = await OrdenesPorEstadoAsync(f, ct);

        var porZona = await periodo
            .SelectMany(o => o.Items, (o, i) => new { Zona = i.TipoEntrada!.Nombre, i.Cantidad, i.PrecioUnitario })
            .ToListAsync(ct);
        a.PorZona = porZona.GroupBy(z => z.Zona)
            .Select(g => new ZonaAnalisis(g.Key, g.Sum(z => z.Cantidad), g.Sum(z => z.PrecioUnitario * z.Cantidad)))
            .OrderByDescending(z => z.Entradas)
            .Take(ZonasEnAnalisis)
            .ToList();

        // Los rankings de organizadores y de clientes son de toda la plataforma: un organizador no los ve
        if (f.OrganizadorId == null)
        {
            var porOrganizador = await periodo
                .GroupBy(o => new { o.Evento!.OrganizadorId, Nombre = o.Evento.Organizador!.NombreCompleto })
                .Select(g => new { g.Key.Nombre, Eventos = g.Select(o => o.EventoId).Distinct().Count(), Ingresos = g.Sum(o => o.Total) })
                .ToListAsync(ct);
            a.TopOrganizadores = porOrganizador
                .Select(o => new OrganizadorAnalisis(string.IsNullOrWhiteSpace(o.Nombre) ? "Sin nombre" : o.Nombre, o.Eventos, o.Ingresos))
                .OrderByDescending(o => o.Ingresos)
                .Take(CompradoresEnAnalisis)
                .ToList();

            // ---------- Clientes ----------
            a.TopCompradores = (await FiltrarClientesAsync(new FiltroClientes { Desde = f.Desde, Hasta = f.Hasta, CategoriaId = f.CategoriaId, EventoId = f.EventoId }, ct))
                .Take(CompradoresEnAnalisis)
                .Select(c => new CompradorAnalisis(c.Codigo, c.Segmento, c.Compras, c.Entradas, c.Gasto))
                .ToList();
            a.Segmentos = await ResumenSegmentosAsync(ct);

            // ---------- Asistencia por rango de edad ----------
            await CalcularEdadesAsync(a, f, ct);
        }

        // ---------- Próximos eventos con baja ocupación ----------
        a.BajaOcupacion = await EventosBajaOcupacionAsync(30, 0.4, ct, f);

        a.Recomendaciones = Recomendador.De(a);
        return a;
    }

    // Por fecha del evento y no de la compra: solo en un evento ya realizado se sabe quién asistió.
    // Cada entrada cuenta con la edad de la cuenta que la compró (no se sabe quién usó las entradas de un grupo).
    private async Task CalcularEdadesAsync(AnalisisVentas a, FiltroAnalitica f, CancellationToken ct)
    {
        var ahora = DateTime.Now;
        var finPeriodo = f.Hasta.AddDays(1);
        var fin = finPeriodo < ahora ? finPeriodo : ahora;
        var eventos = EventosDe(f).Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha >= f.Desde && e.Fecha < fin);

        var entradas = await (
                from entrada in _db.Entradas.AsNoTracking()
                join evento in eventos on entrada.EventoId equals evento.Id
                join usuario in _db.Users.AsNoTracking() on entrada.UsuarioId equals usuario.Id
                where entrada.Estado != EstadoEntrada.Anulada
                select new { usuario.Id, usuario.FechaNacimiento, evento.Fecha, Categoria = evento.Categoria!.Nombre, entrada.Estado })
            .ToListAsync(ct);

        a.EntradasSinEdad = entradas.Count(e => e.FechaNacimiento == null);

        var porRango = entradas.Where(e => e.FechaNacimiento != null)
            .ToLookup(e => RangoEdad.De(EdadUsuario.En(e.FechaNacimiento!.Value, e.Fecha)));

        a.PorEdad = RangoEdad.Todos.Where(r => porRango[r].Any()).Select(r =>
        {
            var delRango = porRango[r].ToList();
            var categorias = delRango.GroupBy(e => e.Categoria)
                .Select(g => new CategoriaEdad(g.Key, g.Count(e => e.Estado == EstadoEntrada.Usada)))
                .OrderByDescending(c => c.Asistieron).ThenBy(c => c.Categoria)
                .ToList();
            return new EdadAnalisis(r, delRango.Select(e => e.Id).Distinct().Count(), delRango.Count,
                delRango.Count(e => e.Estado == EstadoEntrada.Usada), categorias);
        }).ToList();
    }

    private async Task<TotalesPlataforma> AcumuladoAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var ventas = _db.Ordenes.AsNoTracking().Where(o => o.Estado == EstadoOrden.Pagada);
        if (f.CategoriaId != null)
            ventas = ventas.Where(o => o.Evento!.CategoriaId == f.CategoriaId);
        if (f.EventoId != null)
            ventas = ventas.Where(o => o.EventoId == f.EventoId);
        if (f.OrganizadorId != null)
            ventas = ventas.Where(o => o.Evento!.OrganizadorId == f.OrganizadorId);
        var eventos = EventosDe(f).Where(e => e.Estado == EstadoEvento.Aprobado);

        return new TotalesPlataforma(
            await ventas.SumAsync(o => (decimal?)o.Total, ct) ?? 0,
            await ventas.SumAsync(o => (decimal?)o.Comision, ct) ?? 0,
            await ventas.CountAsync(ct),
            await ventas.SelectMany(o => o.Items).SumAsync(i => (int?)i.Cantidad, ct) ?? 0,
            await ventas.Select(o => o.UsuarioId).Distinct().CountAsync(ct),
            await eventos.CountAsync(ct),
            f.OrganizadorId != null ? 0 : await _db.Users.AsNoTracking().CountAsync(ct));
    }

    private async Task<List<ConteoEtiqueta>> EventosPorEstadoAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var eventos = await EventosDe(f).Select(e => new { e.Estado, e.Fecha }).ToListAsync(ct);

        var ahora = DateTime.Now;
        return new List<ConteoEtiqueta>
        {
            new("Publicados", eventos.Count(e => e.Estado == EstadoEvento.Aprobado && e.Fecha > ahora)),
            new("Realizados", eventos.Count(e => e.Estado == EstadoEvento.Aprobado && e.Fecha <= ahora)),
            new("Pendientes", eventos.Count(e => e.Estado == EstadoEvento.Pendiente)),
            new("Rechazados", eventos.Count(e => e.Estado == EstadoEvento.Rechazado)),
            new("Cancelados", eventos.Count(e => e.Estado == EstadoEvento.Cancelado))
        };
    }

    private async Task<List<ConteoEtiqueta>> OrdenesPorEstadoAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var desdeUtc = f.Desde.Date.ToUniversalTime();
        var hastaUtc = f.Hasta.Date.AddDays(1).ToUniversalTime();
        var ordenes = _db.Ordenes.AsNoTracking().Where(o => o.FechaCreacion >= desdeUtc && o.FechaCreacion < hastaUtc);
        if (f.CategoriaId != null)
            ordenes = ordenes.Where(o => o.Evento!.CategoriaId == f.CategoriaId);
        if (f.EventoId != null)
            ordenes = ordenes.Where(o => o.EventoId == f.EventoId);
        if (f.OrganizadorId != null)
            ordenes = ordenes.Where(o => o.Evento!.OrganizadorId == f.OrganizadorId);

        var porEstado = await ordenes.GroupBy(o => o.Estado).Select(g => new { Estado = g.Key, Total = g.Count() }).ToListAsync(ct);
        return new (EstadoOrden Estado, string Nombre)[]
            {
                (EstadoOrden.Pagada, "Pagadas"), (EstadoOrden.PendientePago, "Reservas activas"), (EstadoOrden.Expirada, "Vencidas"),
                (EstadoOrden.Cancelada, "Canceladas"), (EstadoOrden.Rechazada, "Rechazadas"), (EstadoOrden.RequiereReembolso, "Por reembolsar")
            }
            .Select(x => new ConteoEtiqueta(x.Nombre, porEstado.FirstOrDefault(p => p.Estado == x.Estado)?.Total ?? 0))
            .ToList();
    }

    // Serie histórica fija desde julio de 2026 hasta el mes actual; solo incluye pagos hasta hoy.
    private async Task<List<PuntoMes>> MesesAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var hoy = DateTime.Today;
        if (hoy < InicioSerieHistorica)
            return new List<PuntoMes>();

        var ultimoMes = new DateTime(hoy.Year, hoy.Month, 1);
        var cantidadMeses = (ultimoMes.Year - InicioSerieHistorica.Year) * 12
            + ultimoMes.Month - InicioSerieHistorica.Month + 1;
        var pagos = await PagadasDe(InicioSerieHistorica, hoy, f)
            .Select(o => new { o.FechaPago, o.Total, o.Comision })
            .ToListAsync(ct);
        var porMes = pagos.ToLookup(p => { var l = p.FechaPago!.Value.ToLocalTime(); return new DateTime(l.Year, l.Month, 1); });

        return Enumerable.Range(0, cantidadMeses)
            .Select(i => InicioSerieHistorica.AddMonths(i))
            .Select(m => new PuntoMes(m.ToString("MMM yy", CultureInfo.GetCultureInfo("es-PE")).Replace(".", ""), porMes[m].Sum(p => p.Total), porMes[m].Sum(p => p.Comision)))
            .ToList();
    }

    // Ventas de cada día del período, con el acumulado, y compras por hora del día
    private async Task<(List<PuntoDia>, List<PuntoHora>)> SeriesDelPeriodoAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var pagos = (await PagadasDe(f.Desde, f.Hasta, f)
                .Select(o => new { o.FechaPago, o.Total, Entradas = o.Items.Sum(i => i.Cantidad) })
                .ToListAsync(ct))
            .Select(o => new { Fecha = o.FechaPago!.Value.ToLocalTime(), o.Total, o.Entradas })
            .ToList();

        var porDia = pagos.ToLookup(p => p.Fecha.Date);
        var fin = f.Hasta < DateTime.Today ? f.Hasta : DateTime.Today;
        var acumulado = 0m;
        var dias = new List<PuntoDia>();
        for (var d = f.Desde.Date; d <= fin; d = d.AddDays(1))
        {
            var ingresos = porDia[d].Sum(p => p.Total);
            acumulado += ingresos;
            dias.Add(new PuntoDia(d.ToString("dd/MM", CultureInfo.InvariantCulture), porDia[d].Sum(p => p.Entradas), ingresos, acumulado));
        }

        var porHora = pagos.ToLookup(p => p.Fecha.Hour);
        return (dias, Enumerable.Range(0, 24).Select(h => new PuntoHora($"{h:00}:00", porHora[h].Count())).ToList());
    }

    private async Task<List<PuntoDiaSemana>> DiasSemanaAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var hoy = DateTime.Today;
        if (hoy < InicioSerieHistorica)
            return new List<PuntoDiaSemana>();

        var pagos = await PagadasDe(InicioSerieHistorica, hoy, f)
            .Select(o => new { o.FechaPago, o.Total })
            .ToListAsync(ct);
        var porDia = pagos.ToLookup(p => p.FechaPago!.Value.ToLocalTime().DayOfWeek);

        var nombres = new[] { "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo" };
        return nombres.Select((nombre, i) => (nombre, dia: (DayOfWeek)((i + 1) % 7)))
            .Select(x => new PuntoDiaSemana(x.nombre, porDia[x.dia].Count(), porDia[x.dia].Sum(p => p.Total)))
            .ToList();
    }

    // Contratos pagados con vigencia en el período y lo que vendió cada evento mientras estuvo destacado
    private async Task<List<DestacadoAnalisis>> DestacadosAsync(FiltroAnalitica f, CancellationToken ct)
    {
        var finPeriodo = f.Hasta.Date.AddDays(1);
        var contratos = _db.ContratosDestacado.AsNoTracking()
            .Where(c => c.Estado == EstadoContrato.Activo && c.FechaInicio < finPeriodo && c.FechaFin >= f.Desde);
        if (f.CategoriaId != null)
            contratos = contratos.Where(c => c.Evento!.CategoriaId == f.CategoriaId);
        if (f.EventoId != null)
            contratos = contratos.Where(c => c.EventoId == f.EventoId);
        if (f.OrganizadorId != null)
            contratos = contratos.Where(c => c.Evento!.OrganizadorId == f.OrganizadorId);

        var lista = await contratos
            .Select(c => new { c.EventoId, Evento = c.Evento!.Titulo, Paquete = c.Paquete!.Nombre, c.Precio, Inicio = c.FechaInicio!.Value, Fin = c.FechaFin!.Value })
            .ToListAsync(ct);
        if (lista.Count == 0)
            return new List<DestacadoAnalisis>();

        var eventos = lista.Select(c => c.EventoId).Distinct().ToList();
        var ventas = await PagadasDe(f.Desde, f.Hasta, f)
            .Where(o => eventos.Contains(o.EventoId))
            .Select(o => new { o.EventoId, o.FechaPago, o.Total, Entradas = o.Items.Sum(i => i.Cantidad) })
            .ToListAsync(ct);

        return lista
            .Select(c =>
            {
                var durante = ventas.Where(v => v.EventoId == c.EventoId && v.FechaPago!.Value.ToLocalTime() >= c.Inicio && v.FechaPago.Value.ToLocalTime() < c.Fin).ToList();
                return new DestacadoAnalisis(c.EventoId, c.Evento, c.Paquete, $"{c.Inicio:dd/MM/yyyy} – {c.Fin:dd/MM/yyyy}", c.Precio, durante.Sum(v => v.Entradas), durante.Sum(v => v.Total));
            })
            .OrderByDescending(c => c.Ingresos)
            .ToList();
    }

    // Cambia cuando hay una compra pagada nueva, una entrada validada en puerta o un evento aprobado o cancelado
    private async Task<string> HuellaAsync(CancellationToken ct)
    {
        var pagadas = _db.Ordenes.AsNoTracking().Where(o => o.Estado == EstadoOrden.Pagada);
        var cantidad = await pagadas.CountAsync(ct);
        var ultima = await pagadas.MaxAsync(o => o.FechaPago, ct);
        var usadas = await _db.Entradas.AsNoTracking().CountAsync(e => e.Estado == EstadoEntrada.Usada, ct);
        var aprobados = await _db.Eventos.AsNoTracking().CountAsync(e => e.Estado == EstadoEvento.Aprobado, ct);
        return $"{cantidad}-{ultima?.Ticks}-{usadas}-{aprobados}";
    }

    // =============================
    // 👥 CLIENTES Y SEGMENTACIÓN RFM (HU22)
    // =============================
    public async Task<List<ClienteAnalitica>> ClientesAsync(CancellationToken ct = default)
    {
        var clientes = await AgregarClientesAsync(_db.Ordenes.AsNoTracking().Where(o => o.Estado == EstadoOrden.Pagada), ct);

        // Puntajes por quintiles entre todos los clientes: Recencia (menos días = mejor), Frecuencia y Monto
        var r = Puntajes(clientes.Select(c => -(double)c.DiasDesdeUltima).ToList());
        var f = Puntajes(clientes.Select(c => (double)c.Compras).ToList());
        var m = Puntajes(clientes.Select(c => (double)c.Gasto).ToList());
        for (var i = 0; i < clientes.Count; i++)
        {
            var c = clientes[i];
            (c.R, c.F, c.M) = (r[i], f[i], m[i]);
            c.Segmento = SegmentoRfm.De(c.Compras, c.DiasDesdeUltima, c.R, c.F, c.M);
        }

        return clientes.OrderByDescending(c => c.Gasto).ThenByDescending(c => c.Compras).ToList();
    }

    // Gasto, compras, entradas y última compra por usuario sobre un conjunto de órdenes pagadas
    private async Task<List<ClienteAnalitica>> AgregarClientesAsync(IQueryable<Orden> pagadas, CancellationToken ct)
    {
        var porUsuario = await pagadas
            .GroupBy(o => o.UsuarioId)
            .Select(g => new
            {
                UsuarioId = g.Key,
                Gasto = g.Sum(o => o.Total),
                Compras = g.Count(),
                Entradas = g.SelectMany(o => o.Items).Sum(i => i.Cantidad),
                Ultima = g.Max(o => o.FechaPago)
            })
            .ToListAsync(ct);

        var promociones = (await _db.Users.AsNoTracking()
            .Where(u => u.AceptaPromociones)
            .Select(u => u.Id)
            .ToListAsync(ct)).ToHashSet();

        var ahora = DateTime.UtcNow;
        return porUsuario.Select(c => new ClienteAnalitica
        {
            UsuarioId = c.UsuarioId,
            Gasto = c.Gasto,
            Compras = c.Compras,
            Entradas = c.Entradas,
            UltimaCompra = (c.Ultima ?? ahora).ToLocalTime(),
            DiasDesdeUltima = (int)(ahora - (c.Ultima ?? ahora)).TotalDays,
            AceptaPromociones = promociones.Contains(c.UsuarioId)
        }).ToList();
    }

    // =============================
    // 🔎 FILTRO DE CLIENTES (HU29)
    // =============================
    public async Task<List<ClienteAnalitica>> FiltrarClientesAsync(FiltroClientes filtro, CancellationToken ct = default)
    {
        var historial = await ClientesAsync(ct);
        var clientes = historial;

        // Con período o categoría, las cifras se recalculan solo con esas compras; el segmento RFM sigue siendo el del historial
        if (filtro.Desde != null || filtro.Hasta != null || filtro.CategoriaId != null)
        {
            var q = _db.Ordenes.AsNoTracking().Where(o => o.Estado == EstadoOrden.Pagada);
            if (filtro.Desde != null)
            {
                var desdeUtc = filtro.Desde.Value.Date.ToUniversalTime();
                q = q.Where(o => o.FechaPago >= desdeUtc);
            }
            if (filtro.Hasta != null)
            {
                var hastaUtc = filtro.Hasta.Value.Date.AddDays(1).ToUniversalTime();
                q = q.Where(o => o.FechaPago < hastaUtc);
            }
            if (filtro.CategoriaId != null)
                q = q.Where(o => o.Evento!.CategoriaId == filtro.CategoriaId);
            if (filtro.EventoId != null)
                q = q.Where(o => o.EventoId == filtro.EventoId);

            var rfm = historial.ToDictionary(c => c.UsuarioId);
            clientes = await AgregarClientesAsync(q, ct);
            foreach (var c in clientes)
            {
                var h = rfm[c.UsuarioId];
                (c.R, c.F, c.M, c.Segmento) = (h.R, h.F, h.M, h.Segmento);
            }
        }

        IEnumerable<ClienteAnalitica> r = clientes;
        if (filtro.GastoMin != null) r = r.Where(c => c.Gasto >= filtro.GastoMin);
        if (filtro.GastoMax != null) r = r.Where(c => c.Gasto <= filtro.GastoMax);
        if (filtro.EntradasMin != null) r = r.Where(c => c.Entradas >= filtro.EntradasMin);
        if (filtro.ComprasMin != null) r = r.Where(c => c.Compras >= filtro.ComprasMin);
        if (!string.IsNullOrEmpty(filtro.Segmento)) r = r.Where(c => c.Segmento == filtro.Segmento);

        // De mayor a menor según el criterio elegido (desempate por gasto)
        r = filtro.Orden switch
        {
            "entradas" => r.OrderByDescending(c => c.Entradas).ThenByDescending(c => c.Gasto),
            "compras" => r.OrderByDescending(c => c.Compras).ThenByDescending(c => c.Gasto),
            _ => r.OrderByDescending(c => c.Gasto).ThenByDescending(c => c.Compras)
        };
        return r.ToList();
    }

    // Puntaje 1–5 según la posición del valor entre todos (empates reciben el mismo puntaje; el mayor valor, 5)
    public static List<int> Puntajes(IReadOnlyList<double> valores)
    {
        var n = valores.Count;
        var ordenados = valores.OrderBy(v => v).ToList();
        return valores.Select(v =>
        {
            var menores = ordenados.TakeWhile(x => x < v).Count();
            return Math.Clamp((int)Math.Ceiling(5.0 * (menores + 1) / n), 1, 5);
        }).ToList();
    }

    public async Task<FichaCliente?> FichaClienteAsync(string usuarioId, string administradorId, CancellationToken ct = default)
    {
        var usuario = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == usuarioId, ct);
        if (usuario == null)
            return null;

        // Ley 29733: el acceso a los datos de una persona queda registrado (aunque no tenga compras)
        await RegistrarAuditoriaAsync(administradorId, $"Ficha del cliente {ClienteAnalitica.CodigoDe(usuarioId)}", "Ordenes, Entradas, Eventos", ct);

        var resumen = (await ClientesAsync(ct)).FirstOrDefault(c => c.UsuarioId == usuarioId)
                      ?? new ClienteAnalitica { UsuarioId = usuarioId, Segmento = "Sin compras" };

        var pagadas = _db.Ordenes.AsNoTracking().Where(o => o.UsuarioId == usuarioId && o.Estado == EstadoOrden.Pagada);

        var ficha = new FichaCliente
        {
            Resumen = resumen,
            Nombre = usuario.NombreCompleto,
            Correo = usuario.Email ?? "",
            MiembroDesde = usuario.FechaRegistro.ToLocalTime(),
            UltimasCompras = (await pagadas
                    .OrderByDescending(o => o.FechaPago)
                    .Take(10)
                    .Select(o => new { o.Codigo, o.FechaPago, o.EventoId, Evento = o.Evento!.Titulo, Entradas = o.Items.Sum(i => i.Cantidad), o.Total })
                    .ToListAsync(ct))
                .Select(o => new CompraCliente(o.Codigo, o.FechaPago!.Value.ToLocalTime(), o.EventoId, o.Evento, o.Entradas, o.Total))
                .ToList(),

            // Asistió = tiene entradas Usadas (validadas en puerta); no basta con haber comprado
            EventosAsistidos = (await _db.Entradas.AsNoTracking()
                    .Where(e => e.UsuarioId == usuarioId && e.Estado == EstadoEntrada.Usada)
                    .GroupBy(e => new { e.EventoId, e.Evento!.Titulo, e.Evento.Fecha })
                    .Select(g => new { g.Key.EventoId, g.Key.Titulo, g.Key.Fecha, Entradas = g.Count() })
                    .ToListAsync(ct))
                .OrderByDescending(e => e.Fecha)
                .Select(e => new EventoAsistido(e.EventoId, e.Titulo, e.Fecha, e.Entradas))
                .ToList()
        };

        var items = await pagadas
            .SelectMany(o => o.Items, (o, i) => new { Evento = o.Evento!.Titulo, Categoria = o.Evento.Categoria!.Nombre, i.PrecioUnitario, i.Cantidad })
            .ToListAsync(ct);

        if (items.Count > 0)
        {
            var masCara = items.OrderByDescending(i => i.PrecioUnitario).First();
            ficha.MayorPrecioEntrada = (masCara.Evento, masCara.PrecioUnitario);

            var mayorGasto = items.GroupBy(i => i.Evento).Select(g => (Evento: g.Key, Total: g.Sum(i => i.PrecioUnitario * i.Cantidad)))
                .OrderByDescending(g => g.Total).First();
            ficha.MayorGastoEvento = mayorGasto;

            var preferida = items.GroupBy(i => i.Categoria).Select(g => (Categoria: g.Key, Entradas: g.Sum(i => i.Cantidad)))
                .OrderByDescending(g => g.Entradas).First();
            ficha.CategoriaPreferida = preferida;
        }

        return ficha;
    }

    public async Task RegistrarAuditoriaAsync(string administradorId, string consulta, string fuenteDatos, CancellationToken ct = default)
    {
        _db.AuditoriasAnalitica.Add(new AuditoriaAnalitica
        {
            AdministradorId = administradorId,
            Consulta = consulta.Length > 200 ? consulta[..200] : consulta,
            FuenteDatos = fuenteDatos,
            Fecha = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<AuditoriaAnalitica>> AuditoriaAsync(int maximo = 50, CancellationToken ct = default) =>
        _db.AuditoriasAnalitica.AsNoTracking()
            .Include(a => a.Administrador)
            .OrderByDescending(a => a.Fecha)
            .Take(maximo)
            .ToListAsync(ct);

    // =============================
    // 🔎 CONSULTAS DE SOLO LECTURA (HU24 / HU23)
    // =============================
    // Fechas locales; "hasta" incluye todo el día. Solo agregados: nunca nombres ni correos.
    public async Task<List<VentaEvento>> TopEventosAsync(DateTime desde, DateTime hasta, int n, CancellationToken ct = default, FiltroAnalitica? alcance = null)
    {
        var ventas = await PagadasDe(desde, hasta, alcance ?? new())
            .GroupBy(o => new { o.EventoId, o.Evento!.Titulo, Categoria = o.Evento.Categoria!.Nombre, o.Evento.Aforo })
            .Select(g => new { g.Key.EventoId, g.Key.Titulo, g.Key.Categoria, g.Key.Aforo, Entradas = g.SelectMany(o => o.Items).Sum(i => i.Cantidad), Ingresos = g.Sum(o => o.Total) })
            .ToListAsync(ct);

        return ventas
            .Select(v => new VentaEvento(v.EventoId, v.Titulo, v.Categoria, v.Entradas, v.Ingresos, v.Aforo))
            .OrderByDescending(v => v.Entradas).ThenByDescending(v => v.Ingresos)
            .Take(Math.Clamp(n, 1, 50))
            .ToList();
    }

    public async Task<List<VentaCategoriaPeriodo>> IngresosPorCategoriaAsync(DateTime desde, DateTime hasta, CancellationToken ct = default, FiltroAnalitica? alcance = null)
    {
        var ventas = await PagadasDe(desde, hasta, alcance ?? new())
            .GroupBy(o => o.Evento!.Categoria!.Nombre)
            .Select(g => new { Categoria = g.Key, Eventos = g.Select(o => o.EventoId).Distinct().Count(), Entradas = g.SelectMany(o => o.Items).Sum(i => i.Cantidad), Ingresos = g.Sum(o => o.Total) })
            .ToListAsync(ct);

        return ventas
            .Select(v => new VentaCategoriaPeriodo(v.Categoria, v.Eventos, v.Entradas, v.Ingresos))
            .OrderByDescending(v => v.Ingresos)
            .ToList();
    }

    // Entradas vendidas por semana de cada evento (desde su primera venta del período hasta su fecha o el fin del período).
    // Con menos de 2 semanas no hay variabilidad que medir. σ es la desviación estándar muestral (n − 1).
    public async Task<List<VariabilidadEvento>> VariabilidadPorEventoAsync(DateTime desde, DateTime hasta, CancellationToken ct = default, FiltroAnalitica? alcance = null)
    {
        var umbrales = await UmbralesAsync(ct);
        var inicio = desde.Date;
        var ventas = await PagadasDe(inicio, hasta, alcance ?? new())
            .SelectMany(o => o.Items, (o, i) => new { o.EventoId, o.Evento!.Titulo, Categoria = o.Evento.Categoria!.Nombre, EventoFecha = o.Evento.Fecha, o.FechaPago, i.Cantidad })
            .ToListAsync(ct);

        var semanaFin = (int)((hasta.Date - inicio).TotalDays / 7);
        var resultado = new List<VariabilidadEvento>();
        foreach (var g in ventas.GroupBy(v => new { v.EventoId, v.Titulo, v.Categoria, v.EventoFecha }))
        {
            int Semana(DateTime d) => (int)((d.Date - inicio).TotalDays / 7);
            var porSemana = g.GroupBy(v => Semana(v.FechaPago!.Value.ToLocalTime())).ToDictionary(s => s.Key, s => s.Sum(v => v.Cantidad));
            var primera = porSemana.Keys.Min();
            var ultima = Math.Max(porSemana.Keys.Max(), Math.Min(semanaFin, Semana(g.Key.EventoFecha)));
            if (ultima - primera < 1)
                continue;

            var serie = Enumerable.Range(primera, ultima - primera + 1).Select(s => porSemana.GetValueOrDefault(s)).ToList();
            var valores = serie.Select(v => (double)v).ToArray();
            resultado.Add(new VariabilidadEvento(g.Key.EventoId, g.Key.Titulo, g.Key.Categoria, serie,
                Math.Round(MathNet.Numerics.Statistics.Statistics.Mean(valores), 2),
                Math.Round(MathNet.Numerics.Statistics.Statistics.StandardDeviation(valores), 2))
            { LimiteEstable = umbrales.CvEstable, LimiteInestable = umbrales.CvInestable });
        }

        return resultado.OrderByDescending(v => v.Cv ?? -1).ToList();
    }

    public async Task<List<ResumenSegmento>> ResumenSegmentosAsync(CancellationToken ct = default)
    {
        var clientes = await ClientesAsync(ct);
        return SegmentoRfm.Todos
            .Select(s => clientes.Where(c => c.Segmento == s).ToList())
            .Select((g, i) => new ResumenSegmento(SegmentoRfm.Todos[i], g.Count, g.Sum(c => c.Gasto), g.Count(c => c.AceptaPromociones)))
            .ToList();
    }

    // Eventos aprobados de los próximos "dias" con ocupación (entradas emitidas ÷ aforo) por debajo del umbral (0–1)
    public async Task<List<EventoOcupacion>> EventosBajaOcupacionAsync(int dias, double umbral, CancellationToken ct = default, FiltroAnalitica? alcance = null)
    {
        var ahora = DateTime.Now;
        var limite = ahora.Date.AddDays(Math.Clamp(dias, 1, 366) + 1);
        var eventos = await EventosDe(alcance ?? new())
            .Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha >= ahora && e.Fecha < limite)
            .Select(e => new
            {
                e.Id, e.Titulo, Categoria = e.Categoria!.Nombre, e.Fecha,
                Aforo = e.TiposEntrada.Sum(t => t.Capacidad),
                Vendidas = _db.Entradas.Count(x => x.EventoId == e.Id && x.Estado != EstadoEntrada.Anulada)
            })
            .ToListAsync(ct);

        return eventos
            .Select(e => new EventoOcupacion(e.Id, e.Titulo, e.Categoria, e.Fecha, e.Vendidas, e.Aforo))
            .Where(e => e.Aforo > 0 && e.Ocupacion < umbral)
            .OrderBy(e => e.Ocupacion).ThenBy(e => e.Fecha)
            .ToList();
    }

    public async Task<List<EventoListado>> ListaEventosAsync(FiltroAnalitica alcance, string? estado, DateTime? desde, DateTime? hasta, CancellationToken ct = default)
    {
        var ahora = DateTime.Now;
        var q = EventosDe(alcance);
        q = estado switch
        {
            EstadosListado.Aprobados => q.Where(e => e.Estado == EstadoEvento.Aprobado),
            EstadosListado.Publicados => q.Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha > ahora),
            EstadosListado.Realizados => q.Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha <= ahora),
            EstadosListado.Pendientes => q.Where(e => e.Estado == EstadoEvento.Pendiente && !e.PendientePagoPublicacion),
            EstadosListado.Rechazados => q.Where(e => e.Estado == EstadoEvento.Rechazado),
            EstadosListado.Cancelados => q.Where(e => e.Estado == EstadoEvento.Cancelado),
            _ => q
        };

        var eventos = await q
            .Select(e => new
            {
                e.Id, e.Titulo, Categoria = e.Categoria!.Nombre, Organizador = e.Organizador!.NombreCompleto, e.Fecha, e.Estado,
                e.PendientePagoPublicacion, e.FechaCreacion, e.FechaModeracion,
                Aforo = e.TiposEntrada.Sum(t => t.Capacidad),
                Vendidas = _db.Entradas.Count(x => x.EventoId == e.Id && x.Estado != EstadoEntrada.Anulada)
            })
            .ToListAsync(ct);

        // Con un período: los moderados por la fecha en que se revisaron y los demás por la fecha en que se crearon
        var inicio = desde?.Date.ToUniversalTime();
        var fin = hasta?.Date.AddDays(1).ToUniversalTime();
        return eventos
            .Where(e => inicio == null || (e.FechaModeracion ?? e.FechaCreacion) >= inicio)
            .Where(e => fin == null || (e.FechaModeracion ?? e.FechaCreacion) < fin)
            .OrderByDescending(e => e.FechaModeracion ?? e.FechaCreacion)
            .Select(e => new EventoListado(e.Id, e.Titulo, e.Categoria, alcance.OrganizadorId != null ? null : e.Organizador, e.Fecha,
                EstadosListado.Nombre(e.Estado, e.PendientePagoPublicacion, e.Fecha <= ahora), e.FechaModeracion?.ToLocalTime(), e.Vendidas, e.Aforo))
            .ToList();
    }

    public Task<List<OpcionEvento>> OpcionesEventoAsync(string? organizadorId, CancellationToken ct = default) =>
        EventosDe(new FiltroAnalitica { OrganizadorId = organizadorId })
            .OrderByDescending(e => e.Fecha)
            .Select(e => new OpcionEvento(e.Id, e.Titulo, e.CategoriaId, e.Fecha))
            .ToListAsync(ct);

    public Task<List<OpcionCategoria>> OpcionesCategoriaAsync(CancellationToken ct = default) =>
        _db.Categorias.AsNoTracking().Select(c => new OpcionCategoria(c.Id, c.Nombre)).ToListAsync(ct);

    // =============================
    // 🚦 VARIABILIDAD Y SEMÁFORO DE DECISIÓN (HU23)
    // =============================
    public async Task<UmbralesVariabilidad> UmbralesAsync(CancellationToken ct = default) =>
        await _db.UmbralesVariabilidad.AsNoTracking().OrderBy(u => u.Id).FirstOrDefaultAsync(ct) ?? new UmbralesVariabilidad();

    public async Task<string?> GuardarUmbralesAsync(UmbralesVariabilidad umbrales, string administradorId, CancellationToken ct = default)
    {
        var error = umbrales.Validar();
        if (error != null)
            return error;

        var actual = await _db.UmbralesVariabilidad.OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
        if (actual == null)
            _db.UmbralesVariabilidad.Add(actual = new UmbralesVariabilidad());
        actual.CvEstable = Math.Round(umbrales.CvEstable, 1);
        actual.CvInestable = Math.Round(umbrales.CvInestable, 1);
        actual.OcupacionBaja = Math.Round(umbrales.OcupacionBaja, 1);
        actual.OcupacionAlta = Math.Round(umbrales.OcupacionAlta, 1);
        actual.ActualizadoEn = DateTime.UtcNow;
        actual.ActualizadoPorId = administradorId;
        await _db.SaveChangesAsync(ct);
        return null;
    }

    // Regla del semáforo: rojo = actuar (demanda inestable u ocupación baja), ámbar = monitorear, verde = mantener precio
    public static (Semaforo Color, string Decision) Decidir(double? cv, double? ocupacion, UmbralesVariabilidad u)
    {
        var ocupacionPct = ocupacion * 100;
        var inestable = cv > u.CvInestable;
        var baja = ocupacionPct < u.OcupacionBaja;
        var alta = ocupacionPct >= u.OcupacionAlta;

        if (inestable && baja)
            return (Semaforo.Rojo, "Demanda inestable y baja ocupación: lanzar una promoción y destacar el evento en las semanas de baja venta.");
        if (inestable)
            return (Semaforo.Rojo, "Demanda inestable: destacar el evento en las semanas de baja venta.");
        if (baja)
            return (Semaforo.Rojo, "Ocupación baja: revisar el precio o la fecha y lanzar una promoción.");
        if (cv == null)
            return (Semaforo.Ambar, "Pocas semanas de ventas para medir la variabilidad: monitorear.");
        if (cv >= u.CvEstable)
            return (Semaforo.Ambar, "Demanda variable: monitorear las ventas de las próximas semanas.");
        return alta
            ? (Semaforo.Verde, "Demanda estable y alta ocupación: mantener el precio (o evaluar subirlo).")
            : (Semaforo.Verde, "Demanda estable: mantener el precio.");
    }

    public async Task<ReporteVariabilidad> VariabilidadAsync(FiltroAnalitica filtro, CancellationToken ct = default)
    {
        var f = filtro.Normalizado();
        var umbrales = await UmbralesAsync(ct);
        var reporte = new ReporteVariabilidad { Desde = f.Desde, Hasta = f.Hasta, CategoriaId = f.CategoriaId, Umbrales = umbrales };

        var items = await Pagadas(f.Desde, f.Hasta, f.CategoriaId)
            .SelectMany(o => o.Items, (o, i) => new
            {
                OrdenId = o.Id, o.UsuarioId, o.Total, o.EventoId, o.Evento!.Titulo, Categoria = o.Evento.Categoria!.Nombre,
                EventoFecha = o.Evento.Fecha, o.FechaPago, i.Cantidad, i.PrecioUnitario
            })
            .ToListAsync(ct);
        if (items.Count == 0)
            return reporte;

        var inicio = f.Desde.Date;
        int Semana(DateTime d) => (int)((d.Date - inicio).TotalDays / 7);
        var semanaFin = Semana(f.Hasta);

        // Precios de las zonas y ocupación actual de los eventos con ventas en el período
        var ids = items.Select(i => i.EventoId).Distinct().ToList();
        var zonas = await _db.TiposEntrada.AsNoTracking().Where(t => ids.Contains(t.EventoId))
            .Select(t => new { t.EventoId, t.Precio, t.Capacidad }).ToListAsync(ct);
        var emitidas = await _db.Entradas.AsNoTracking().Where(e => ids.Contains(e.EventoId) && e.Estado != EstadoEntrada.Anulada)
            .GroupBy(e => e.EventoId).Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        // ---------- Por evento: ventas semanales, precios, ocupación y semáforo ----------
        foreach (var g in items.GroupBy(i => new { i.EventoId, i.Titulo, i.Categoria, i.EventoFecha }))
        {
            var porSemana = g.GroupBy(v => Semana(v.FechaPago!.Value.ToLocalTime())).ToDictionary(s => s.Key, s => s.Sum(v => v.Cantidad));
            var primera = porSemana.Keys.Min();
            var ultima = Math.Max(porSemana.Keys.Max(), Math.Min(semanaFin, Semana(g.Key.EventoFecha)));
            var serie = Enumerable.Range(primera, ultima - primera + 1).Select(s => porSemana.GetValueOrDefault(s)).ToList();

            var precios = zonas.Where(z => z.EventoId == g.Key.EventoId).Select(z => z.Precio).ToList();
            var aforo = zonas.Where(z => z.EventoId == g.Key.EventoId).Sum(z => z.Capacidad);
            double? ocupacion = aforo == 0 ? null : (double)emitidas.GetValueOrDefault(g.Key.EventoId) / aforo;

            var fila = new FilaVariabilidadEvento
            {
                EventoId = g.Key.EventoId, Evento = g.Key.Titulo, Categoria = g.Key.Categoria, Fecha = g.Key.EventoFecha,
                Semanas = serie, Entradas = serie.Sum(),
                Ventas = Estadisticos.De(serie.Select(v => (double)v).ToList()),
                Precios = Estadisticos.De(precios.Select(p => (double)p).ToList()),
                PrecioMin = precios.DefaultIfEmpty().Min(), PrecioMax = precios.DefaultIfEmpty().Max(),
                Ocupacion = ocupacion
            };
            (fila.Semaforo, fila.Decision) = Decidir(fila.Ventas.Cv, ocupacion, umbrales);
            reporte.Eventos.Add(fila);
        }

        // Eventos con ventas atípicas en el período (más entradas que Q3 + 1,5 × IQR)
        var limiteEventos = Estadisticos.De(reporte.Eventos.Select(e => (double)e.Entradas).ToList()).LimiteAtipico;
        foreach (var e in reporte.Eventos)
            e.Atipico = limiteEventos != null && e.Entradas > limiteEventos;

        reporte.Eventos = reporte.Eventos
            .OrderByDescending(e => e.Semaforo).ThenByDescending(e => e.Ventas.Cv ?? -1).ThenBy(e => e.Evento)
            .ToList();

        // ---------- Por categoría: ventas semanales (con semanas sin venta), precio pagado y gasto por compra ----------
        foreach (var g in items.GroupBy(i => i.Categoria))
        {
            var porSemana = g.GroupBy(v => Semana(v.FechaPago!.Value.ToLocalTime())).ToDictionary(s => s.Key, s => s.Sum(v => v.Cantidad));
            var serie = Enumerable.Range(0, semanaFin + 1).Select(s => (double)porSemana.GetValueOrDefault(s)).ToList();
            reporte.Categorias.Add(new FilaVariabilidadCategoria
            {
                Categoria = g.Key,
                Eventos = g.Select(v => v.EventoId).Distinct().Count(),
                Entradas = g.Sum(v => v.Cantidad),
                VentasSemanales = Estadisticos.De(serie),
                PrecioPagado = Estadisticos.De(g.SelectMany(v => Enumerable.Repeat((double)v.PrecioUnitario, v.Cantidad)).ToList()),
                GastoPorCompra = Estadisticos.De(g.DistinctBy(v => v.OrdenId).Select(v => (double)v.Total).ToList())
            });
        }
        reporte.Categorias = reporte.Categorias.OrderByDescending(c => c.Entradas).ToList();

        // ---------- Gasto: por compra y por cliente, con clientes atípicos (código anónimo) ----------
        var ordenes = items.DistinctBy(i => i.OrdenId).ToList();
        reporte.GastoPorCompra = Estadisticos.De(ordenes.Select(o => (double)o.Total).ToList());
        var porCliente = ordenes.GroupBy(o => o.UsuarioId).Select(c => (UsuarioId: c.Key, Gasto: c.Sum(o => o.Total), Compras: c.Count())).ToList();
        reporte.GastoPorCliente = Estadisticos.De(porCliente.Select(c => (double)c.Gasto).ToList());
        var limiteClientes = reporte.GastoPorCliente.LimiteAtipico;
        reporte.ClientesAtipicos = limiteClientes == null ? new() : porCliente
            .Where(c => (double)c.Gasto > limiteClientes)
            .OrderByDescending(c => c.Gasto)
            .Select(c => new ClienteAtipico(c.UsuarioId, ClienteAnalitica.CodigoDe(c.UsuarioId), c.Gasto, c.Compras))
            .ToList();

        return reporte;
    }
}
