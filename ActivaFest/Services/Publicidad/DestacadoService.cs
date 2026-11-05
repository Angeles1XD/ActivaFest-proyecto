using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Email;
using ActivaFest.Services.Entradas;

namespace ActivaFest.Services.Publicidad;

public record ResultadoContrato(ContratoDestacado? Contrato, string? Error)
{
    public static ResultadoContrato Fallo(string error) => new(null, error);
}

public enum ResultadoActivacion
{
    Activado,
    YaEstabaActivo,
    MontoNoCoincide,
    NoEncontrado
}

public interface IDestacadoService
{
    Task<List<PaqueteDestacado>> PaquetesDisponiblesAsync(CancellationToken ct = default);

    Task<ResultadoContrato> ContratarAsync(string usuarioId, bool esAdmin, int eventoId, int paqueteId, CancellationToken ct = default);

    Task<ResultadoActivacion> ConfirmarPagoAsync(string codigo, string proveedor, string referencia, decimal montoPagado, CancellationToken ct = default);

    Task<List<ContratoDestacado>> HistorialAsync(int eventoId, CancellationToken ct = default);

    // Días completos que faltan para el evento: un paquete no puede durar más que eso
    int DiasDisponibles(Evento evento, DateTime ahora);
}

// =============================
// ⭐ PAQUETES PUBLICITARIOS (HU12 / RF10 / OE2)
// =============================
// La vigencia se maneja con la misma hora local que Evento.Fecha, para que "destacado hasta el evento" sea exacto.
public class DestacadoService : IDestacadoService
{
    private readonly ApplicationDbContext _db;
    private readonly ICatalogoService _catalogo;
    private readonly IEmailService _email;
    private readonly ILogger<DestacadoService> _logger;

    public DestacadoService(ApplicationDbContext db, ICatalogoService catalogo, IEmailService email, ILogger<DestacadoService> logger)
    {
        _db = db;
        _catalogo = catalogo;
        _email = email;
        _logger = logger;
    }

    public Task<List<PaqueteDestacado>> PaquetesDisponiblesAsync(CancellationToken ct = default) =>
        _db.PaquetesDestacado.AsNoTracking().Where(p => p.Activo).OrderBy(p => p.Dias).ToListAsync(ct);

    public int DiasDisponibles(Evento evento, DateTime ahora) => Math.Max(0, (int)Math.Floor((evento.Fecha - ahora).TotalDays));

    public async Task<ResultadoContrato> ContratarAsync(string usuarioId, bool esAdmin, int eventoId, int paqueteId, CancellationToken ct = default)
    {
        var evento = await _db.Eventos.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventoId, ct);
        if (evento == null || (!esAdmin && evento.OrganizadorId != usuarioId))
            return ResultadoContrato.Fallo("Evento no encontrado.");

        if (evento.Estado != EstadoEvento.Aprobado)
            return ResultadoContrato.Fallo("Solo puedes destacar eventos aprobados y publicados.");

        var paquete = await _db.PaquetesDestacado.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paqueteId && p.Activo, ct);
        if (paquete == null)
            return ResultadoContrato.Fallo("El paquete seleccionado no está disponible.");

        var dias = DiasDisponibles(evento, DateTime.Now);
        if (dias < 1)
            return ResultadoContrato.Fallo("Tu evento ocurre en menos de un día: ya no se puede destacar.");

        if (paquete.Dias > dias)
            return ResultadoContrato.Fallo($"Tu evento ocurre en {dias} {(dias == 1 ? "día" : "días")}: elige un paquete de hasta {dias} {(dias == 1 ? "día" : "días")}.");

        // Un contrato pendiente anterior del mismo evento se descarta (no reservó nada, solo evita duplicados)
        await _db.ContratosDestacado
            .Where(c => c.EventoId == eventoId && c.Estado == EstadoContrato.PendientePago)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Estado, EstadoContrato.Cancelado), ct);

        var contrato = new ContratoDestacado
        {
            Codigo = ContratoDestacado.PrefijoCodigo + CodigoSeguro.Generar(12),
            EventoId = eventoId,
            PaqueteId = paquete.Id,
            OrganizadorId = evento.OrganizadorId ?? usuarioId,
            Precio = paquete.Precio,
            Dias = paquete.Dias,
            Prioridad = paquete.Prioridad,
            Estado = EstadoContrato.PendientePago,
            FechaCreacion = DateTime.UtcNow
        };

        _db.ContratosDestacado.Add(contrato);
        await _db.SaveChangesAsync(ct);
        return new ResultadoContrato(contrato, null);
    }

    public async Task<ResultadoActivacion> ConfirmarPagoAsync(string codigo, string proveedor, string referencia, decimal montoPagado,
        CancellationToken ct = default)
    {
        var contrato = await _db.ContratosDestacado
            .AsNoTracking()
            .Include(c => c.Evento)
            .Include(c => c.Paquete)
            .Include(c => c.Organizador)
            .FirstOrDefaultAsync(c => c.Codigo == codigo, ct);

        if (contrato == null)
            return ResultadoActivacion.NoEncontrado;

        if (contrato.Estado == EstadoContrato.Activo)
            return ResultadoActivacion.YaEstabaActivo;

        if (Math.Abs(montoPagado - contrato.Precio) > 0.01m)
        {
            _logger.LogWarning("⚠️ Pago {Referencia} de {Monto} no coincide con el destacado {Codigo} ({Precio})", referencia, montoPagado, codigo, contrato.Precio);
            return ResultadoActivacion.MontoNoCoincide;
        }

        // Si ya hay un destacado vigente, el nuevo empieza cuando termina (se acumulan, no se pisan)
        var ahora = DateTime.Now;
        var finVigente = await _db.ContratosDestacado
            .Where(c => c.EventoId == contrato.EventoId && c.Estado == EstadoContrato.Activo && c.FechaFin > ahora)
            .Select(c => c.FechaFin)
            .MaxAsync(ct);

        DateTime? inicio = finVigente.HasValue && finVigente > ahora ? finVigente : ahora;
        var limite = contrato.Evento!.Fecha;
        DateTime? fin = inicio!.Value.AddDays(contrato.Dias) > limite ? limite : inicio.Value.AddDays(contrato.Dias);
        DateTime? pago = DateTime.UtcNow;

        // Solo una confirmación puede activarlo (redirección y webhook pueden llegar a la vez)
        var activados = await _db.ContratosDestacado
            .Where(c => c.Codigo == codigo && c.Estado != EstadoContrato.Activo)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Estado, EstadoContrato.Activo)
                .SetProperty(c => c.FechaPago, pago)
                .SetProperty(c => c.FechaInicio, inicio)
                .SetProperty(c => c.FechaFin, fin)
                .SetProperty(c => c.ProveedorPago, proveedor)
                .SetProperty(c => c.ReferenciaPago, referencia), ct);

        if (activados == 0)
            return ResultadoActivacion.YaEstabaActivo;

        _logger.LogInformation("⭐ Destacado {Codigo} activo para el evento {Evento} del {Inicio} al {Fin}", codigo, contrato.EventoId, inicio, fin);
        await _catalogo.InvalidarAsync(ct);

        if (contrato.Organizador?.Email != null)
        {
            contrato.FechaInicio = inicio;
            contrato.FechaFin = fin;
            await _email.EnviarAsync(contrato.Organizador.Email, $"Tu evento ya está destacado: {contrato.Evento.Titulo}",
                EmailTemplates.DestacadoActivado(contrato));
        }

        return ResultadoActivacion.Activado;
    }

    public Task<List<ContratoDestacado>> HistorialAsync(int eventoId, CancellationToken ct = default) =>
        _db.ContratosDestacado
            .AsNoTracking()
            .Include(c => c.Paquete)
            .Where(c => c.EventoId == eventoId && c.Estado != EstadoContrato.Cancelado)
            .OrderByDescending(c => c.FechaCreacion)
            .ToListAsync(ct);
}
