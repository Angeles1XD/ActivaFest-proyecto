using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Organizadores;

namespace ActivaFest.Services.Acceso;

// Reputación de una persona del personal de control a partir de las calificaciones de los asistentes
public record Reputacion(int Total, double Promedio, int Reportes, bool MalaFama)
{
    public static readonly Reputacion Vacia = new(0, 0, 0, false);

    public string Resumen => Total == 0
        ? "Sin calificaciones todavía"
        : $"⭐ {Promedio:0.0} · {Total} {(Total == 1 ? "calificación" : "calificaciones")} · {Reportes} {(Reportes == 1 ? "reporte" : "reportes")}";
}

public record EventoControlado(string Evento, DateTime Fecha, bool Activa);

// Lo que ve el administrador para decidir si aprueba una cuenta como personal de control
public record DetalleReputacion(ApplicationUser Usuario, Reputacion Reputacion, int[] Distribucion, int EntradasValidadas,
    List<EventoControlado> Eventos, List<CalificacionPersonal> Recientes);

public interface IReputacionPersonalService
{
    Task<Reputacion> ReputacionAsync(string personalId, CancellationToken ct = default);
    Task<Dictionary<string, Reputacion>> ReputacionesAsync(IEnumerable<string> personalIds, CancellationToken ct = default);
    Task<DetalleReputacion?> DetalleAsync(string personalId, CancellationToken ct = default);

    // Calificaciones que el asistente ya dio, por entrada
    Task<Dictionary<int, CalificacionPersonal>> CalificacionesDeAsync(string usuarioId, IEnumerable<int> entradaIds, CancellationToken ct = default);
    Task<ResultadoSolicitud> CalificarAsync(string usuarioId, int entradaId, int puntaje, string? comentario, CancellationToken ct = default);
}

// =============================
// ⭐ CALIFICACIÓN Y REPUTACIÓN DEL PERSONAL DE CONTROL (HU33)
// =============================
public class ReputacionPersonalService : IReputacionPersonalService
{
    // Regla de "mala reputación": con al menos 5 calificaciones, promedio menor a 2,5 o 40 % o más de reportes (1–2 ★)
    public const int MinimoCalificaciones = 5;
    public const double PromedioMinimo = 2.5;
    public const int PorcentajeMaximoReportes = 40;
    public const int DiasParaCalificar = 7;

    private readonly ApplicationDbContext _db;

    public ReputacionPersonalService(ApplicationDbContext db)
    {
        _db = db;
    }

    public static bool EsMalaFama(int total, double promedio, int reportes) =>
        total >= MinimoCalificaciones && (promedio < PromedioMinimo || reportes * 100 >= PorcentajeMaximoReportes * total);

    // Solo el dueño de una entrada ya usada, escaneada por otra persona y dentro de los 7 días siguientes al ingreso
    public static bool PuedeCalificar(Entrada entrada, string usuarioId, DateTime ahora) =>
        entrada.UsuarioId == usuarioId &&
        entrada.Estado == EstadoEntrada.Usada &&
        entrada.ValidadoPorId != null && entrada.ValidadoPorId != usuarioId &&
        entrada.FechaUso is DateTime uso && ahora <= uso.AddDays(DiasParaCalificar);

    public async Task<Reputacion> ReputacionAsync(string personalId, CancellationToken ct = default) =>
        (await ReputacionesAsync(new[] { personalId }, ct)).GetValueOrDefault(personalId, Reputacion.Vacia);

    public async Task<Dictionary<string, Reputacion>> ReputacionesAsync(IEnumerable<string> personalIds, CancellationToken ct = default)
    {
        var ids = personalIds.Distinct().ToList();
        var filas = await _db.CalificacionesPersonal.AsNoTracking()
            .Where(c => ids.Contains(c.PersonalId))
            .GroupBy(c => c.PersonalId)
            .Select(g => new
            {
                Id = g.Key,
                Total = g.Count(),
                Suma = g.Sum(c => c.Puntaje),
                Reportes = g.Count(c => c.Puntaje <= CalificacionPersonal.PuntajeReporte)
            })
            .ToListAsync(ct);

        return filas.ToDictionary(f => f.Id, f =>
        {
            var promedio = Math.Round((double)f.Suma / f.Total, 2);
            return new Reputacion(f.Total, promedio, f.Reportes, EsMalaFama(f.Total, promedio, f.Reportes));
        });
    }

    public async Task<DetalleReputacion?> DetalleAsync(string personalId, CancellationToken ct = default)
    {
        var usuario = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == personalId, ct);
        if (usuario == null)
            return null;

        var puntajes = await _db.CalificacionesPersonal.Where(c => c.PersonalId == personalId).Select(c => c.Puntaje).ToListAsync(ct);
        var distribucion = Enumerable.Range(1, 5).Select(p => puntajes.Count(x => x == p)).ToArray();

        var eventos = await _db.AsignacionesAcceso.AsNoTracking()
            .Where(a => a.UsuarioId == personalId)
            .OrderByDescending(a => a.Evento!.Fecha)
            .Take(20)
            .Select(a => new EventoControlado(a.Evento!.Titulo, a.Evento.Fecha, a.FinalizadaEn == null))
            .ToListAsync(ct);

        // Comentarios sin datos de quien calificó (Ley N.° 29733)
        var recientes = await _db.CalificacionesPersonal.AsNoTracking()
            .Include(c => c.Evento)
            .Where(c => c.PersonalId == personalId)
            .OrderByDescending(c => c.Fecha)
            .Take(15)
            .ToListAsync(ct);

        return new DetalleReputacion(usuario, await ReputacionAsync(personalId, ct), distribucion,
            await _db.Entradas.CountAsync(e => e.ValidadoPorId == personalId, ct), eventos, recientes);
    }

    public Task<Dictionary<int, CalificacionPersonal>> CalificacionesDeAsync(string usuarioId, IEnumerable<int> entradaIds, CancellationToken ct = default)
    {
        var ids = entradaIds.ToList();
        return _db.CalificacionesPersonal.AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId && ids.Contains(c.EntradaId))
            .ToDictionaryAsync(c => c.EntradaId, ct);
    }

    public async Task<ResultadoSolicitud> CalificarAsync(string usuarioId, int entradaId, int puntaje, string? comentario, CancellationToken ct = default)
    {
        if (puntaje is < 1 or > 5)
            return ResultadoSolicitud.Fallo("Elige de 1 a 5 estrellas.");

        var entrada = await _db.Entradas.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entradaId, ct);
        if (entrada == null || !PuedeCalificar(entrada, usuarioId, DateTime.Now))
            return ResultadoSolicitud.Fallo($"Solo puedes calificar la atención en la puerta de una entrada que usaste, hasta {DiasParaCalificar} días después del ingreso.");

        if (await _db.CalificacionesPersonal.AnyAsync(c => c.EntradaId == entradaId, ct))
            return ResultadoSolicitud.Fallo("Ya calificaste la atención de esta entrada.");

        comentario = string.IsNullOrWhiteSpace(comentario) ? null : comentario.Trim();
        _db.CalificacionesPersonal.Add(new CalificacionPersonal
        {
            EntradaId = entradaId,
            EventoId = entrada.EventoId,
            PersonalId = entrada.ValidadoPorId!,
            UsuarioId = usuarioId,
            Puntaje = puntaje,
            Comentario = comentario is { Length: > 300 } ? comentario[..300] : comentario,
            Fecha = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Dos envíos a la vez: el índice único por entrada deja pasar solo uno
            return ResultadoSolicitud.Fallo("Ya calificaste la atención de esta entrada.");
        }
        return new ResultadoSolicitud(true, null);
    }
}
