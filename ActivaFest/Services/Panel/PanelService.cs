using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;

namespace ActivaFest.Services.Panel;

public class ResumenEventoPanel
{
    public int Id { get; set; }
    public string Titulo { get; set; } = "";
    public DateTime Fecha { get; set; }
    public EstadoEvento Estado { get; set; }
    public int Aforo { get; set; }
    public int Vendidas { get; set; }
    public decimal Ingresos { get; set; }
    public int Reservadas { get; set; }
    public string? Organizador { get; set; }
    public string? Categoria { get; set; }
    public string? IconoCategoria { get; set; }

    // HU13: aforo restante = lo que aún se puede vender (un evento cancelado ya no vende)
    public int AforoRestante => Estado == EstadoEvento.Cancelado ? 0 : Math.Max(0, Aforo - Vendidas - Reservadas);

    public double Ocupacion => Aforo == 0 ? 0 : (double)Vendidas / Aforo;
}


// Búsqueda y filtros del panel (mejora de HU13 para bases con muchos eventos)
public class FiltroPanel
{
    public const int TamanoPagina = 20;
    public static readonly string[] Periodos = { "proximos", "pasados" };

    // Texto en el título, la ubicación o (solo administrador) el organizador
    public string? Q { get; set; }
    public EstadoEvento? Estado { get; set; }
    public int? CategoriaId { get; set; }

    // "proximos", "pasados" o vacío (todos)
    public string? Periodo { get; set; }
    public int Pagina { get; set; } = 1;

    public bool TieneFiltros => !string.IsNullOrWhiteSpace(Q) || Estado != null || CategoriaId != null || !string.IsNullOrEmpty(Periodo);
}

public record RendimientoCategoria(int CategoriaId, string Nombre, string Icono, string Color, int Eventos, int Aforo, int Vendidas, decimal Ingresos)
{
    // Umbral del documento: ocupación menor al 40 % = baja venta (candidata a promoción o destacado)
    public const double UmbralBajo = 0.40, UmbralAlto = 0.70;

    public double Ocupacion => Aforo == 0 ? 0 : (double)Vendidas / Aforo;
    public string Nivel => Ocupacion >= UmbralAlto ? "Alta" : Ocupacion < UmbralBajo ? "Baja" : "Media";
}

public class ListadoPanel
{
    public FiltroPanel Filtro { get; set; } = new();
    public List<ResumenEventoPanel> Eventos { get; set; } = new();

    // Totales de todos los eventos que cumplen el filtro (no solo de la página)
    public int Total { get; set; }
    public decimal Ingresos { get; set; }
    public int Vendidas { get; set; }
    public int Proximos { get; set; }

    // Rendimiento por categoría de los eventos aprobados encontrados (¿qué categoría se llena y cuál está floja?)
    public List<RendimientoCategoria> PorCategoria { get; set; } = new();
    public List<Categoria> Categorias { get; set; } = new();

    public int Pagina { get; set; } = 1;
    public int Paginas => Math.Max(1, (int)Math.Ceiling(Total / (double)FiltroPanel.TamanoPagina));
}
public class ZonaPanel
{
    public string Nombre { get; set; } = "";
    public decimal Precio { get; set; }
    public int Capacidad { get; set; }
    public int Vendidas { get; set; }
    public int Reservadas { get; set; }
    public decimal Ingresos { get; set; }

    public int Disponibles => Math.Max(0, Capacidad - Vendidas - Reservadas);
    public double Ocupacion => Capacidad == 0 ? 0 : (double)Vendidas / Capacidad;
}

public record VentaDia(DateTime Dia, int Entradas, decimal Ingresos);

public record OrdenReciente(string Codigo, DateTime Fecha, string Comprador, int Entradas, decimal Total, string Proveedor);

public class PanelEvento
{
    public Evento Evento { get; set; } = new();
    public int Vendidas { get; set; }
    public decimal Ingresos { get; set; }
    public int OrdenesPagadas { get; set; }

    // Estado de cuenta del organizador (HU16 / OE5)
    public decimal Comision { get; set; }
    public decimal Liquidado { get; set; }
    public decimal Neto => Ingresos - Comision;
    public decimal SaldoPorCobrar => Neto - Liquidado;
    public int Ingresados { get; set; }
    public int Reservadas { get; set; }
    public int Aforo { get; set; }
    public int Visitas { get; set; }
    public List<ZonaPanel> Zonas { get; set; } = new();
    public List<VentaDia> VentasPorDia { get; set; } = new();
    public List<OrdenReciente> UltimasOrdenes { get; set; } = new();

    // OE4: los contadores de aforo coinciden con las entradas realmente emitidas
    public bool Conciliado { get; set; }

    public int Disponibles => Math.Max(0, Aforo - Vendidas - Reservadas);
    public double Ocupacion => Aforo == 0 ? 0 : (double)Vendidas / Aforo;
    public double Conversion => Visitas == 0 ? 0 : (double)OrdenesPagadas / Visitas;
}

public interface IPanelService
{
    Task<List<ResumenEventoPanel>> ResumenAsync(string usuarioId, bool esAdmin, CancellationToken ct = default);

    // Búsqueda, filtros y paginación del panel
    Task<ListadoPanel> BuscarAsync(string usuarioId, bool esAdmin, FiltroPanel filtro, CancellationToken ct = default);

    Task<PanelEvento?> DetalleAsync(int eventoId, string usuarioId, bool esAdmin, CancellationToken ct = default);
}

// =============================
// 📊 PANEL DEL ORGANIZADOR (HU13 / RF11 / OE4)
// =============================
// Las cifras salen de las transacciones (órdenes pagadas y entradas emitidas), no de contadores,
// y se concilian con los contadores de aforo para detectar cualquier discrepancia.
public class PanelService : IPanelService
{
    private const int DiasGrafico = 30;

    private static readonly TimeZoneInfo ZonaPeru = BuscarZonaPeru();

    private readonly ApplicationDbContext _db;

    public PanelService(ApplicationDbContext db)
    {
        _db = db;
    }

    private IQueryable<Evento> EventosVisibles(string usuarioId, bool esAdmin) =>
        esAdmin ? _db.Eventos.AsNoTracking() : _db.Eventos.AsNoTracking().Where(e => e.OrganizadorId == usuarioId);

    public async Task<List<ResumenEventoPanel>> ResumenAsync(string usuarioId, bool esAdmin, CancellationToken ct = default)
    {
        var eventos = await Proyectar(EventosVisibles(usuarioId, esAdmin)).ToListAsync(ct);
        await CompletarVentasAsync(eventos, ct);

        // Próximos primero (más cercano arriba), luego los pasados (más reciente arriba)
        var ahora = DateTime.Now;
        return eventos
            .OrderBy(e => e.Fecha < ahora)
            .ThenBy(e => e.Fecha < ahora ? -e.Fecha.Ticks : e.Fecha.Ticks)
            .ToList();
    }

    // Filtra y pagina en la base de datos: el panel sigue siendo rápido aunque haya miles de eventos
    public async Task<ListadoPanel> BuscarAsync(string usuarioId, bool esAdmin, FiltroPanel filtro, CancellationToken ct = default)
    {
        var ahora = DateTime.Now;
        var q = EventosVisibles(usuarioId, esAdmin);

        var texto = filtro.Q?.Trim();
        if (!string.IsNullOrEmpty(texto))
        {
            // Los comodines que escriba el usuario (% y _) se buscan literalmente, con "\" como carácter de escape
            var patron = "%" + texto.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            q = esAdmin
                ? q.Where(e => EF.Functions.Like(e.Titulo, patron, "\\") || EF.Functions.Like(e.Ubicacion, patron, "\\")
                               || EF.Functions.Like(e.Organizador!.NombreCompleto, patron, "\\") || EF.Functions.Like(e.Organizador!.Email!, patron, "\\"))
                : q.Where(e => EF.Functions.Like(e.Titulo, patron, "\\") || EF.Functions.Like(e.Ubicacion, patron, "\\"));
        }
        if (filtro.CategoriaId != null)
            q = q.Where(e => e.CategoriaId == filtro.CategoriaId);
        if (filtro.Estado != null)
            q = q.Where(e => e.Estado == filtro.Estado);
        if (filtro.Periodo == "proximos")
            q = q.Where(e => e.Fecha >= ahora);
        else if (filtro.Periodo == "pasados")
            q = q.Where(e => e.Fecha < ahora);

        var listado = new ListadoPanel { Filtro = filtro, Total = await q.CountAsync(ct) };
        listado.Pagina = Math.Clamp(filtro.Pagina, 1, listado.Paginas);
        listado.Proximos = await q.CountAsync(e => e.Fecha >= ahora, ct);

        // Totales de todos los eventos filtrados (no solo de la página visible)
        var ids = q.Select(e => e.Id);
        listado.Vendidas = await _db.Entradas.CountAsync(e => ids.Contains(e.EventoId) && e.Estado != EstadoEntrada.Anulada, ct);
        listado.Ingresos = (await _db.Ordenes
            .Where(o => ids.Contains(o.EventoId) && o.Estado == EstadoOrden.Pagada)
            .Select(o => o.Total)
            .ToListAsync(ct)).Sum();

        // Mismo orden que el panel: próximos (el más cercano arriba) y luego pasados (el más reciente arriba)
        listado.Eventos = await Proyectar(q
                .OrderBy(e => e.Fecha < ahora)
                .ThenBy(e => e.Fecha < ahora ? (DateTime?)null : e.Fecha)
                .ThenByDescending(e => e.Fecha)
                .ThenBy(e => e.Id)
                .Skip((listado.Pagina - 1) * FiltroPanel.TamanoPagina)
                .Take(FiltroPanel.TamanoPagina))
            .ToListAsync(ct);
        await CompletarVentasAsync(listado.Eventos, ct);

        listado.Categorias = await _db.Categorias.AsNoTracking().OrderBy(c => c.Nombre).ToListAsync(ct);
        listado.PorCategoria = await RendimientoPorCategoriaAsync(q, listado.Categorias, ct);
        return listado;
    }

    // Rendimiento de cada categoría con los eventos aprobados del filtro (sin pendientes, rechazados ni cancelados).
    // Vendidas e ingresos salen de las entradas emitidas y las órdenes pagadas (OE4).
    private async Task<List<RendimientoCategoria>> RendimientoPorCategoriaAsync(IQueryable<Evento> eventos, List<Categoria> categorias,
        CancellationToken ct)
    {
        var ids = eventos.Where(e => e.Estado == EstadoEvento.Aprobado).Select(e => e.Id);

        var cantidad = await _db.Eventos.Where(e => ids.Contains(e.Id))
            .GroupBy(e => e.CategoriaId).Select(g => new { g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        var aforo = await _db.TiposEntrada.Where(t => ids.Contains(t.EventoId))
            .GroupBy(t => t.Evento!.CategoriaId).Select(g => new { g.Key, Total = g.Sum(t => t.Capacidad) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        var vendidas = await _db.Entradas.Where(e => ids.Contains(e.EventoId) && e.Estado != EstadoEntrada.Anulada)
            .GroupBy(e => e.Evento!.CategoriaId).Select(g => new { g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        var ingresos = await _db.Ordenes.Where(o => ids.Contains(o.EventoId) && o.Estado == EstadoOrden.Pagada)
            .GroupBy(o => o.Evento!.CategoriaId).Select(g => new { g.Key, Total = g.Sum(o => o.Total) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        return categorias
            .Where(c => cantidad.ContainsKey(c.Id))
            .Select(c => new RendimientoCategoria(c.Id, c.Nombre, c.Icono, c.Color, cantidad[c.Id],
                aforo.GetValueOrDefault(c.Id), vendidas.GetValueOrDefault(c.Id), ingresos.GetValueOrDefault(c.Id)))
            .OrderByDescending(r => r.Ocupacion)
            .ThenByDescending(r => r.Ingresos)
            .ToList();
    }

    private static IQueryable<ResumenEventoPanel> Proyectar(IQueryable<Evento> eventos) =>
        eventos.Select(e => new ResumenEventoPanel
        {
            Id = e.Id,
            Titulo = e.Titulo,
            Fecha = e.Fecha,
            Estado = e.Estado,
            Aforo = e.TiposEntrada.Sum(t => t.Capacidad),
            Reservadas = e.TiposEntrada.Sum(t => t.Reservadas),
            Organizador = e.Organizador!.NombreCompleto,
            Categoria = e.Categoria!.Nombre,
            IconoCategoria = e.Categoria.Icono
        });

    // Vendidas e ingresos salen de las transacciones (OE4), no de contadores
    private async Task CompletarVentasAsync(List<ResumenEventoPanel> eventos, CancellationToken ct)
    {
        var ids = eventos.Select(e => e.Id).ToList();

        var vendidas = await _db.Entradas
            .Where(e => ids.Contains(e.EventoId) && e.Estado != EstadoEntrada.Anulada)
            .GroupBy(e => e.EventoId)
            .Select(g => new { g.Key, Cantidad = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Cantidad, ct);

        var ingresos = await _db.Ordenes
            .Where(o => ids.Contains(o.EventoId) && o.Estado == EstadoOrden.Pagada)
            .GroupBy(o => o.EventoId)
            .Select(g => new { g.Key, Total = g.Sum(o => o.Total) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        foreach (var e in eventos)
        {
            e.Vendidas = vendidas.GetValueOrDefault(e.Id);
            e.Ingresos = ingresos.GetValueOrDefault(e.Id);
        }
    }

    public async Task<PanelEvento?> DetalleAsync(int eventoId, string usuarioId, bool esAdmin, CancellationToken ct = default)
    {
        var evento = await EventosVisibles(usuarioId, esAdmin)
            .Include(e => e.TiposEntrada)
            .FirstOrDefaultAsync(e => e.Id == eventoId, ct);

        if (evento == null)
            return null;

        var entradas = await _db.Entradas
            .Where(e => e.EventoId == eventoId && e.Estado != EstadoEntrada.Anulada)
            .Select(e => new { e.TipoEntradaId, e.Estado, e.FechaEmision })
            .ToListAsync(ct);

        var ordenes = await _db.Ordenes
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.Usuario)
            .Where(o => o.EventoId == eventoId && o.Estado == EstadoOrden.Pagada)
            .ToListAsync(ct);

        var vendidasPorZona = entradas.GroupBy(e => e.TipoEntradaId).ToDictionary(g => g.Key, g => g.Count());
        var ingresosPorZona = ordenes.SelectMany(o => o.Items).GroupBy(i => i.TipoEntradaId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Subtotal));

        var panel = new PanelEvento
        {
            Evento = evento,
            Vendidas = entradas.Count,
            Ingresos = ordenes.Sum(o => o.Total),
            Comision = ordenes.Sum(o => o.Comision),
            Liquidado = await _db.Liquidaciones.Where(l => l.EventoId == eventoId).SumAsync(l => l.Monto, ct),
            OrdenesPagadas = ordenes.Count,
            Ingresados = entradas.Count(e => e.Estado == EstadoEntrada.Usada),
            Reservadas = evento.TiposEntrada.Sum(t => t.Reservadas),
            Aforo = evento.TiposEntrada.Sum(t => t.Capacidad),
            Visitas = evento.Visitas,
            Zonas = evento.TiposEntrada.OrderBy(t => t.Precio).Select(t => new ZonaPanel
            {
                Nombre = t.Nombre,
                Precio = t.Precio,
                Capacidad = t.Capacidad,
                Vendidas = vendidasPorZona.GetValueOrDefault(t.Id),
                Reservadas = t.Reservadas,
                Ingresos = ingresosPorZona.GetValueOrDefault(t.Id)
            }).ToList(),
            // Conciliación: el contador Vendidas de cada zona debe ser igual a las entradas emitidas
            Conciliado = evento.TiposEntrada.All(t => t.Vendidas == vendidasPorZona.GetValueOrDefault(t.Id)),
            UltimasOrdenes = ordenes
                .OrderByDescending(o => o.FechaPago)
                .Take(10)
                .Select(o => new OrdenReciente(o.Codigo, AHoraPeru(o.FechaPago ?? o.FechaCreacion),
                    o.Usuario?.NombreCompleto ?? "—", o.Items.Sum(i => i.Cantidad), o.Total, o.ProveedorPago ?? ""))
                .ToList()
        };

        panel.VentasPorDia = VentasPorDia(
            entradas.Select(e => AHoraPeru(e.FechaEmision)).ToList(),
            ordenes.Select(o => (AHoraPeru(o.FechaPago ?? o.FechaCreacion), o.Total)).ToList(),
            AHoraPeru(DateTime.UtcNow).Date);

        return panel;
    }

    // Serie continua de los últimos días (los días sin ventas valen 0 para no deformar el gráfico)
    public static List<VentaDia> VentasPorDia(List<DateTime> emisiones, List<(DateTime Fecha, decimal Total)> pagos, DateTime hoy)
    {
        var desde = hoy.AddDays(-(DiasGrafico - 1));
        if (emisiones.Count > 0)
        {
            var primera = emisiones.Min().Date;
            if (primera > desde)
                desde = primera < hoy.AddDays(-6) ? primera : hoy.AddDays(-6);
        }
        else
        {
            desde = hoy.AddDays(-6);
        }

        var entradasDia = emisiones.Where(f => f.Date >= desde).GroupBy(f => f.Date).ToDictionary(g => g.Key, g => g.Count());
        var ingresosDia = pagos.Where(p => p.Fecha.Date >= desde).GroupBy(p => p.Fecha.Date).ToDictionary(g => g.Key, g => g.Sum(p => p.Total));

        return Enumerable.Range(0, (hoy - desde).Days + 1)
            .Select(i => desde.AddDays(i))
            .Select(d => new VentaDia(d, entradasDia.GetValueOrDefault(d), ingresosDia.GetValueOrDefault(d)))
            .ToList();
    }

    // Las fechas se guardan en UTC; el panel las muestra en hora de Perú
    public static DateTime AHoraPeru(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), ZonaPeru);

    private static TimeZoneInfo BuscarZonaPeru()
    {
        foreach (var id in new[] { "America/Lima", "SA Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Peru", TimeSpan.FromHours(-5), "Peru", "Peru");
    }
}
