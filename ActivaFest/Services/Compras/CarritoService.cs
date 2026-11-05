using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;

namespace ActivaFest.Services.Compras;

public record LineaCarrito(string Zona, int Cantidad, decimal Subtotal);

public record ItemCarrito(string Codigo, int EventoId, string Evento, DateTime Fecha, string Ubicacion, string? Imagen,
    List<LineaCarrito> Lineas, decimal Descuento, decimal Total, DateTime Vence)
{
    public int Entradas => Lineas.Sum(l => l.Cantidad);
}

public class Carrito
{
    public List<ItemCarrito> PorPagar { get; set; } = new();

    // Reservas que vencieron en las últimas 24 horas: se muestran para poder volver a elegirlas
    public List<ItemCarrito> Vencidas { get; set; } = new();

    public decimal Total => PorPagar.Sum(i => i.Total);
    public int Entradas => PorPagar.Sum(i => i.Entradas);
}

public interface ICarritoService
{
    Task<Carrito> ObtenerAsync(string usuarioId, CancellationToken ct = default);
    Task<int> ContarAsync(string usuarioId, CancellationToken ct = default);
}

// =============================
// 🛒 CARRITO: las reservas del usuario que faltan pagar
// =============================
// Cada reserva es una orden PendientePago: sus entradas ya están apartadas hasta que vence y se pagan en el checkout.
// Por eso aparecen aquí tanto las reservas hechas desde la ficha del evento como las que pide el asistente.
public class CarritoService : ICarritoService
{
    private static readonly TimeSpan VentanaVencidas = TimeSpan.FromHours(24);

    private readonly ApplicationDbContext _db;

    public CarritoService(ApplicationDbContext db)
    {
        _db = db;
    }

    public Task<int> ContarAsync(string usuarioId, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow;
        return _db.Ordenes.AsNoTracking()
            .CountAsync(o => o.UsuarioId == usuarioId && o.Estado == EstadoOrden.PendientePago && o.FechaExpiracion > ahora, ct);
    }

    public async Task<Carrito> ObtenerAsync(string usuarioId, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow;
        var limite = ahora - VentanaVencidas;
        var ordenes = await _db.Ordenes.AsNoTracking()
            .Include(o => o.Evento!).ThenInclude(e => e.Imagenes)
            .Include(o => o.Items).ThenInclude(i => i.TipoEntrada)
            .AsSplitQuery()
            .Where(o => o.UsuarioId == usuarioId && o.FechaExpiracion > limite
                        && (o.Estado == EstadoOrden.PendientePago || o.Estado == EstadoOrden.Expirada))
            .OrderBy(o => o.FechaExpiracion)
            .ToListAsync(ct);

        return new Carrito
        {
            PorPagar = ordenes.Where(o => o.Estado == EstadoOrden.PendientePago && o.FechaExpiracion > ahora).Select(Item).ToList(),
            Vencidas = ordenes.Where(o => o.Estado == EstadoOrden.Expirada || o.FechaExpiracion <= ahora).Reverse().Select(Item).ToList()
        };
    }

    private static ItemCarrito Item(Orden o) => new(
        o.Codigo, o.EventoId, o.Evento!.Titulo, o.Evento.Fecha, o.Evento.Ubicacion,
        o.Evento.Imagenes.OrderBy(i => i.Orden).FirstOrDefault()?.Ruta,
        o.Items.GroupBy(i => i.TipoEntrada?.Nombre ?? "Entrada")
            .Select(g => new LineaCarrito(g.Key, g.Sum(i => i.Cantidad), g.Sum(i => i.Subtotal)))
            .ToList(),
        o.Descuento, o.Total, o.FechaExpiracion);
}
