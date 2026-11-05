using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Email;
using ActivaFest.Services.Entradas;

namespace ActivaFest.Services.Eventos;

public record ResultadoCobroPublicacion(PagoPublicacion? Pago, string? Error)
{
    public static ResultadoCobroPublicacion Fallo(string error) => new(null, error);
}

public record FilaEventoGratuito(int Id, string Titulo, DateTime Fecha, string Categoria, int Visitas, int Interesados)
{
    // Porcentaje de visitantes que marcaron «Me interesa»
    public double TasaInteres => Visitas == 0 ? 0 : Math.Round(Interesados * 100.0 / Visitas, 1);
}

// Alcance de los eventos gratuitos: sirve al administrador y para animar a otros organizadores a publicar
public record ResumenGratuitos(int Eventos, int Visitas, int Interesados, decimal IngresosPublicacion, int PendientesDePago,
    List<FilaEventoGratuito> Top)
{
    public double TasaInteres => Visitas == 0 ? 0 : Math.Round(Interesados * 100.0 / Visitas, 1);
}

public interface IEventoGratuitoService
{
    IReadOnlyList<TramoTarifa> Tramos { get; }
    decimal TarifaPara(int aforo);

    Task<ResultadoCobroPublicacion> CrearCobroAsync(string usuarioId, bool esAdmin, int eventoId, CancellationToken ct = default);
    Task<bool> ConfirmarPagoAsync(string codigo, string proveedor, string referencia, decimal montoPagado, CancellationToken ct = default);

    Task<(int Interesados, bool MeInteresa)> InteresAsync(int eventoId, string? usuarioId, CancellationToken ct = default);
    Task<bool?> AlternarInteresAsync(string usuarioId, int eventoId, CancellationToken ct = default);

    Task<ResumenGratuitos> ResumenAsync(DateTime? desde = null, DateTime? hasta = null, CancellationToken ct = default);
}

// =============================
// 🎉 EVENTOS GRATUITOS: TARIFA DE PUBLICACIÓN Y «ME INTERESA» (HU34)
// =============================
public class EventoGratuitoService : IEventoGratuitoService
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailService _email;
    private readonly ILogger<EventoGratuitoService> _logger;

    public EventoGratuitoService(ApplicationDbContext db, IOptions<EventosGratuitosOptions> opciones, IEmailService email,
        ILogger<EventoGratuitoService> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
        var tramos = opciones.Value.Tramos.Count > 0 ? opciones.Value.Tramos : EventosGratuitosOptions.TramosPorDefecto.ToList();
        Tramos = tramos.OrderBy(t => t.HastaAforo ?? int.MaxValue).ToList();
    }

    public IReadOnlyList<TramoTarifa> Tramos { get; }

    // Primer tramo cuyo límite cubre el aforo; el último (sin límite) cubre el resto
    public decimal TarifaPara(int aforo) =>
        (Tramos.FirstOrDefault(t => t.HastaAforo == null || aforo <= t.HastaAforo) ?? Tramos[^1]).Tarifa;

    public async Task<ResultadoCobroPublicacion> CrearCobroAsync(string usuarioId, bool esAdmin, int eventoId, CancellationToken ct = default)
    {
        var evento = await _db.Eventos.FirstOrDefaultAsync(e => e.Id == eventoId && (esAdmin || e.OrganizadorId == usuarioId), ct);
        if (evento == null)
            return ResultadoCobroPublicacion.Fallo("Evento no encontrado.");
        if (!evento.PendientePagoPublicacion)
            return ResultadoCobroPublicacion.Fallo("La publicación de este evento ya está pagada.");

        // Se reutiliza el cobro pendiente si el monto no cambió (por ejemplo, si el pago anterior se rechazó)
        var monto = TarifaPara(evento.Aforo);
        var pago = await _db.PagosPublicacion.FirstOrDefaultAsync(p => p.EventoId == eventoId && p.Estado == EstadoPagoPublicacion.PendientePago, ct);
        if (pago == null || pago.Monto != monto)
        {
            pago = new PagoPublicacion
            {
                Codigo = PagoPublicacion.PrefijoCodigo + CodigoSeguro.Generar(12),
                EventoId = eventoId,
                OrganizadorId = evento.OrganizadorId ?? usuarioId,
                Monto = monto,
                Aforo = evento.Aforo
            };
            _db.PagosPublicacion.Add(pago);
            await _db.SaveChangesAsync(ct);
        }
        return new ResultadoCobroPublicacion(pago, null);
    }

    public async Task<bool> ConfirmarPagoAsync(string codigo, string proveedor, string referencia, decimal montoPagado, CancellationToken ct = default)
    {
        var pago = await _db.PagosPublicacion.Include(p => p.Evento).FirstOrDefaultAsync(p => p.Codigo == codigo, ct);
        if (pago == null)
            return false;
        if (pago.Estado == EstadoPagoPublicacion.Pagado)
            return true;   // el webhook y el retorno pueden llegar los dos
        if (montoPagado < pago.Monto)
        {
            _logger.LogWarning("Pago de publicación {Codigo} por un monto menor: {Monto}", codigo, montoPagado);
            return false;
        }

        pago.Estado = EstadoPagoPublicacion.Pagado;
        pago.Proveedor = proveedor;
        pago.Referencia = referencia;
        pago.FechaPago = DateTime.UtcNow;
        pago.Evento!.PendientePagoPublicacion = false;
        await _db.SaveChangesAsync(ct);

        var correo = await _db.Users.Where(u => u.Id == pago.OrganizadorId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (correo != null)
            await _email.EnviarAsync(correo, $"Publicación pagada: {pago.Evento.Titulo}", EmailTemplates.PublicacionPagada(pago, pago.Evento.Titulo));
        return true;
    }

    public async Task<(int Interesados, bool MeInteresa)> InteresAsync(int eventoId, string? usuarioId, CancellationToken ct = default) =>
        (await _db.InteresesEvento.CountAsync(i => i.EventoId == eventoId, ct),
         usuarioId != null && await _db.InteresesEvento.AnyAsync(i => i.EventoId == eventoId && i.UsuarioId == usuarioId, ct));

    // Marca o desmarca «Me interesa». null = el evento no admite interés (no es gratuito o no está publicado)
    public async Task<bool?> AlternarInteresAsync(string usuarioId, int eventoId, CancellationToken ct = default)
    {
        if (!await _db.Eventos.AnyAsync(e => e.Id == eventoId && e.EsGratuito && e.Estado == EstadoEvento.Aprobado, ct))
            return null;

        var actual = await _db.InteresesEvento.FirstOrDefaultAsync(i => i.EventoId == eventoId && i.UsuarioId == usuarioId, ct);
        if (actual != null)
        {
            _db.InteresesEvento.Remove(actual);
            await _db.SaveChangesAsync(ct);
            return false;
        }

        _db.InteresesEvento.Add(new InteresEvento { EventoId = eventoId, UsuarioId = usuarioId });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Doble clic: el índice único deja un solo registro
        }
        return true;
    }

    public async Task<ResumenGratuitos> ResumenAsync(DateTime? desde = null, DateTime? hasta = null, CancellationToken ct = default)
    {
        var eventos = _db.Eventos.AsNoTracking().Where(e => e.EsGratuito && e.Estado == EstadoEvento.Aprobado);
        if (desde != null)
            eventos = eventos.Where(e => e.Fecha >= desde);
        if (hasta != null)
            eventos = eventos.Where(e => e.Fecha < hasta.Value.Date.AddDays(1));

        var filas = await eventos
            .Select(e => new FilaEventoGratuito(e.Id, e.Titulo, e.Fecha, e.Categoria!.Nombre, e.Visitas,
                _db.InteresesEvento.Count(i => i.EventoId == e.Id)))
            .ToListAsync(ct);

        var pagos = _db.PagosPublicacion.AsNoTracking().Where(p => p.Estado == EstadoPagoPublicacion.Pagado);
        if (desde != null)
            pagos = pagos.Where(p => p.FechaPago >= desde.Value.ToUniversalTime());
        if (hasta != null)
            pagos = pagos.Where(p => p.FechaPago < hasta.Value.Date.AddDays(1).ToUniversalTime());
        var ingresos = (await pagos.Select(p => p.Monto).ToListAsync(ct)).Sum();

        return new ResumenGratuitos(filas.Count, filas.Sum(f => f.Visitas), filas.Sum(f => f.Interesados), ingresos,
            await _db.Eventos.CountAsync(e => e.PendientePagoPublicacion, ct),
            filas.OrderByDescending(f => f.Interesados).ThenByDescending(f => f.Visitas).Take(8).ToList());
    }
}
