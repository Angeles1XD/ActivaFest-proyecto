using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;

namespace ActivaFest.Services.Plano;

// Ubicación de una entrada del usuario: lo que la IA puede usar para orientarlo (y nada más)
public record UbicacionMiEntrada(
    int EntradaId,
    int EventoId,
    string Evento,
    DateTime Fecha,
    string Recinto,
    PlantillaPlano Plantilla,
    string Zona,
    string? Fila,
    int? Asiento,
    string? Puerta,
    IReadOnlyList<string> Indicaciones)
{
    public string Resumen => Fila != null && Asiento != null
        ? $"{Zona} · fila {Fila} · asiento {Asiento}{(Puerta != null ? $" · {Puerta}" : "")}"
        : $"{Zona}{(Puerta != null ? $" · {Puerta}" : "")}";
}

public interface IUbicacionService
{
    // HU28: entradas activas del usuario (solo las suyas) con zona, fila, asiento, puerta e indicaciones.
    // "fecha" (opcional) limita a los eventos de ese día; "eventoId" (opcional), a un evento.
    Task<List<UbicacionMiEntrada>> ObtenerUbicacionDeMiEntradaAsync(string usuarioId, DateTime? fecha = null, int? eventoId = null,
        CancellationToken ct = default);
}

// =============================
// 🧭 UBICACIÓN DE MIS ENTRADAS (HU28 / RF22)
// =============================
public class UbicacionService : IUbicacionService
{
    private readonly ApplicationDbContext _db;

    public UbicacionService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<UbicacionMiEntrada>> ObtenerUbicacionDeMiEntradaAsync(string usuarioId, DateTime? fecha = null, int? eventoId = null,
        CancellationToken ct = default)
    {
        // Entradas todavía útiles: activas, de eventos aprobados (no cancelados) que no terminaron su validez
        var desde = DateTime.Now.AddHours(-Evento.HorasValidezEntradas);
        var q = _db.Entradas.AsNoTracking()
            .Where(e => e.UsuarioId == usuarioId && e.Estado == EstadoEntrada.Activa
                        && e.Evento!.Estado == EstadoEvento.Aprobado && e.Evento.Fecha >= desde);

        if (fecha != null)
        {
            var dia = fecha.Value.Date;
            var siguiente = dia.AddDays(1);
            q = q.Where(e => e.Evento!.Fecha >= dia && e.Evento.Fecha < siguiente);
        }
        if (eventoId != null)
            q = q.Where(e => e.EventoId == eventoId);

        var entradas = await q
            .Include(e => e.Evento).ThenInclude(ev => ev!.TiposEntrada)
            .Include(e => e.TipoEntrada)
            .Include(e => e.Asiento)
            .AsSplitQuery()
            .ToListAsync(ct);

        return entradas
            .OrderBy(e => e.Evento!.Fecha)
            .ThenBy(e => PlanoService.IndiceFila(e.Asiento?.Fila))
            .ThenBy(e => e.Asiento?.Numero)
            .Select(e =>
            {
                var evento = e.Evento!;
                var zonas = evento.TiposEntrada.OrderBy(t => t.Id).ToList();
                var zona = e.TipoEntrada!;
                return new UbicacionMiEntrada(e.Id, evento.Id, evento.Titulo, evento.Fecha, evento.Ubicacion, evento.PlantillaPlano,
                    zona.Nombre, e.Asiento?.Fila, e.Asiento?.Numero, string.IsNullOrWhiteSpace(zona.PuertaIngreso) ? null : zona.PuertaIngreso.Trim(),
                    PlanoService.Indicaciones(evento.PlantillaPlano, zonas, zona, e.Asiento?.Fila, e.Asiento?.Numero));
            })
            .ToList();
    }
}
