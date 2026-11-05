using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Entradas;
using ActivaFest.Services.Pagos;

namespace ActivaFest.Controllers
{
    // =============================
    // 🛒 FLUJO DE COMPRA (HU06): ficha → checkout → pasarela → confirmación
    // =============================
    [Authorize]
    public class ComprasController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly ICompraService _compras;
        private readonly IPasarelaPago _pasarela;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly PagosOptions _pagos;
        private readonly ILogger<ComprasController> _logger;

        public ComprasController(ApplicationDbContext db, ICompraService compras, IPasarelaPago pasarela,
            UserManager<ApplicationUser> userManager, IOptions<PagosOptions> pagos, ILogger<ComprasController> logger)
        {
            _db = db;
            _compras = compras;
            _pasarela = pasarela;
            _userManager = userManager;
            _pagos = pagos.Value;
            _logger = logger;
        }

        private string UsuarioId => _userManager.GetUserId(User)!;

        private string UrlMisEntradas => Url.Action("Index", "Entradas", null, Request.Scheme)!;

        // Paso 1: selección de entradas en la ficha → crea la orden y reserva el aforo
        [HttpPost]
        [ValidateAntiForgeryToken]
        // Nombres explícitos: sin ellos, si el formulario no trae "cantidades[...]" el enlazador intentaría leer
        // todos los campos como cantidades (y fallaría con el token antifalsificación)
        public async Task<IActionResult> Iniciar(int eventoId, [FromForm(Name = "cantidades")] Dictionary<int, int>? cantidades,
            [FromForm(Name = "asientos")] List<int>? asientos, [FromForm(Name = "cupon")] string? cupon,
            [FromForm(Name = "destino")] string? destino, CancellationToken ct)
        {
            // HU26: en teatro se compran los asientos elegidos en el plano; en el resto, zona y cantidad
            var resultado = asientos is { Count: > 0 }
                ? await _compras.CrearOrdenConAsientosAsync(UsuarioId, eventoId, asientos, cupon, ct)
                : await _compras.CrearOrdenAsync(UsuarioId, eventoId, cantidades ?? new(), cupon, ct);

            if (resultado.Error != null)
            {
                TempData["Error"] = resultado.Error;
                return RedirectToAction("Detalle", "Eventos", new { id = eventoId }, "comprar");
            }

            var orden = resultado.Orden!;

            // Eventos gratuitos: no pasan por la pasarela
            if (orden.Total == 0)
            {
                await _compras.ConfirmarPagoAsync(orden.Codigo, "Gratuito", "GRATIS", 0, UrlMisEntradas, ct);
                return RedirectToAction(nameof(Confirmacion), new { codigo = orden.Codigo });
            }

            // "Agregar al carrito": la reserva queda apartada para pagarla desde el carrito
            if (destino == "carrito")
            {
                TempData["Mensaje"] = "Entradas agregadas a tu carrito. Págalas antes de que venza la reserva.";
                return RedirectToAction("Index", "Carrito");
            }

            return RedirectToAction(nameof(Checkout), new { codigo = orden.Codigo });
        }

        // Paso 2: resumen y tiempo de reserva
        public async Task<IActionResult> Checkout(string codigo, CancellationToken ct)
        {
            var orden = await CargarOrdenAsync(codigo, ct);
            if (orden == null)
                return NotFound();

            if (orden.Estado == EstadoOrden.Pagada)
                return RedirectToAction(nameof(Confirmacion), new { codigo });

            ViewBag.PasarelaDisponible = _pasarela.Disponible;
            ViewBag.NombrePasarela = _pasarela.Nombre;
            return View(orden);
        }

        // Paso 3: redirige a la pasarela
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Pagar(string codigo, CancellationToken ct)
        {
            var orden = await CargarOrdenAsync(codigo, ct);
            if (orden == null)
                return NotFound();

            if (orden.Estado != EstadoOrden.PendientePago || orden.FechaExpiracion <= DateTime.UtcNow)
            {
                TempData["Error"] = "El tiempo de reserva terminó. Vuelve a seleccionar tus entradas.";
                return RedirectToAction("Detalle", "Eventos", new { id = orden.EventoId });
            }

            if (!_pasarela.Disponible)
            {
                TempData["Error"] = "Los pagos en línea no están disponibles en este momento.";
                return RedirectToAction(nameof(Checkout), new { codigo });
            }

            var urls = new UrlsRetorno(
                Url.Action(nameof(Retorno), "Compras", null, Request.Scheme)!,
                Url.Action(nameof(Retorno), "Compras", null, Request.Scheme)!,
                Url.Action(nameof(Retorno), "Compras", null, Request.Scheme)!,
                $"{Request.Scheme}://{Request.Host}/api/pagos/mercadopago");

            try
            {
                var email = (await _userManager.GetUserAsync(User))?.Email;
                var inicio = await _pasarela.IniciarAsync(SolicitudCobro.DesdeOrden(orden, orden.Evento!.Titulo), email, urls, ct);
                return Redirect(inicio.UrlRedireccion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al iniciar el pago de la orden {Codigo}", codigo);
                TempData["Error"] = "No pudimos conectar con la pasarela de pago. Inténtalo de nuevo en unos segundos.";
                return RedirectToAction(nameof(Checkout), new { codigo });
            }
        }

        // Paso 3b: pasarela simulada (solo cuando es la pasarela activa)
        public async Task<IActionResult> PasarelaSimulada(string codigo, CancellationToken ct)
        {
            if (_pasarela is not Services.Pagos.PasarelaSimulada)
                return NotFound();

            var orden = await CargarOrdenAsync(codigo, ct);
            if (orden == null || orden.Estado != EstadoOrden.PendientePago)
                return NotFound();

            return View(orden);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PasarelaSimulada(string codigo, bool aprobar, CancellationToken ct)
        {
            if (_pasarela is not Services.Pagos.PasarelaSimulada)
                return NotFound();

            var orden = await CargarOrdenAsync(codigo, ct);
            if (orden == null)
                return NotFound();

            if (!aprobar)
                return await PagoRechazadoAsync(orden, ct);

            var resultado = await _compras.ConfirmarPagoAsync(codigo, Services.Pagos.PasarelaSimulada.NombreProveedor,
                "SIM-" + CodigoSeguro.Generar(6), orden.Total, UrlMisEntradas, ct);

            return DespuesDeConfirmar(resultado, codigo);
        }

        // Paso 4 (Mercado Pago): retorno del navegador. El estado se verifica consultando la API, no con la URL.
        [HttpGet]
        public async Task<IActionResult> Retorno([FromQuery(Name = "payment_id")] string? paymentId,
            [FromQuery(Name = "external_reference")] string? referenciaExterna, [FromQuery] string? status, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(referenciaExterna))
                return RedirectToAction("Index", "Entradas");

            var orden = await CargarOrdenAsync(referenciaExterna, ct);
            if (orden == null)
                return NotFound();

            if (!string.IsNullOrEmpty(paymentId) && paymentId != "null")
            {
                var pago = await _pasarela.ConsultarPagoAsync(paymentId, ct);

                if (pago is { Aprobado: true } && pago.CodigoOrden == orden.Codigo)
                {
                    var resultado = await _compras.ConfirmarPagoAsync(orden.Codigo, _pasarela.Nombre, pago.Referencia, pago.Monto, UrlMisEntradas, ct);
                    return DespuesDeConfirmar(resultado, orden.Codigo);
                }

                if (pago?.Estado is "pending" or "in_process")
                {
                    TempData["Mensaje"] = "Tu pago está en proceso. Te enviaremos tus entradas por correo apenas se confirme.";
                    return RedirectToAction(nameof(Checkout), new { codigo = orden.Codigo });
                }

                // Rechazo confirmado por la API (no por la URL, que se puede manipular)
                if (pago != null && pago.CodigoOrden == orden.Codigo && EsRechazo(pago.Estado))
                    return await PagoRechazadoAsync(orden, ct);
            }

            TempData["Error"] = status == "pending"
                ? "Tu pago está pendiente de confirmación."
                : "El pago no se completó. Puedes intentarlo de nuevo mientras dure tu reserva.";
            return RedirectToAction(nameof(Checkout), new { codigo = orden.Codigo });
        }

        // Notificación servidor a servidor de Mercado Pago (webhook)
        [HttpPost("/api/pagos/mercadopago")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> WebhookMercadoPago(CancellationToken ct)
        {
            var tipo = Request.Query["type"].FirstOrDefault() ?? Request.Query["topic"].FirstOrDefault();
            var dataId = Request.Query["data.id"].FirstOrDefault() ?? Request.Query["id"].FirstOrDefault();

            if (dataId == null && Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
                tipo ??= doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var id))
                    dataId = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
            }

            if (tipo != "payment" || string.IsNullOrEmpty(dataId))
                return Ok();

            var secreto = _pagos.MercadoPago.WebhookSecret;
            if (!string.IsNullOrEmpty(secreto) && !MercadoPagoPasarela.FirmaWebhookValida(secreto,
                    Request.Headers["x-signature"].FirstOrDefault(), Request.Headers["x-request-id"].FirstOrDefault(), dataId))
            {
                _logger.LogWarning("Webhook de Mercado Pago con firma inválida para el pago {Id}", dataId);
                return Unauthorized();
            }

            var pago = await _pasarela.ConsultarPagoAsync(dataId, ct);
            if (pago is { Aprobado: true, CodigoOrden: not null })
            {
                // El prefijo del código indica si se pagó un paquete publicitario o entradas
                if (pago.CodigoOrden.StartsWith(ContratoDestacado.PrefijoCodigo, StringComparison.Ordinal))
                {
                    var destacados = HttpContext.RequestServices.GetRequiredService<Services.Publicidad.IDestacadoService>();
                    await destacados.ConfirmarPagoAsync(pago.CodigoOrden, _pasarela.Nombre, pago.Referencia, pago.Monto, ct);
                }
                else if (pago.CodigoOrden.StartsWith(PagoPublicacion.PrefijoCodigo, StringComparison.Ordinal))
                {
                    var gratuitos = HttpContext.RequestServices.GetRequiredService<Services.Eventos.IEventoGratuitoService>();
                    await gratuitos.ConfirmarPagoAsync(pago.CodigoOrden, _pasarela.Nombre, pago.Referencia, pago.Monto, ct);
                }
                else
                {
                    var urlEntradas = $"{Request.Scheme}://{Request.Host}{Url.Action("Index", "Entradas")}";
                    await _compras.ConfirmarPagoAsync(pago.CodigoOrden, _pasarela.Nombre, pago.Referencia, pago.Monto, urlEntradas, ct);
                }
            }
            else if (pago is { CodigoOrden: not null } && EsRechazo(pago.Estado)
                     && !pago.CodigoOrden.StartsWith(ContratoDestacado.PrefijoCodigo, StringComparison.Ordinal)
                     && !pago.CodigoOrden.StartsWith(PagoPublicacion.PrefijoCodigo, StringComparison.Ordinal))
            {
                await _compras.RechazarPagoAsync(pago.CodigoOrden, ct);
            }

            return Ok();
        }

        private static bool EsRechazo(string estado) => estado is "rejected" or "cancelled";

        // HU06: un pago rechazado no descuenta aforo. Se liberan las entradas y el usuario vuelve a la ficha para elegirlas otra vez
        private async Task<IActionResult> PagoRechazadoAsync(Orden orden, CancellationToken ct)
        {
            await _compras.RechazarPagoAsync(orden.Codigo, ct);
            TempData["Error"] = "El pago fue rechazado y no se te cobró nada. Liberamos las entradas que tenías reservadas; puedes volver a elegirlas e intentarlo con otro medio de pago.";
            return RedirectToAction("Detalle", "Eventos", new { id = orden.EventoId }, "comprar");
        }

        // Paso 5: confirmación con las entradas
        public async Task<IActionResult> Confirmacion(string codigo, CancellationToken ct)
        {
            var orden = await CargarOrdenAsync(codigo, ct, incluirEntradas: true);
            if (orden == null)
                return NotFound();

            if (orden.Estado is EstadoOrden.PendientePago)
                return RedirectToAction(nameof(Checkout), new { codigo });

            return View(orden);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancelar(string codigo, CancellationToken ct)
        {
            var orden = await CargarOrdenAsync(codigo, ct);
            if (orden == null)
                return NotFound();

            if (await _compras.CancelarAsync(codigo, UsuarioId, ct))
                TempData["Mensaje"] = "Cancelaste la compra y liberamos tus entradas reservadas.";

            return RedirectToAction("Detalle", "Eventos", new { id = orden.EventoId });
        }

        private IActionResult DespuesDeConfirmar(ResultadoConfirmacion resultado, string codigo)
        {
            if (resultado == ResultadoConfirmacion.MontoNoCoincide)
            {
                TempData["Error"] = "No pudimos validar el pago. Si se realizó un cobro, contáctanos con tu número de orden.";
                return RedirectToAction(nameof(Checkout), new { codigo });
            }

            return RedirectToAction(nameof(Confirmacion), new { codigo });
        }

        // Solo el dueño de la orden puede verla
        private Task<Orden?> CargarOrdenAsync(string codigo, CancellationToken ct, bool incluirEntradas = false)
        {
            var query = _db.Ordenes
                .AsNoTracking()
                .Include(o => o.Evento).ThenInclude(e => e!.Imagenes.OrderBy(i => i.Orden).Take(1))
                .Include(o => o.Items).ThenInclude(i => i.TipoEntrada)
                .Include(o => o.Items).ThenInclude(i => i.Asiento)
                .Include(o => o.Cupon)
                .AsSplitQuery()
                .Where(o => o.Codigo == codigo && o.UsuarioId == UsuarioId);

            if (incluirEntradas)
                query = query.Include(o => o.Entradas).ThenInclude(e => e.TipoEntrada);

            return query.FirstOrDefaultAsync(ct);
        }
    }
}
