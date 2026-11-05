using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Email;

namespace ActivaFest.Services.Eventos;

public enum ResultadoCancelacion
{
    Cancelado,
    NoEncontrado,
    NoCancelable,   // solo se cancela un evento Aprobado (diagrama de estados)
    YaOcurrio,
    MotivoInvalido
}

public record CancelacionEvento(ResultadoCancelacion Resultado, int EntradasAnuladas = 0, int CompradoresAvisados = 0);

public interface ICancelacionEventoService
{
    Task<CancelacionEvento> CancelarAsync(int eventoId, string? motivo, string urlMisEntradas, CancellationToken ct = default);
}

// =============================
// 🚫 CANCELACIÓN DE UN EVENTO (HU15 / RF13)
// =============================
// Sigue los diagramas de estados: Evento Aprobado → Cancelado y sus Entradas Activa → Anulada.
// Las reservas en curso se liberan, las órdenes pagadas quedan para reembolso (lo gestiona el organizador,
// sin comisión para la plataforma) y cada comprador recibe un correo con el motivo.
public class CancelacionEventoService : ICancelacionEventoService
{
    public const int LargoMaximoMotivo = 500;

    private readonly ApplicationDbContext _db;
    private readonly ICatalogoService _catalogo;
    private readonly IEmailService _email;
    private readonly ILogger<CancelacionEventoService> _logger;

    public CancelacionEventoService(ApplicationDbContext db, ICatalogoService catalogo, IEmailService email, ILogger<CancelacionEventoService> logger)
    {
        _db = db;
        _catalogo = catalogo;
        _email = email;
        _logger = logger;
    }

    public async Task<CancelacionEvento> CancelarAsync(int eventoId, string? motivo, string urlMisEntradas, CancellationToken ct = default)
    {
        motivo = motivo?.Trim();
        if (string.IsNullOrEmpty(motivo) || motivo.Length > LargoMaximoMotivo)
            return new(ResultadoCancelacion.MotivoInvalido);

        var evento = await _db.Eventos.AsNoTracking().Include(e => e.Organizador).FirstOrDefaultAsync(e => e.Id == eventoId, ct);
        if (evento == null)
            return new(ResultadoCancelacion.NoEncontrado);
        if (evento.YaOcurrio(DateTime.Now))
            return new(ResultadoCancelacion.YaOcurrio);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Solo quien logra pasar el evento de Aprobado a Cancelado continúa (dos clics no cancelan dos veces)
        var cambiados = await _db.Eventos
            .Where(e => e.Id == eventoId && e.Estado == EstadoEvento.Aprobado)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Estado, EstadoEvento.Cancelado)
                .SetProperty(e => e.MotivoCancelacion, motivo), ct);

        if (cambiados == 0)
        {
            await tx.RollbackAsync(ct);
            return new(ResultadoCancelacion.NoCancelable);
        }

        var anuladas = await _db.Entradas
            .Where(e => e.EventoId == eventoId && e.Estado == EstadoEntrada.Activa)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Estado, EstadoEntrada.Anulada), ct);

        // Compras en curso: se cancelan y se devuelve la reserva
        await _db.Ordenes
            .Where(o => o.EventoId == eventoId && o.Estado == EstadoOrden.PendientePago)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Estado, EstadoOrden.Cancelada), ct);
        await _db.TiposEntrada
            .Where(t => t.EventoId == eventoId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Reservadas, 0).SetProperty(t => t.RowVersion, t => t.RowVersion + 1), ct);

        // Compras pagadas: el organizador devuelve el dinero (no generan comisión)
        var compradores = await _db.Ordenes
            .Where(o => o.EventoId == eventoId && o.Estado == EstadoOrden.Pagada)
            .Select(o => new { o.Usuario!.Email, o.Usuario.NombreCompleto, o.Codigo })
            .ToListAsync(ct);
        await _db.Ordenes
            .Where(o => o.EventoId == eventoId && o.Estado == EstadoOrden.Pagada)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Estado, EstadoOrden.RequiereReembolso), ct);

        await tx.CommitAsync(ct);
        await _catalogo.InvalidarAsync(ct);

        evento.MotivoCancelacion = motivo;
        var avisados = 0;
        foreach (var comprador in compradores.Where(c => c.Email != null).GroupBy(c => c.Email!))
        {
            var ordenes = comprador.Select(c => c.Codigo).ToList();
            await _email.EnviarAsync(comprador.Key, $"Evento cancelado: {evento.Titulo}",
                EmailTemplates.EventoCancelado(evento, comprador.First().NombreCompleto, ordenes, urlMisEntradas));
            avisados++;
        }

        if (evento.Organizador?.Email != null)
            await _email.EnviarAsync(evento.Organizador.Email, $"Cancelaste el evento: {evento.Titulo}",
                EmailTemplates.EventoCanceladoOrganizador(evento, compradores.Count));

        _logger.LogInformation("🚫 Evento {Id} cancelado: {Anuladas} entradas anuladas, {Avisados} compradores avisados", eventoId, anuladas, avisados);
        return new(ResultadoCancelacion.Cancelado, anuladas, avisados);
    }
}
