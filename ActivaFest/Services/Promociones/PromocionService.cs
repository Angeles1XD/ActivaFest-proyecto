using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Email;

namespace ActivaFest.Services.Promociones;

public class PromocionInput
{
    public string Nombre { get; set; } = "";
    public TipoDescuento TipoDescuento { get; set; }
    public decimal Valor { get; set; }
    public DateTime FechaInicio { get; set; }
    public DateTime FechaFin { get; set; }
    public int? LimiteUsos { get; set; }
    public string? CriterioSegmento { get; set; }
    public List<string> Clientes { get; set; } = new();
}

public record ResultadoPromocion(Promocion? Promocion, int Enviados, int Omitidos, string? Error);

public record ResultadoCupon(Cupon? Cupon, decimal Descuento, string? Error)
{
    public static ResultadoCupon Invalido(string error) => new(null, 0, error);
}

public record ResumenPromocion(Promocion Promocion, int Cupones, int Usados);

public interface IPromocionService
{
    // Crea la promoción, genera un cupón personal por cliente con consentimiento y se lo envía por correo
    Task<ResultadoPromocion> CrearAsync(PromocionInput input, string administradorId, string urlCatalogo, CancellationToken ct = default);

    // Validación del cupón en el servidor (secuencia de compra: ValidarCupon(código, usuario))
    Task<ResultadoCupon> ValidarAsync(string codigo, string usuarioId, decimal subtotal, CancellationToken ct = default);

    Task<List<ResumenPromocion>> ListarAsync(CancellationToken ct = default);
    Task<Promocion?> DetalleAsync(int id, CancellationToken ct = default);
    Task<bool> DesactivarAsync(int id, CancellationToken ct = default);

    // Cupones del usuario que aún puede usar
    Task<List<Cupon>> CuponesDisponiblesAsync(string usuarioId, CancellationToken ct = default);
}

// =============================
// 🎁 PROMOCIONES Y CUPONES (HU30)
// =============================
public class PromocionService : IPromocionService
{
    public const int MaxClientes = 5000;

    private readonly ApplicationDbContext _db;
    private readonly IEmailService _email;
    private readonly ILogger<PromocionService> _logger;

    public PromocionService(ApplicationDbContext db, IEmailService email, ILogger<PromocionService> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    public async Task<ResultadoPromocion> CrearAsync(PromocionInput input, string administradorId, string urlCatalogo, CancellationToken ct = default)
    {
        var error = Validar(input, DateTime.Today);
        if (error != null)
            return new ResultadoPromocion(null, 0, 0, error);

        var pedidos = input.Clientes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();

        // El consentimiento se vuelve a comprobar en el servidor: aunque el formulario traiga a alguien más, solo
        // reciben cupón las cuentas activas que aceptaron recibir promociones
        var destinatarios = await _db.Users.AsNoTracking()
            .Where(u => pedidos.Contains(u.Id) && u.AceptaPromociones && u.Activo && u.Email != null)
            .Select(u => new { u.Id, u.Email, u.NombreCompleto })
            .ToListAsync(ct);

        if (destinatarios.Count == 0)
            return new ResultadoPromocion(null, 0, pedidos.Count, "Ninguno de los clientes seleccionados aceptó recibir promociones.");

        var promocion = new Promocion
        {
            Nombre = input.Nombre.Trim(),
            TipoDescuento = input.TipoDescuento,
            Valor = Math.Round(input.Valor, 2),
            FechaInicio = input.FechaInicio.Date,
            FechaFin = input.FechaFin.Date,
            LimiteUsos = input.LimiteUsos,
            CriterioSegmento = Recortar(input.CriterioSegmento ?? "Selección manual", 300),
            CreadaPorId = administradorId,
            FechaCreacion = DateTime.UtcNow
        };

        var codigos = new HashSet<string>();
        foreach (var d in destinatarios)
        {
            string codigo;
            do codigo = GenerarCodigo(); while (!codigos.Add(codigo) || await _db.Cupones.AnyAsync(c => c.Codigo == codigo, ct));
            promocion.Cupones.Add(new Cupon { UsuarioId = d.Id, Codigo = codigo });
        }

        _db.Promociones.Add(promocion);
        await _db.SaveChangesAsync(ct);

        // Envío por correo (cola con reintentos): cada cliente recibe solo su propio código
        var porUsuario = promocion.Cupones.ToDictionary(c => c.UsuarioId, c => c.Codigo);
        foreach (var d in destinatarios)
            await _email.EnviarAsync(d.Email!, $"🎁 Tienes un cupón: {promocion.Nombre}",
                EmailTemplates.CuponPromocion(d.NombreCompleto, promocion, porUsuario[d.Id], urlCatalogo));

        _logger.LogInformation("🎁 Promoción {Id} creada: {Enviados} cupones enviados, {Omitidos} clientes sin consentimiento omitidos",
            promocion.Id, destinatarios.Count, pedidos.Count - destinatarios.Count);
        return new ResultadoPromocion(promocion, destinatarios.Count, pedidos.Count - destinatarios.Count, null);
    }

    public static string? Validar(PromocionInput p, DateTime hoy)
    {
        if (string.IsNullOrWhiteSpace(p.Nombre)) return "Escribe el nombre de la promoción.";
        if (p.Nombre.Trim().Length > 100) return "El nombre admite hasta 100 caracteres.";
        if (!Enum.IsDefined(p.TipoDescuento)) return "Elige el tipo de descuento.";
        if (p.Valor <= 0) return "El valor del descuento debe ser mayor que cero.";
        if (p.TipoDescuento == TipoDescuento.Porcentaje && p.Valor > 100) return "El porcentaje no puede superar el 100 %.";
        if (p.TipoDescuento == TipoDescuento.Monto && p.Valor > 10000) return "El monto del descuento no puede superar S/ 10,000.";
        if (p.FechaFin.Date < p.FechaInicio.Date) return "La fecha de fin debe ser igual o posterior a la de inicio.";
        if (p.FechaFin.Date < hoy.Date) return "La vigencia ya terminó: elige una fecha de fin futura.";
        if (p.LimiteUsos is < 1) return "El límite de usos debe ser al menos 1.";
        if (p.Clientes.Count == 0) return "Selecciona al menos un cliente en el filtro de clientes.";
        if (p.Clientes.Count > MaxClientes) return $"Puedes enviar la promoción a {MaxClientes} clientes como máximo.";
        return null;
    }

    // Códigos legibles y difíciles de adivinar: "AF-" + 8 caracteres sin 0/O ni 1/I (32^8 ≈ 10^12 combinaciones)
    private const string Alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string GenerarCodigo() =>
        "AF-" + new string(Enumerable.Range(0, 8).Select(_ => Alfabeto[RandomNumberGenerator.GetInt32(Alfabeto.Length)]).ToArray());

    public static string Normalizar(string? codigo) => (codigo ?? "").Trim().ToUpperInvariant();

    public Task<ResultadoCupon> ValidarAsync(string codigo, string usuarioId, decimal subtotal, CancellationToken ct = default) =>
        ValidarAsync(_db, codigo, usuarioId, subtotal, DateTime.Now, ct);

    // También la usa CompraService al reservar: el cupón se valida siempre en el servidor
    public static async Task<ResultadoCupon> ValidarAsync(ApplicationDbContext db, string codigo, string usuarioId, decimal subtotal,
        DateTime ahoraLocal, CancellationToken ct = default)
    {
        var normalizado = Normalizar(codigo);
        if (normalizado.Length == 0)
            return ResultadoCupon.Invalido("Escribe el código del cupón.");

        var cupon = await db.Cupones.AsNoTracking().Include(c => c.Promocion).FirstOrDefaultAsync(c => c.Codigo == normalizado, ct);

        // Mismo mensaje si no existe o si es de otra persona: no se revela qué códigos existen
        if (cupon == null || cupon.UsuarioId != usuarioId)
            return ResultadoCupon.Invalido("El cupón no es válido para tu cuenta.");
        if (cupon.Usado)
            return ResultadoCupon.Invalido("Este cupón ya fue usado.");

        var p = cupon.Promocion!;
        if (!p.Activa)
            return ResultadoCupon.Invalido("Esta promoción ya no está disponible.");
        if (ahoraLocal < p.FechaInicio.Date)
            return ResultadoCupon.Invalido($"Este cupón se puede usar desde el {p.FechaInicio:dd/MM/yyyy}.");
        if (ahoraLocal >= p.FechaFin.Date.AddDays(1))
            return ResultadoCupon.Invalido($"Este cupón venció el {p.FechaFin:dd/MM/yyyy}.");
        if (p.LimiteUsos != null && p.Usos >= p.LimiteUsos && cupon.OrdenId == null)
            return ResultadoCupon.Invalido("La promoción alcanzó su límite de usos.");

        return new ResultadoCupon(cupon, p.DescuentoPara(subtotal), null);
    }

    public async Task<List<ResumenPromocion>> ListarAsync(CancellationToken ct = default) =>
        (await _db.Promociones.AsNoTracking()
            .OrderByDescending(p => p.FechaCreacion)
            .Select(p => new { Promocion = p, Cupones = p.Cupones.Count, Usados = p.Cupones.Count(c => c.Usado) })
            .ToListAsync(ct))
        .Select(x => new ResumenPromocion(x.Promocion, x.Cupones, x.Usados))
        .ToList();

    public Task<Promocion?> DetalleAsync(int id, CancellationToken ct = default) =>
        _db.Promociones.AsNoTracking()
            .Include(p => p.CreadaPor)
            .Include(p => p.Cupones).ThenInclude(c => c.Orden)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<bool> DesactivarAsync(int id, CancellationToken ct = default) =>
        await _db.Promociones.Where(p => p.Id == id && p.Activa)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Activa, false), ct) == 1;

    public async Task<List<Cupon>> CuponesDisponiblesAsync(string usuarioId, CancellationToken ct = default)
    {
        var hoy = DateTime.Today;
        return await _db.Cupones.AsNoTracking()
            .Include(c => c.Promocion)
            .Where(c => c.UsuarioId == usuarioId && !c.Usado && c.Promocion!.Activa
                        && c.Promocion.FechaInicio <= hoy && c.Promocion.FechaFin >= hoy)
            .OrderBy(c => c.Promocion!.FechaFin)
            .ToListAsync(ct);
    }

    private static string Recortar(string texto, int largo) => texto.Length > largo ? texto[..largo] : texto;
}
