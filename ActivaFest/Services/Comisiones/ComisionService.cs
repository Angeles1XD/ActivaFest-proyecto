using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;

namespace ActivaFest.Services.Comisiones;

public class FilaComisionEvento
{
    public int EventoId { get; set; }
    public string Evento { get; set; } = "";
    public DateTime FechaEvento { get; set; }
    public string OrganizadorId { get; set; } = "";
    public string Organizador { get; set; } = "";
    public string CorreoOrganizador { get; set; } = "";

    // Del periodo filtrado
    public int Ordenes { get; set; }
    public int EntradasVendidas { get; set; }
    public decimal Bruto { get; set; }
    public decimal Comision { get; set; }
    public decimal Neto => Bruto - Comision;

    // Porcentaje efectivo cobrado (cada orden congela el porcentaje vigente al pagarse)
    public decimal PorcentajeComision => Bruto == 0 ? 0 : Math.Round(Comision / Bruto * 100, 2);

    // Acumulados al día de hoy (no dependen del filtro)
    public decimal NetoTotal { get; set; }
    public decimal Liquidado { get; set; }
    public decimal Saldo => NetoTotal - Liquidado;
}

public class ReporteComisiones
{
    public DateTime? Desde { get; set; }
    public DateTime? Hasta { get; set; }
    public List<FilaComisionEvento> Eventos { get; set; } = new();
    public decimal IngresosPublicidad { get; set; }
    public int ContratosPublicidad { get; set; }

    public decimal Bruto => Eventos.Sum(e => e.Bruto);
    public decimal Comisiones => Eventos.Sum(e => e.Comision);
    public decimal IngresosPlataforma => Comisiones + IngresosPublicidad;
    public decimal SaldoPorLiquidar => Eventos.Sum(e => e.Saldo);
}

public record ResultadoLiquidacion(bool Exito, string Mensaje);

public interface IComisionService
{
    Task<ReporteComisiones> ReporteAsync(DateTime? desde, DateTime? hasta, string? organizadorId = null, CancellationToken ct = default);

    Task<ResultadoLiquidacion> RegistrarLiquidacionAsync(int eventoId, decimal monto, string referencia, string? nota, string adminId,
        CancellationToken ct = default);

    Task<List<Liquidacion>> LiquidacionesAsync(int? eventoId = null, int maximo = 50, CancellationToken ct = default);

    byte[] ExportarCsv(ReporteComisiones reporte);
}

// =============================
// 💰 COMISIONES Y LIQUIDACIONES (HU16 / RF14 / OE5)
// =============================
// Fuente de verdad: órdenes pagadas (con su comisión congelada), contratos de publicidad pagados
// y liquidaciones registradas (solo inserción). Nada se recalcula con tarifas actuales.
public class ComisionService : IComisionService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ComisionService> _logger;

    public ComisionService(ApplicationDbContext db, ILogger<ComisionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReporteComisiones> ReporteAsync(DateTime? desde, DateTime? hasta, string? organizadorId = null, CancellationToken ct = default)
    {
        // El filtro es por fecha de pago (UTC); "hasta" incluye todo el día
        var desdeUtc = desde?.Date.ToUniversalTime();
        var hastaUtc = hasta?.Date.AddDays(1).ToUniversalTime();

        var pagadas = _db.Ordenes.AsNoTracking().Where(o => o.Estado == EstadoOrden.Pagada);
        if (organizadorId != null)
            pagadas = pagadas.Where(o => o.Evento!.OrganizadorId == organizadorId);

        var periodo = pagadas;
        if (desdeUtc.HasValue) periodo = periodo.Where(o => o.FechaPago >= desdeUtc);
        if (hastaUtc.HasValue) periodo = periodo.Where(o => o.FechaPago < hastaUtc);

        var ventasPeriodo = await periodo
            .GroupBy(o => o.EventoId)
            .Select(g => new
            {
                EventoId = g.Key, Ordenes = g.Count(), Entradas = g.Sum(o => o.Items.Sum(i => i.Cantidad)),
                Bruto = g.Sum(o => o.Total), Comision = g.Sum(o => o.Comision)
            })
            .ToListAsync(ct);

        var netoTotal = await pagadas
            .GroupBy(o => o.EventoId)
            .Select(g => new { EventoId = g.Key, Neto = g.Sum(o => o.Total) - g.Sum(o => o.Comision) })
            .ToDictionaryAsync(x => x.EventoId, x => x.Neto, ct);

        var liquidado = await _db.Liquidaciones.AsNoTracking()
            .GroupBy(l => l.EventoId)
            .Select(g => new { EventoId = g.Key, Monto = g.Sum(l => l.Monto) })
            .ToDictionaryAsync(x => x.EventoId, x => x.Monto, ct);

        // Eventos con ventas en el periodo o con saldo pendiente
        var ids = ventasPeriodo.Select(v => v.EventoId)
            .Union(netoTotal.Where(n => n.Value - liquidado.GetValueOrDefault(n.Key) > 0.009m).Select(n => n.Key))
            .ToList();

        var eventos = await _db.Eventos.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new
            {
                e.Id, e.Titulo, e.Fecha, e.OrganizadorId,
                Nombre = e.Organizador != null ? e.Organizador.NombreCompleto : "—",
                Correo = e.Organizador != null ? e.Organizador.Email : ""
            })
            .ToListAsync(ct);

        var porEvento = ventasPeriodo.ToDictionary(v => v.EventoId);
        var filas = eventos.Select(e => new FilaComisionEvento
        {
            EventoId = e.Id,
            Evento = e.Titulo,
            FechaEvento = e.Fecha,
            OrganizadorId = e.OrganizadorId ?? "",
            Organizador = e.Nombre,
            CorreoOrganizador = e.Correo ?? "",
            Ordenes = porEvento.TryGetValue(e.Id, out var v) ? v.Ordenes : 0,
            EntradasVendidas = v?.Entradas ?? 0,
            Bruto = v?.Bruto ?? 0,
            Comision = v?.Comision ?? 0,
            NetoTotal = netoTotal.GetValueOrDefault(e.Id),
            Liquidado = liquidado.GetValueOrDefault(e.Id)
        })
        .OrderBy(f => f.Organizador).ThenBy(f => f.FechaEvento)
        .ToList();

        // Publicidad: contratos pagados en el periodo (por fecha de pago)
        var publicidad = _db.ContratosDestacado.AsNoTracking().Where(c => c.Estado == EstadoContrato.Activo);
        if (organizadorId != null) publicidad = publicidad.Where(c => c.OrganizadorId == organizadorId);
        if (desdeUtc.HasValue) publicidad = publicidad.Where(c => c.FechaPago >= desdeUtc);
        if (hastaUtc.HasValue) publicidad = publicidad.Where(c => c.FechaPago < hastaUtc);

        return new ReporteComisiones
        {
            Desde = desde,
            Hasta = hasta,
            Eventos = filas,
            IngresosPublicidad = await publicidad.SumAsync(c => c.Precio, ct),
            ContratosPublicidad = await publicidad.CountAsync(ct)
        };
    }

    public async Task<ResultadoLiquidacion> RegistrarLiquidacionAsync(int eventoId, decimal monto, string referencia, string? nota,
        string adminId, CancellationToken ct = default)
    {
        monto = Math.Round(monto, 2);
        if (monto <= 0)
            return new(false, "El monto debe ser mayor a cero.");

        if (string.IsNullOrWhiteSpace(referencia))
            return new(false, "Indica el n.° de operación o comprobante del pago.");

        var evento = await _db.Eventos.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventoId, ct);
        if (evento?.OrganizadorId == null)
            return new(false, "Evento sin organizador.");

        // Todo dentro de una transacción para que dos registros simultáneos no superen el saldo
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var neto = await _db.Ordenes
            .Where(o => o.EventoId == eventoId && o.Estado == EstadoOrden.Pagada)
            .SumAsync(o => o.Total - o.Comision, ct);
        var pagado = await _db.Liquidaciones.Where(l => l.EventoId == eventoId).SumAsync(l => l.Monto, ct);
        var saldo = neto - pagado;

        if (monto > saldo + 0.009m)
        {
            await tx.RollbackAsync(ct);
            return new(false, $"El monto supera el saldo pendiente ({Formato.Monto(saldo)}).");
        }

        _db.Liquidaciones.Add(new Liquidacion
        {
            OrganizadorId = evento.OrganizadorId,
            EventoId = eventoId,
            Monto = monto,
            Referencia = referencia.Trim(),
            Nota = string.IsNullOrWhiteSpace(nota) ? null : nota.Trim(),
            Fecha = DateTime.UtcNow,
            RegistradoPorId = adminId
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("💸 Liquidación de {Monto} al organizador {Organizador} por el evento {Evento} registrada por {Admin}",
            monto, evento.OrganizadorId, eventoId, adminId);
        return new(true, $"Pago de {Formato.Monto(monto)} registrado para \"{evento.Titulo}\".");
    }

    public Task<List<Liquidacion>> LiquidacionesAsync(int? eventoId = null, int maximo = 50, CancellationToken ct = default)
    {
        var query = _db.Liquidaciones.AsNoTracking().Include(l => l.Evento).Include(l => l.Organizador).AsQueryable();
        if (eventoId.HasValue)
            query = query.Where(l => l.EventoId == eventoId);

        return query.OrderByDescending(l => l.Fecha).Take(maximo).ToListAsync(ct);
    }

    // CSV para Excel en español: separador ";", coma decimal y BOM UTF-8 para que se vean las tildes
    public byte[] ExportarCsv(ReporteComisiones r)
    {
        var peru = CultureInfo.GetCultureInfo("es-PE");
        string Num(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

        var sb = new StringBuilder();
        sb.AppendLine("Organizador;Correo;Evento;Fecha del evento;Órdenes del periodo;Entradas vendidas;Ventas brutas;% comisión;Comisión;Neto organizador;Neto acumulado;Liquidado;Saldo por liquidar");
        foreach (var f in r.Eventos)
        {
            sb.AppendLine(string.Join(";",
                Celda(f.Organizador), Celda(f.CorreoOrganizador), Celda(f.Evento), f.FechaEvento.ToString("dd/MM/yyyy HH:mm", peru),
                f.Ordenes, f.EntradasVendidas, Num(f.Bruto), Num(f.PorcentajeComision), Num(f.Comision), Num(f.Neto), Num(f.NetoTotal), Num(f.Liquidado), Num(f.Saldo)));
        }

        sb.AppendLine();
        sb.AppendLine($"Totales;;;;{r.Eventos.Sum(e => e.Ordenes)};{r.Eventos.Sum(e => e.EntradasVendidas)};{Num(r.Bruto)};;{Num(r.Comisiones)};{Num(r.Bruto - r.Comisiones)};;;{Num(r.SaldoPorLiquidar)}");
        sb.AppendLine($"Ingresos por publicidad;;;;{r.ContratosPublicidad};;{Num(r.IngresosPublicidad)}");
        sb.AppendLine($"Ingresos de la plataforma (comisiones + publicidad);;;;;;{Num(r.IngresosPlataforma)}");

        var bom = Encoding.UTF8.GetPreamble();
        return bom.Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    // Escapa comillas y separadores, y neutraliza fórmulas (=, +, -, @) para evitar inyección en Excel
    public static string Celda(string? valor)
    {
        var texto = valor ?? "";
        if (texto.Length > 0 && "=+-@\t\r".Contains(texto[0]))
            texto = "'" + texto;

        return texto.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
            ? "\"" + texto.Replace("\"", "\"\"") + "\""
            : texto;
    }
}
