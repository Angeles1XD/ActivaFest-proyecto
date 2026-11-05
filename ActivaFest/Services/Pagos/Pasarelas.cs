using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ActivaFest.Models;

namespace ActivaFest.Services.Pagos;

public class PagosOptions
{
    // "MercadoPago" o "Simulado"
    public string Proveedor { get; set; } = "Simulado";

    // La pasarela simulada solo funciona en Development salvo que se habilite explícitamente (demo / pruebas)
    public bool PermitirSimuladoFueraDeDesarrollo { get; set; }

    public MercadoPagoOptions MercadoPago { get; set; } = new();
}

public class MercadoPagoOptions
{
    public string AccessToken { get; set; } = "";

    // Clave secreta de webhooks (panel de Mercado Pago > Webhooks) para validar la firma x-signature
    public string WebhookSecret { get; set; } = "";
}

public record UrlsRetorno(string Exito, string Fallo, string Pendiente, string Notificacion);

public record InicioPago(string UrlRedireccion);

// Lo que se cobra en la pasarela: entradas (Orden) o un paquete publicitario (ContratoDestacado)
public record ItemCobro(string Id, string Titulo, int Cantidad, decimal PrecioUnitario);

public record SolicitudCobro(string Codigo, IReadOnlyList<ItemCobro> Items, DateTime ExpiracionUtc)
{
    public decimal Total => Items.Sum(i => i.PrecioUnitario * i.Cantidad);

    public bool EsDestacado => Codigo.StartsWith(ContratoDestacado.PrefijoCodigo, StringComparison.Ordinal);

    public bool EsPublicacion => Codigo.StartsWith(PagoPublicacion.PrefijoCodigo, StringComparison.Ordinal);

    // HU30: con cupón se cobra una sola línea por el total con descuento (la pasarela no admite precios negativos)
    public static SolicitudCobro DesdeOrden(Orden orden, string tituloEvento) => new(
        orden.Codigo,
        orden.Descuento > 0
            ? new[] { new ItemCobro($"orden-{orden.Codigo}", $"{tituloEvento} - {orden.CantidadEntradas} entradas (con cupón)", 1, orden.Total) }
            : orden.Items.Select(i => new ItemCobro(
                i.TipoEntradaId.ToString(CultureInfo.InvariantCulture),
                $"{tituloEvento} - {i.TipoEntrada?.Nombre ?? "Entrada"}",
                i.Cantidad,
                i.PrecioUnitario)).ToList(),
        orden.FechaExpiracion);

    public static SolicitudCobro DesdeContrato(ContratoDestacado contrato, string tituloEvento, string nombrePaquete) => new(
        contrato.Codigo,
        new[] { new ItemCobro($"paquete-{contrato.PaqueteId}", $"Destacado {nombrePaquete} ({contrato.Dias} días) - {tituloEvento}", 1, contrato.Precio) },
        DateTime.UtcNow.AddHours(1));

    // HU34: tarifa única por publicar un evento gratuito
    public static SolicitudCobro DesdePublicacion(PagoPublicacion pago, string tituloEvento) => new(
        pago.Codigo,
        new[] { new ItemCobro($"publicacion-{pago.EventoId}", $"Publicación de evento gratuito - {tituloEvento}", 1, pago.Monto) },
        DateTime.UtcNow.AddHours(1));
}

public record PagoExterno(string Referencia, string Estado, string? CodigoOrden, decimal Monto)
{
    public bool Aprobado => Estado == "approved";
}

public interface IPasarelaPago
{
    string Nombre { get; }

    bool Disponible { get; }

    Task<InicioPago> IniciarAsync(SolicitudCobro cobro, string? emailPagador, UrlsRetorno urls, CancellationToken ct = default);

    // Consulta el pago directamente a la pasarela: nunca se confía en los parámetros que trae el navegador
    Task<PagoExterno?> ConsultarPagoAsync(string referencia, CancellationToken ct = default);
}

// =============================
// 🧪 PASARELA SIMULADA (desarrollo y demos)
// =============================
public class PasarelaSimulada : IPasarelaPago
{
    public const string NombreProveedor = "Simulado";

    private readonly LinkGenerator _links;
    private readonly IHttpContextAccessor _http;

    public PasarelaSimulada(LinkGenerator links, IHttpContextAccessor http)
    {
        _links = links;
        _http = http;
    }

    public string Nombre => NombreProveedor;

    public bool Disponible => true;

    public Task<InicioPago> IniciarAsync(SolicitudCobro cobro, string? emailPagador, UrlsRetorno urls, CancellationToken ct = default)
    {
        // Cada tipo de cobro tiene su propia pantalla de prueba
        var controlador = cobro.EsDestacado ? "Destacados" : cobro.EsPublicacion ? "Publicacion" : "Compras";
        var url = _links.GetPathByAction(_http.HttpContext!, "PasarelaSimulada", controlador, new { codigo = cobro.Codigo })!;
        return Task.FromResult(new InicioPago(url));
    }

    // En la simulación el "pago" lo confirma directamente el controlador
    public Task<PagoExterno?> ConsultarPagoAsync(string referencia, CancellationToken ct = default) =>
        Task.FromResult<PagoExterno?>(null);
}

public class PasarelaNoDisponible : IPasarelaPago
{
    public string Nombre => "NoDisponible";

    public bool Disponible => false;

    public Task<InicioPago> IniciarAsync(SolicitudCobro cobro, string? emailPagador, UrlsRetorno urls, CancellationToken ct = default) =>
        throw new InvalidOperationException("No hay una pasarela de pago configurada.");

    public Task<PagoExterno?> ConsultarPagoAsync(string referencia, CancellationToken ct = default) =>
        Task.FromResult<PagoExterno?>(null);
}

// =============================
// 💳 MERCADO PAGO CHECKOUT PRO (sandbox) — HU06
// =============================
// El pago se hace en la página de Mercado Pago: ActivaFest nunca ve ni guarda datos de tarjeta.
public class MercadoPagoPasarela : IPasarelaPago
{
    public const string NombreProveedor = "MercadoPago";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly MercadoPagoOptions _opciones;
    private readonly ILogger<MercadoPagoPasarela> _logger;

    public MercadoPagoPasarela(HttpClient http, IOptions<PagosOptions> opciones, ILogger<MercadoPagoPasarela> logger)
    {
        _http = http;
        _opciones = opciones.Value.MercadoPago;
        _logger = logger;

        _http.BaseAddress ??= new Uri("https://api.mercadopago.com/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opciones.AccessToken);
    }

    public string Nombre => NombreProveedor;

    public bool Disponible => !string.IsNullOrWhiteSpace(_opciones.AccessToken);

    // Las credenciales de prueba empiezan con TEST-
    private bool EsSandbox => _opciones.AccessToken.StartsWith("TEST-", StringComparison.Ordinal);

    public async Task<InicioPago> IniciarAsync(SolicitudCobro cobro, string? emailPagador, UrlsRetorno urls, CancellationToken ct = default)
    {
        var esHttps = urls.Exito.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var preferencia = new
        {
            Items = cobro.Items.Select(i => new
            {
                i.Id,
                Title = i.Titulo,
                Quantity = i.Cantidad,
                UnitPrice = i.PrecioUnitario,
                CurrencyId = "PEN"
            }),
            ExternalReference = cobro.Codigo,
            Payer = emailPagador == null ? null : new { Email = emailPagador },
            BackUrls = new { Success = urls.Exito, Failure = urls.Fallo, Pending = urls.Pendiente },
            // Mercado Pago solo acepta retorno automático y notificaciones hacia URLs públicas HTTPS
            AutoReturn = esHttps ? "approved" : null,
            NotificationUrl = urls.Notificacion.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? urls.Notificacion : null,
            Expires = true,
            ExpirationDateTo = cobro.ExpiracionUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            StatementDescriptor = "ACTIVAFEST"
        };

        using var respuesta = await _http.PostAsJsonAsync("checkout/preferences", preferencia, Json, ct);
        var cuerpo = await respuesta.Content.ReadAsStringAsync(ct);

        if (!respuesta.IsSuccessStatusCode)
        {
            _logger.LogError("Mercado Pago rechazó la preferencia de {Codigo}: {Estado} {Cuerpo}", cobro.Codigo, (int)respuesta.StatusCode, cuerpo);
            throw new InvalidOperationException("No se pudo iniciar el pago con Mercado Pago.");
        }

        using var doc = JsonDocument.Parse(cuerpo);
        var url = EsSandbox && doc.RootElement.TryGetProperty("sandbox_init_point", out var sandbox)
            ? sandbox.GetString()
            : doc.RootElement.GetProperty("init_point").GetString();

        return new InicioPago(url!);
    }

    public async Task<PagoExterno?> ConsultarPagoAsync(string referencia, CancellationToken ct = default)
    {
        if (!long.TryParse(referencia, out _))
            return null;

        using var respuesta = await _http.GetAsync($"v1/payments/{referencia}", ct);
        if (!respuesta.IsSuccessStatusCode)
        {
            _logger.LogWarning("No se pudo consultar el pago {Referencia} en Mercado Pago: {Estado}", referencia, (int)respuesta.StatusCode);
            return null;
        }

        using var doc = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(ct));
        var raiz = doc.RootElement;

        return new PagoExterno(
            referencia,
            raiz.GetProperty("status").GetString() ?? "",
            raiz.TryGetProperty("external_reference", out var ext) && ext.ValueKind == JsonValueKind.String ? ext.GetString() : null,
            raiz.GetProperty("transaction_amount").GetDecimal());
    }

    // Valida la cabecera x-signature de los webhooks: "ts=...,v1=HMAC_SHA256(secreto, 'id:{id};request-id:{rid};ts:{ts};')"
    public static bool FirmaWebhookValida(string secreto, string? xSignature, string? xRequestId, string dataId)
    {
        if (string.IsNullOrEmpty(xSignature))
            return false;

        var partes = xSignature.Split(',')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

        if (!partes.TryGetValue("ts", out var ts) || !partes.TryGetValue("v1", out var v1))
            return false;

        var manifiesto = $"id:{dataId.ToLowerInvariant()};request-id:{xRequestId};ts:{ts};";
        var calculada = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), Encoding.UTF8.GetBytes(manifiesto)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(calculada.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(v1.ToLowerInvariant()));
    }
}
