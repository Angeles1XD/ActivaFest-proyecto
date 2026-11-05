using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Email;
using ActivaFest.Services.Entradas;
using ActivaFest.Services.Promociones;

namespace ActivaFest.Services.Compras;

public class ComprasOptions
{
    // Diagrama de secuencia de compra: la reserva dura 10 minutos
    public int MinutosReserva { get; set; } = 10;
    public int MaxEntradasPorOrden { get; set; } = 10;
}

public class ComisionesOptions
{
    // Porcentaje que cobra la plataforma sobre cada venta de entradas (RF14)
    public decimal Porcentaje { get; set; } = 8m;
}

public record ResultadoOrden(Orden? Orden, string? Error)
{
    public static ResultadoOrden Fallo(string error) => new(null, error);
}

public enum ResultadoConfirmacion
{
    Confirmada,
    YaEstabaConfirmada,
    RequiereReembolso,
    MontoNoCoincide,
    NoEncontrada
}

public interface ICompraService
{
    // HU30: "cupon" es opcional; se valida en el servidor y se aparta junto con la reserva
    Task<ResultadoOrden> CrearOrdenAsync(string usuarioId, int eventoId, IReadOnlyDictionary<int, int> cantidades, string? cupon = null, CancellationToken ct = default);

    // HU26: compra eligiendo los asientos exactos (recintos con asientos numerados)
    Task<ResultadoOrden> CrearOrdenConAsientosAsync(string usuarioId, int eventoId, IReadOnlyCollection<int> asientos, string? cupon = null, CancellationToken ct = default);

    Task<ResultadoConfirmacion> ConfirmarPagoAsync(string codigoOrden, string proveedor, string referencia, decimal montoPagado,
        string urlMisEntradas, CancellationToken ct = default);

    Task<bool> CancelarAsync(string codigoOrden, string usuarioId, CancellationToken ct = default);

    // HU06: un pago rechazado no descuenta aforo; la reserva se libera de inmediato
    Task<bool> RechazarPagoAsync(string codigoOrden, CancellationToken ct = default);

    Task<int> LiberarExpiradasAsync(CancellationToken ct = default);
}

// =============================
// 🛒 COMPRA DE ENTRADAS (HU06) + CONTROL DE AFORO SIN SOBREVENTA (HU11) + EMISIÓN (HU07)
// =============================
// Regla de oro: el stock solo cambia con UPDATE condicionales atómicos ("...WHERE quedan >= cantidad"),
// con token de concurrencia (RowVersion) por zona y un bloqueo distribuido por evento (Redis) al reservar (HU11).
// Así, aunque lleguen compras simultáneas, la base de datos impide vender más de la capacidad.
public class CompraService : ICompraService
{
    private readonly ApplicationDbContext _db;
    private readonly ICatalogoService _catalogo;
    private readonly IQrEntradaService _qr;
    private readonly IEmailService _email;
    private readonly ComprasOptions _opciones;
    private readonly ComisionesOptions _comisiones;
    private const int IntentosReserva = 5;
    private static readonly TimeSpan DuracionBloqueo = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EsperaBloqueo = TimeSpan.FromSeconds(10);

    private readonly IBloqueoDistribuido _bloqueos;
    private readonly IBloqueoAsientos _asientosBloqueados;
    private readonly ILogger<CompraService> _logger;

    public CompraService(ApplicationDbContext db, ICatalogoService catalogo, IQrEntradaService qr, IEmailService email,
        IOptions<ComprasOptions> opciones, IOptions<ComisionesOptions> comisiones, IBloqueoDistribuido bloqueos, IBloqueoAsientos asientosBloqueados,
        ILogger<CompraService> logger)
    {
        _db = db;
        _catalogo = catalogo;
        _qr = qr;
        _email = email;
        _opciones = opciones.Value;
        _comisiones = comisiones.Value;
        _bloqueos = bloqueos;
        _asientosBloqueados = asientosBloqueados;
        _logger = logger;
    }

    // =============================
    // 1️⃣ CREAR ORDEN Y RESERVAR AFORO
    // =============================
    // Zonas sin asientos numerados: el usuario elige zona y cantidad
    public async Task<ResultadoOrden> CrearOrdenAsync(string usuarioId, int eventoId, IReadOnlyDictionary<int, int> cantidades, string? cupon = null,
        CancellationToken ct = default)
    {
        var pedidas = cantidades.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value);
        if (pedidas.Count == 0)
            return ResultadoOrden.Fallo("Selecciona al menos una entrada.");

        var (evento, error) = await EventoEnVentaAsync(eventoId, ct);
        if (evento == null)
            return ResultadoOrden.Fallo(error!);

        var tipos = evento.TiposEntrada.ToDictionary(t => t.Id);
        if (pedidas.Keys.Any(id => !tipos.ContainsKey(id)))
            return ResultadoOrden.Fallo("Tipo de entrada no válido.");

        // HU26: en las zonas con asientos numerados se elige el asiento exacto en el plano
        if (pedidas.Keys.Any(id => tipos[id].AsientosNumerados))
            return ResultadoOrden.Fallo("En este recinto eliges tus asientos en el plano.");

        var items = pedidas.Select(p => new OrdenItem { TipoEntradaId = p.Key, Cantidad = p.Value, PrecioUnitario = tipos[p.Key].Precio }).ToList();
        return await CrearAsync(usuarioId, evento, items, cupon, ct);
    }

    // HU26: recintos con asientos numerados: el usuario elige los asientos exactos en el plano
    public async Task<ResultadoOrden> CrearOrdenConAsientosAsync(string usuarioId, int eventoId, IReadOnlyCollection<int> asientos, string? cupon = null,
        CancellationToken ct = default)
    {
        var ids = asientos.Distinct().ToList();
        if (ids.Count == 0)
            return ResultadoOrden.Fallo("Elige al menos un asiento en el plano.");

        var (evento, error) = await EventoEnVentaAsync(eventoId, ct);
        if (evento == null)
            return ResultadoOrden.Fallo(error!);

        // Un asiento cuya reserva ya venció está libre aunque el proceso periódico aún no haya pasado
        await LiberarExpiradasDelEventoAsync(eventoId, ct);

        var elegidos = await _db.Asientos.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.TipoEntrada!.EventoId == eventoId && a.TipoEntrada.AsientosNumerados)
            .ToListAsync(ct);
        if (elegidos.Count != ids.Count)
            return ResultadoOrden.Fallo("Algún asiento elegido no pertenece a este evento.");

        var ocupados = elegidos.Where(a => a.Estado != EstadoAsiento.Disponible).ToList();
        if (ocupados.Count > 0)
            return AsientosNoDisponibles(ocupados);

        var tipos = evento.TiposEntrada.ToDictionary(t => t.Id);
        var items = elegidos
            .OrderBy(a => a.Fila.Length).ThenBy(a => a.Fila).ThenBy(a => a.Numero)
            .Select(a => new OrdenItem { TipoEntradaId = a.TipoEntradaId, Cantidad = 1, AsientoId = a.Id, PrecioUnitario = tipos[a.TipoEntradaId].Precio })
            .ToList();
        return await CrearAsync(usuarioId, evento, items, cupon, ct, elegidos);
    }

    private async Task<(Evento? Evento, string? Error)> EventoEnVentaAsync(int eventoId, CancellationToken ct)
    {
        var evento = await _db.Eventos.AsNoTracking().Include(e => e.TiposEntrada).FirstOrDefaultAsync(e => e.Id == eventoId, ct);

        if (evento == null || evento.Estado != EstadoEvento.Aprobado)
            return (null, "Este evento no está disponible para la venta.");
        // HU34: en los eventos de entrada libre no se venden entradas
        if (evento.EsGratuito)
            return (null, "Este evento es de entrada libre: no necesitas comprar entrada.");
        if (evento.Fecha <= DateTime.Now)
            return (null, "La venta de este evento ya terminó.");
        return (evento, null);
    }

    // Criterio de HU26: el segundo usuario recibe un aviso para elegir otro asiento
    private static ResultadoOrden AsientosNoDisponibles(IEnumerable<Asiento> asientos)
    {
        var lista = asientos.ToList();
        return ResultadoOrden.Fallo(lista.Count == 1
            ? $"El asiento {lista[0].Etiqueta} ya no está disponible: elige otro en el plano."
            : $"Los asientos {string.Join(", ", lista.Select(a => a.Etiqueta))} ya no están disponibles: elige otros en el plano.");
    }

    private async Task<ResultadoOrden> CrearAsync(string usuarioId, Evento evento, List<OrdenItem> items, string? codigoCupon, CancellationToken ct,
        List<Asiento>? asientos = null)
    {
        if (items.Sum(i => i.Cantidad) > _opciones.MaxEntradasPorOrden)
            return ResultadoOrden.Fallo($"Puedes comprar como máximo {_opciones.MaxEntradasPorOrden} entradas por compra.");

        // HU30: el cupón se valida en el servidor antes de reservar (secuencia de compra: ValidarCupon(código, usuario))
        ResultadoCupon? cupon = null;
        if (!string.IsNullOrWhiteSpace(codigoCupon))
        {
            cupon = await PromocionService.ValidarAsync(_db, codigoCupon, usuarioId, items.Sum(i => i.Subtotal), DateTime.Now, ct);
            if (cupon.Error != null)
                return ResultadoOrden.Fallo(cupon.Error);
        }

        var tipos = evento.TiposEntrada.ToDictionary(t => t.Id);

        // Una sola orden pendiente por persona y evento: la anterior se cancela y libera su reserva
        var pendientes = await _db.Ordenes
            .Where(o => o.UsuarioId == usuarioId && o.EventoId == evento.Id && o.Estado == EstadoOrden.PendientePago)
            .Select(o => o.Codigo)
            .ToListAsync(ct);

        foreach (var codigo in pendientes)
            await LiberarAsync(codigo, EstadoOrden.Cancelada, ct);

        var orden = new Orden
        {
            Codigo = CodigoSeguro.Generar(12),
            UsuarioId = usuarioId,
            EventoId = evento.Id,
            Estado = EstadoOrden.PendientePago,
            FechaCreacion = DateTime.UtcNow,
            FechaExpiracion = DateTime.UtcNow.AddMinutes(_opciones.MinutosReserva),
            Items = items
        };
        orden.Descuento = cupon?.Descuento ?? 0;
        orden.CuponId = cupon?.Cupon?.Id;
        orden.Total = orden.Items.Sum(i => i.Subtotal) - orden.Descuento;

        // HU11: bloqueo distribuido (Redis) por evento: las reservas del mismo evento se procesan de una en una
        await using var bloqueo = await _bloqueos.AdquirirAsync($"aforo:evento:{evento.Id}", DuracionBloqueo, EsperaBloqueo, ct);
        if (bloqueo == null)
            return ResultadoOrden.Fallo("Hay mucha demanda en este momento. Inténtalo de nuevo en unos segundos.");

        // Una reserva vencida devuelve su aforo ya mismo (no espera al proceso que corre cada minuto)
        await LiberarExpiradasDelEventoAsync(evento.Id, ct);

        // HU26 (1ª barrera): cada asiento se aparta en Redis para esta compra durante la reserva (10 minutos)
        var idsAsientos = asientos?.Select(a => a.Id).ToList() ?? new List<int>();
        if (idsAsientos.Count > 0)
        {
            var tomados = await _asientosBloqueados.BloquearAsync(idsAsientos, orden.Codigo, TimeSpan.FromMinutes(_opciones.MinutosReserva), ct);
            if (tomados.Count > 0)
                return AsientosNoDisponibles(asientos!.Where(a => tomados.Contains(a.Id)));
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        foreach (var zona in items.GroupBy(i => i.TipoEntradaId))
        {
            var cantidad = zona.Sum(i => i.Cantidad);
            if (!await ReservarAsync(zona.Key, cantidad, ct))
            {
                await tx.RollbackAsync(ct);
                await _asientosBloqueados.LiberarAsync(idsAsientos, orden.Codigo, ct);
                return ResultadoOrden.Fallo($"Ya no quedan {cantidad} entradas disponibles en la zona {tipos[zona.Key].Nombre}.");
            }
        }

        // HU26 (2ª barrera): Disponible → Bloqueado solo si nadie lo tomó (UPDATE condicional en la BD)
        if (idsAsientos.Count > 0)
        {
            var bloqueados = await _db.Asientos
                .Where(a => idsAsientos.Contains(a.Id) && a.Estado == EstadoAsiento.Disponible)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Estado, EstadoAsiento.Bloqueado), ct);

            if (bloqueados != idsAsientos.Count)
            {
                await tx.RollbackAsync(ct);
                await _asientosBloqueados.LiberarAsync(idsAsientos, orden.Codigo, ct);
                var ocupados = await _db.Asientos.AsNoTracking().Where(a => idsAsientos.Contains(a.Id) && a.Estado != EstadoAsiento.Disponible).ToListAsync(ct);
                return AsientosNoDisponibles(ocupados.Count > 0 ? ocupados : asientos!);
            }
        }

        _db.Ordenes.Add(orden);
        await _db.SaveChangesAsync(ct);

        // HU30: el cupón se aparta para esta compra en la misma transacción (un solo uso y límite de la promoción)
        if (cupon?.Cupon != null && !await ApartarCuponAsync(cupon.Cupon, orden.Id, ct))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            await _asientosBloqueados.LiberarAsync(idsAsientos, orden.Codigo, ct);
            return ResultadoOrden.Fallo("Este cupón ya se está usando en otra compra o la promoción alcanzó su límite de usos.");
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation("🛒 Orden {Codigo} creada: {Cantidad} entradas, total {Total}", orden.Codigo, orden.CantidadEntradas, orden.Total);
        return new ResultadoOrden(orden, null);
    }

    // =============================
    // 2️⃣ CONFIRMAR PAGO Y EMITIR ENTRADAS (idempotente)
    // =============================
    public async Task<ResultadoConfirmacion> ConfirmarPagoAsync(string codigoOrden, string proveedor, string referencia,
        decimal montoPagado, string urlMisEntradas, CancellationToken ct = default)
    {
        var orden = await _db.Ordenes
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Codigo == codigoOrden, ct);

        if (orden == null)
            return ResultadoConfirmacion.NoEncontrada;

        if (orden.Estado == EstadoOrden.Pagada)
            return ResultadoConfirmacion.YaEstabaConfirmada;

        // Nunca se confía en el monto que diga el cliente: debe coincidir con el total de la orden
        if (Math.Abs(montoPagado - orden.Total) > 0.01m)
        {
            _logger.LogWarning("⚠️ Pago {Referencia} de {Monto} no coincide con la orden {Codigo} ({Total})",
                referencia, montoPagado, orden.Codigo, orden.Total);
            return ResultadoConfirmacion.MontoNoCoincide;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Solo una ejecución puede pasar la orden de Pendiente a Pagada (evita doble emisión si llegan
        // a la vez la redirección del navegador y la notificación de la pasarela)
        var marcada = await MarcarPagadaAsync(orden.Codigo, EstadoOrden.PendientePago, proveedor, referencia, ct);

        if (marcada)
        {
            foreach (var item in orden.Items)
            {
                var cantidad = item.Cantidad;
                await _db.TiposEntrada
                    .Where(t => t.Id == item.TipoEntradaId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Reservadas, t => t.Reservadas - cantidad)
                        .SetProperty(t => t.Vendidas, t => t.Vendidas + cantidad)
                        .SetProperty(t => t.RowVersion, t => t.RowVersion + 1), ct);
            }

            // HU26: Bloqueado → Vendido
            var asientos = orden.Items.Where(i => i.AsientoId.HasValue).Select(i => i.AsientoId!.Value).ToList();
            if (asientos.Count > 0)
                await _db.Asientos.Where(a => asientos.Contains(a.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Estado, EstadoAsiento.Vendido), ct);

            // HU30: el cupón apartado queda usado y asociado a esta compra
            await MarcarCuponUsadoAsync(orden, ct);
        }
        else
        {
            // La orden ya no estaba pendiente: ¿otro proceso la confirmó, o la reserva caducó?
            var estadoActual = await _db.Ordenes.Where(o => o.Id == orden.Id).Select(o => o.Estado).FirstAsync(ct);
            if (estadoActual == EstadoOrden.Pagada)
            {
                await tx.RollbackAsync(ct);
                return ResultadoConfirmacion.YaEstabaConfirmada;
            }

            // Reserva caducada pero el pago llegó: se intenta vender directo si aún hay aforo
            if (!await VenderSinReservaAsync(orden, ct))
            {
                await tx.RollbackAsync(ct);
                await _db.Ordenes.Where(o => o.Id == orden.Id && o.Estado != EstadoOrden.Pagada)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.Estado, EstadoOrden.RequiereReembolso)
                        .SetProperty(o => o.ProveedorPago, proveedor)
                        .SetProperty(o => o.ReferenciaPago, referencia), ct);

                _logger.LogError("💸 Orden {Codigo} pagada sin aforo disponible: requiere reembolso", orden.Codigo);
                return ResultadoConfirmacion.RequiereReembolso;
            }

            if (!await MarcarPagadaAsync(orden.Codigo, estadoActual, proveedor, referencia, ct))
            {
                await tx.RollbackAsync(ct);
                return ResultadoConfirmacion.YaEstabaConfirmada;
            }

            // La reserva caducada ya había devuelto el cupón: se vuelve a tomar, porque el pago se hizo con el descuento
            await MarcarCuponUsadoAsync(orden, ct, retomar: true);
        }

        // Emisión: una entrada con QR único por cada persona
        var entradas = orden.Items
            .SelectMany(i => Enumerable.Range(0, i.Cantidad).Select(_ => new Entrada
            {
                Codigo = CodigoSeguro.Generar(),
                OrdenId = orden.Id,
                EventoId = orden.EventoId,
                TipoEntradaId = i.TipoEntradaId,
                AsientoId = i.AsientoId, // índice único: un asiento nunca queda en dos entradas
                UsuarioId = orden.UsuarioId,
                Estado = EstadoEntrada.Activa,
                FechaEmision = DateTime.UtcNow
            }))
            .ToList();

        _db.Entradas.AddRange(entradas);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // El asiento ya está Vendido en la BD: su apartado temporal en Redis ya no hace falta
        var vendidos = orden.Items.Where(i => i.AsientoId.HasValue).Select(i => i.AsientoId!.Value).ToList();
        if (vendidos.Count > 0)
            await _asientosBloqueados.LiberarAsync(vendidos, orden.Codigo, ct);

        _logger.LogInformation("✅ Orden {Codigo} pagada ({Proveedor} {Referencia}): {Cantidad} entradas emitidas",
            orden.Codigo, proveedor, referencia, entradas.Count);

        await _catalogo.InvalidarAsync(ct);
        await EnviarEntradasAsync(orden.Id, urlMisEntradas, ct);

        return ResultadoConfirmacion.Confirmada;
    }

    // =============================
    // 3️⃣ CANCELAR / EXPIRAR: devuelve la reserva al aforo
    // =============================
    public async Task<bool> CancelarAsync(string codigoOrden, string usuarioId, CancellationToken ct = default)
    {
        var esDelUsuario = await _db.Ordenes.AnyAsync(o => o.Codigo == codigoOrden && o.UsuarioId == usuarioId, ct);
        return esDelUsuario && await LiberarAsync(codigoOrden, EstadoOrden.Cancelada, ct);
    }

    public async Task<bool> RechazarPagoAsync(string codigoOrden, CancellationToken ct = default)
    {
        var liberada = await LiberarAsync(codigoOrden, EstadoOrden.Rechazada, ct);
        if (liberada)
        {
            _logger.LogInformation("💳 Pago rechazado: la orden {Codigo} liberó su reserva", codigoOrden);
            await _catalogo.InvalidarAsync(ct);
        }
        return liberada;
    }

    public async Task<int> LiberarExpiradasAsync(CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow;
        var vencidas = await _db.Ordenes
            .Where(o => o.Estado == EstadoOrden.PendientePago && o.FechaExpiracion < ahora)
            .Select(o => o.Codigo)
            .ToListAsync(ct);

        var liberadas = 0;
        foreach (var codigo in vencidas)
        {
            if (await LiberarAsync(codigo, EstadoOrden.Expirada, ct))
                liberadas++;
        }

        if (liberadas > 0)
        {
            _logger.LogInformation("⏰ {Cantidad} reservas vencidas liberadas", liberadas);
            await _catalogo.InvalidarAsync(ct);
        }

        return liberadas;
    }

    // =============================
    // 🔐 RESERVA CON TOKEN DE CONCURRENCIA (RowVersion)
    // =============================
    // Se lee la zona con su versión y se actualiza "WHERE RowVersion = la leída": si otra compra la cambió
    // entre medio, no se toca ninguna fila y se reintenta con datos frescos. La condición de stock en el
    // WHERE es una segunda barrera: aunque algo falle, la base nunca deja Vendidas + Reservadas > Capacidad.
    private async Task<bool> ReservarAsync(int tipoEntradaId, int cantidad, CancellationToken ct)
    {
        for (var intento = 0; intento < IntentosReserva; intento++)
        {
            var zona = await _db.TiposEntrada.AsNoTracking()
                .Where(t => t.Id == tipoEntradaId)
                .Select(t => new { Libres = t.Capacidad - t.Vendidas - t.Reservadas, t.RowVersion })
                .FirstOrDefaultAsync(ct);

            if (zona == null || zona.Libres < cantidad)
                return false;

            var version = zona.RowVersion;
            var actualizadas = await _db.TiposEntrada
                .Where(t => t.Id == tipoEntradaId && t.RowVersion == version
                            && t.Capacidad - t.Vendidas - t.Reservadas >= cantidad
                            && _db.Eventos.Any(e => e.Id == t.EventoId && e.Estado == EstadoEvento.Aprobado)) // por si se canceló mientras tanto
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Reservadas, t => t.Reservadas + cantidad)
                    .SetProperty(t => t.RowVersion, t => t.RowVersion + 1), ct);

            if (actualizadas == 1)
                return true;
        }

        _logger.LogWarning("Reserva de {Cantidad} entradas en la zona {Zona} no lograda tras {Intentos} intentos por concurrencia",
            cantidad, tipoEntradaId, IntentosReserva);
        return false;
    }

    private async Task LiberarExpiradasDelEventoAsync(int eventoId, CancellationToken ct)
    {
        var ahora = DateTime.UtcNow;
        var vencidas = await _db.Ordenes
            .Where(o => o.EventoId == eventoId && o.Estado == EstadoOrden.PendientePago && o.FechaExpiracion < ahora)
            .Select(o => o.Codigo)
            .ToListAsync(ct);

        foreach (var codigo in vencidas)
            await LiberarAsync(codigo, EstadoOrden.Expirada, ct);
    }

    private async Task<bool> LiberarAsync(string codigoOrden, EstadoOrden nuevoEstado, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Solo libera quien logra sacar la orden de Pendiente: la reserva nunca se devuelve dos veces
        var cambiadas = await _db.Ordenes
            .Where(o => o.Codigo == codigoOrden && o.Estado == EstadoOrden.PendientePago)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Estado, nuevoEstado), ct);

        if (cambiadas == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var items = await _db.OrdenItems
            .Where(i => i.Orden!.Codigo == codigoOrden)
            .Select(i => new { i.TipoEntradaId, i.Cantidad, i.AsientoId })
            .ToListAsync(ct);

        foreach (var item in items)
        {
            var cantidad = item.Cantidad;
            await _db.TiposEntrada
                .Where(t => t.Id == item.TipoEntradaId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Reservadas, t => t.Reservadas - cantidad)
                    .SetProperty(t => t.RowVersion, t => t.RowVersion + 1), ct);
        }

        // HU30: el cupón apartado vuelve a estar disponible para su dueño y se devuelve el uso a la promoción
        var orden = await _db.Ordenes.Where(o => o.Codigo == codigoOrden).Select(o => new { o.Id, o.CuponId }).FirstAsync(ct);
        if (orden.CuponId != null)
        {
            var promocionId = await _db.Cupones.Where(c => c.Id == orden.CuponId).Select(c => c.PromocionId).FirstAsync(ct);
            var devuelto = await _db.Cupones
                .Where(c => c.Id == orden.CuponId && c.OrdenId == orden.Id && !c.Usado)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.OrdenId, (int?)null), ct);
            if (devuelto == 1)
                await _db.Promociones.Where(p => p.Id == promocionId && p.Usos > 0)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Usos, p => p.Usos - 1), ct);
        }

        // HU26 / ciclo de vida del Asiento: Bloqueado → Disponible (expira, pago rechazado o compra cancelada)
        var asientos = items.Where(i => i.AsientoId.HasValue).Select(i => i.AsientoId!.Value).ToList();
        if (asientos.Count > 0)
            await _db.Asientos
                .Where(a => asientos.Contains(a.Id) && a.Estado == EstadoAsiento.Bloqueado)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Estado, EstadoAsiento.Disponible), ct);

        await tx.CommitAsync(ct);

        if (asientos.Count > 0)
            await _asientosBloqueados.LiberarAsync(asientos, codigoOrden, ct);
        return true;
    }

    private async Task<bool> VenderSinReservaAsync(Orden orden, CancellationToken ct)
    {
        foreach (var item in orden.Items)
        {
            var cantidad = item.Cantidad;
            var vendidas = await _db.TiposEntrada
                .Where(t => t.Id == item.TipoEntradaId && t.Capacidad - t.Vendidas - t.Reservadas >= cantidad
                            && _db.Eventos.Any(e => e.Id == t.EventoId && e.Estado == EstadoEvento.Aprobado)) // no se vende si se canceló
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Vendidas, t => t.Vendidas + cantidad)
                    .SetProperty(t => t.RowVersion, t => t.RowVersion + 1), ct);

            if (vendidas == 0)
                return false;
        }

        // HU26: un pago tardío solo se acepta si sus asientos siguen libres (nadie los tomó mientras tanto)
        var asientos = orden.Items.Where(i => i.AsientoId.HasValue).Select(i => i.AsientoId!.Value).ToList();
        if (asientos.Count > 0)
        {
            var tomados = await _db.Asientos
                .Where(a => asientos.Contains(a.Id) && a.Estado == EstadoAsiento.Disponible)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Estado, EstadoAsiento.Vendido), ct);
            if (tomados != asientos.Count)
                return false; // la transacción se revierte completa
        }

        return true;
    }

    // =============================
    // 🎁 CUPÓN DE UN SOLO USO (HU30)
    // =============================
    // Dos UPDATE condicionales: el cupón solo se aparta si nadie lo tiene y no está usado, y la promoción solo
    // suma un uso si no llegó a su límite. Si alguno falla, la transacción de la reserva se revierte completa.
    private async Task<bool> ApartarCuponAsync(Cupon cupon, int ordenId, CancellationToken ct)
    {
        int? id = ordenId;
        var apartado = await _db.Cupones
            .Where(c => c.Id == cupon.Id && !c.Usado && c.OrdenId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.OrdenId, id), ct);
        if (apartado == 0)
            return false;

        var usos = await _db.Promociones
            .Where(p => p.Id == cupon.PromocionId && p.Activa && (p.LimiteUsos == null || p.Usos < p.LimiteUsos))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Usos, p => p.Usos + 1), ct);
        return usos == 1;
    }

    private async Task MarcarCuponUsadoAsync(Orden orden, CancellationToken ct, bool retomar = false)
    {
        if (orden.CuponId == null)
            return;

        int? ordenId = orden.Id;
        DateTime? ahora = DateTime.UtcNow;
        var usados = await _db.Cupones
            .Where(c => c.Id == orden.CuponId && !c.Usado && (c.OrdenId == orden.Id || (retomar && c.OrdenId == null)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Usado, true)
                .SetProperty(c => c.OrdenId, ordenId)
                .SetProperty(c => c.FechaUso, ahora), ct);

        if (retomar && usados == 1)
        {
            // El pago ya se hizo con el descuento: se respeta aunque la promoción haya llegado a su límite mientras tanto
            var promocionId = await _db.Cupones.Where(c => c.Id == orden.CuponId).Select(c => c.PromocionId).FirstAsync(ct);
            await _db.Promociones.Where(p => p.Id == promocionId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Usos, p => p.Usos + 1), ct);
        }
        else if (usados == 0)
            _logger.LogWarning("🎁 El cupón {Cupon} de la orden {Codigo} no se pudo marcar como usado (ya usado en otra compra)", orden.CuponId, orden.Codigo);
    }

    private async Task<bool> MarcarPagadaAsync(string codigo, EstadoOrden desde, string proveedor, string referencia, CancellationToken ct)
    {
        DateTime? ahora = DateTime.UtcNow;
        var total = await _db.Ordenes.Where(o => o.Codigo == codigo).Select(o => o.Total).FirstAsync(ct);

        // La comisión se congela en el mismo UPDATE que confirma el pago (OE5: trazable y sin recalcular después)
        var porcentaje = _comisiones.Porcentaje;
        var comision = CalcularComision(total, porcentaje);

        var filas = await _db.Ordenes
            .Where(o => o.Codigo == codigo && o.Estado == desde)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Estado, EstadoOrden.Pagada)
                .SetProperty(o => o.FechaPago, ahora)
                .SetProperty(o => o.ProveedorPago, proveedor)
                .SetProperty(o => o.ReferenciaPago, referencia)
                .SetProperty(o => o.PorcentajeComision, porcentaje)
                .SetProperty(o => o.Comision, comision), ct);

        return filas == 1;
    }

    // Redondeo comercial a céntimos
    public static decimal CalcularComision(decimal total, decimal porcentaje) =>
        Math.Round(total * porcentaje / 100m, 2, MidpointRounding.AwayFromZero);

    // Correo con un QR adjunto por entrada (HU07)
    private async Task EnviarEntradasAsync(int ordenId, string urlMisEntradas, CancellationToken ct)
    {
        try
        {
            var orden = await _db.Ordenes
                .AsNoTracking()
                .Include(o => o.Usuario)
                .Include(o => o.Evento)
                .Include(o => o.Items).ThenInclude(i => i.TipoEntrada)
                .Include(o => o.Entradas).ThenInclude(e => e.TipoEntrada)
                .Include(o => o.Entradas).ThenInclude(e => e.Asiento)
                .AsSplitQuery()
                .FirstAsync(o => o.Id == ordenId, ct);

            if (orden.Usuario?.Email == null)
                return;

            // HU27: el QR lleva cifrada la ubicación (zona, fila y asiento)
            var adjuntos = orden.Entradas
                .Select((e, i) => new AdjuntoCorreo($"entrada-{i + 1}-{e.TipoEntrada?.Nombre}.png", _qr.GenerarPng(e.Codigo, UbicacionEntrada.De(e).ParaQr)))
                .ToList();

            // HU27: plano del recinto con los asientos (o zonas) de esta compra resaltados
            var evento = await _db.Eventos.AsNoTracking().Include(e => e.TiposEntrada).FirstAsync(e => e.Id == orden.EventoId, ct);
            var asientosEvento = evento.PlantillaPlano == PlantillaPlano.Teatro
                ? await _db.Asientos.AsNoTracking().Where(a => a.TipoEntrada!.EventoId == evento.Id).ToListAsync(ct)
                : new List<Asiento>();
            var plano = Services.Plano.PlanoService.PngDeEvento(evento, asientosEvento,
                orden.Entradas.Where(e => e.AsientoId.HasValue).Select(e => e.AsientoId!.Value).ToHashSet(),
                orden.Entradas.Where(e => e.AsientoId == null).Select(e => e.TipoEntradaId).ToHashSet());
            adjuntos.Add(new AdjuntoCorreo("plano-tu-ubicacion.png", plano));

            await _email.EnviarAsync(orden.Usuario.Email, $"Tus entradas para {orden.Evento!.Titulo}",
                EmailTemplates.EntradasCompradas(orden.Usuario.NombreCompleto, orden, orden.Evento, urlMisEntradas), adjuntos);
        }
        catch (Exception ex)
        {
            // Las entradas ya están emitidas y visibles en "Mis entradas": un fallo de correo no revierte la compra
            _logger.LogError(ex, "No se pudo enviar el correo de entradas de la orden {OrdenId}", ordenId);
        }
    }
}
