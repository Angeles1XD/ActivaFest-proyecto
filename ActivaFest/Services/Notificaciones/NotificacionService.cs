using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;

namespace ActivaFest.Services.Notificaciones;

public record NotificacionPanel(int Id, string Icono, string Titulo, string Mensaje, string? Url, DateTime Fecha, bool Leida);

// Lo que el asistente tiene programado y aún no se envía (recordatorios y alertas activas)
public record ProgramadaPanel(string Icono, string Texto, DateTime? Cuando);

public class PanelNotificaciones
{
    public int NoLeidas { get; set; }
    public List<NotificacionPanel> Notificaciones { get; set; } = new();
    public List<ProgramadaPanel> Programadas { get; set; } = new();
}

public interface INotificacionService
{
    Task CrearAsync(string usuarioId, string icono, string titulo, string mensaje, string? url = null, int? eventoId = null, CancellationToken ct = default);

    Task<PanelNotificaciones> PanelAsync(string usuarioId, CancellationToken ct = default);

    Task<int> NoLeidasAsync(string usuarioId, CancellationToken ct = default);

    Task MarcarLeidasAsync(string usuarioId, CancellationToken ct = default);
}

// =============================
// 🔔 PANEL DE NOTIFICACIONES
// =============================
// Cada usuario solo ve las suyas: el id siempre llega de la sesión del servidor.
public class NotificacionService : INotificacionService
{
    private const int Maximo = 30;

    private readonly ApplicationDbContext _db;

    public NotificacionService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task CrearAsync(string usuarioId, string icono, string titulo, string mensaje, string? url = null, int? eventoId = null, CancellationToken ct = default)
    {
        _db.Notificaciones.Add(Nueva(usuarioId, icono, titulo, mensaje, url, eventoId));
        await _db.SaveChangesAsync(ct);
    }

    // Para agregarla en la misma transacción que otro cambio (se guarda con el SaveChanges de quien la usa)
    public static Notificacion Nueva(string usuarioId, string icono, string titulo, string mensaje, string? url = null, int? eventoId = null) => new()
    {
        UsuarioId = usuarioId,
        Icono = icono,
        Titulo = titulo.Length > 120 ? titulo[..120] : titulo,
        Mensaje = mensaje.Length > 500 ? mensaje[..500] : mensaje,
        Url = url != null && url.StartsWith('/') && !url.StartsWith("//") ? url : null,
        EventoId = eventoId
    };

    public async Task<PanelNotificaciones> PanelAsync(string usuarioId, CancellationToken ct = default)
    {
        var notificaciones = await _db.Notificaciones.AsNoTracking()
            .Where(n => n.UsuarioId == usuarioId)
            .OrderByDescending(n => n.Fecha).ThenByDescending(n => n.Id)
            .Take(Maximo)
            .Select(n => new NotificacionPanel(n.Id, n.Icono, n.Titulo, n.Mensaje, n.Url, n.Fecha, n.Leida))
            .ToListAsync(ct);

        var ahora = DateTime.Now;
        var recordatorios = await _db.RecordatoriosEvento.AsNoTracking()
            .Where(r => r.UsuarioId == usuarioId && r.EnviadoEn == null && r.Evento!.Fecha > ahora)
            .OrderBy(r => r.Evento!.Fecha)
            .Select(r => new { r.Evento!.Titulo, r.Evento.Fecha })
            .Take(10)
            .ToListAsync(ct);
        var alertas = await _db.AlertasAsistente.AsNoTracking()
            .Where(a => a.UsuarioId == usuarioId && a.Activa)
            .Select(a => new { a.Tipo, Titulo = a.Evento != null ? a.Evento.Titulo : null })
            .ToListAsync(ct);

        var programadas = recordatorios
            .Select(r => new ProgramadaPanel("⏰", $"Recordatorio de «{r.Titulo}»", r.Fecha.AddHours(-RecordatorioEvento.HorasAntes)))
            .Concat(alertas.Select(a => a.Tipo == TipoAlertaAsistente.AprobacionEvento
                ? new ProgramadaPanel("⏳", $"Te aviso cuando revisen «{a.Titulo}»", null)
                : new ProgramadaPanel("🛡️", "Te aviso cuando lleguen eventos por aprobar", null)))
            .ToList();

        return new PanelNotificaciones
        {
            NoLeidas = await NoLeidasAsync(usuarioId, ct),
            Notificaciones = notificaciones,
            Programadas = programadas
        };
    }

    public Task<int> NoLeidasAsync(string usuarioId, CancellationToken ct = default) =>
        _db.Notificaciones.CountAsync(n => n.UsuarioId == usuarioId && !n.Leida, ct);

    public Task MarcarLeidasAsync(string usuarioId, CancellationToken ct = default) =>
        _db.Notificaciones.Where(n => n.UsuarioId == usuarioId && !n.Leida)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Leida, true), ct);
}
