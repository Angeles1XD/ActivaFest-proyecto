using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Entradas;
using ActivaFest.Services.Eventos;
using ActivaFest.Services.Pagos;

namespace ActivaFest.Controllers
{
    // =============================
    // 🎉 PAGO DE LA PUBLICACIÓN DE UN EVENTO GRATUITO (HU34)
    // =============================
    // Mismo recorrido que los paquetes destacados: pasarela (Mercado Pago o simulada) y confirmación por retorno o webhook.
    [Authorize(Roles = Roles.Organizador + "," + Roles.Administrador)]
    public class PublicacionController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly IEventoGratuitoService _gratuitos;
        private readonly IPasarelaPago _pasarela;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<PublicacionController> _logger;

        public PublicacionController(ApplicationDbContext db, IEventoGratuitoService gratuitos, IPasarelaPago pasarela,
            UserManager<ApplicationUser> userManager, ILogger<PublicacionController> logger)
        {
            _db = db;
            _gratuitos = gratuitos;
            _pasarela = pasarela;
            _userManager = userManager;
            _logger = logger;
        }

        private string UsuarioId => _userManager.GetUserId(User)!;
        private bool EsAdmin => User.IsInRole(Roles.Administrador);

        public async Task<IActionResult> Pagar(int id, CancellationToken ct)
        {
            var evento = await _db.Eventos.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id && e.EsGratuito && (EsAdmin || e.OrganizadorId == UsuarioId), ct);
            if (evento == null)
                return NotFound();
            if (!evento.PendientePagoPublicacion)
            {
                TempData["Mensaje"] = "La publicación de este evento ya está pagada.";
                return RedirectToAction("MisEventos", "Eventos");
            }

            ViewBag.Tarifa = _gratuitos.TarifaPara(evento.Aforo);
            ViewBag.Tramos = _gratuitos.Tramos;
            ViewBag.PasarelaDisponible = _pasarela.Disponible;
            return View(evento);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [ActionName(nameof(Pagar))]
        public async Task<IActionResult> IniciarPago(int id, CancellationToken ct)
        {
            var r = await _gratuitos.CrearCobroAsync(UsuarioId, EsAdmin, id, ct);
            if (r.Error != null)
            {
                TempData["Error"] = r.Error;
                return RedirectToAction("MisEventos", "Eventos");
            }
            if (!_pasarela.Disponible)
            {
                TempData["Error"] = "Los pagos en línea no están disponibles en este momento.";
                return RedirectToAction(nameof(Pagar), new { id });
            }

            var pago = r.Pago!;
            var evento = await _db.Eventos.AsNoTracking().FirstAsync(e => e.Id == id, ct);
            var retorno = Url.Action(nameof(Retorno), "Publicacion", null, Request.Scheme)!;
            var urls = new UrlsRetorno(retorno, retorno, retorno, $"{Request.Scheme}://{Request.Host}/api/pagos/mercadopago");
            try
            {
                var email = (await _userManager.GetUserAsync(User))?.Email;
                var inicio = await _pasarela.IniciarAsync(SolicitudCobro.DesdePublicacion(pago, evento.Titulo), email, urls, ct);
                return Redirect(inicio.UrlRedireccion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al iniciar el pago de la publicación {Codigo}", pago.Codigo);
                TempData["Error"] = "No pudimos conectar con la pasarela de pago. Inténtalo de nuevo.";
                return RedirectToAction(nameof(Pagar), new { id });
            }
        }

        // Pasarela simulada (solo cuando es la pasarela activa)
        public async Task<IActionResult> PasarelaSimulada(string codigo, CancellationToken ct)
        {
            if (_pasarela is not Services.Pagos.PasarelaSimulada)
                return NotFound();
            var pago = await CargarPagoAsync(codigo, ct);
            return pago == null || pago.Estado != EstadoPagoPublicacion.PendientePago ? NotFound() : View(pago);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PasarelaSimulada(string codigo, bool aprobar, CancellationToken ct)
        {
            if (_pasarela is not Services.Pagos.PasarelaSimulada)
                return NotFound();
            var pago = await CargarPagoAsync(codigo, ct);
            if (pago == null)
                return NotFound();

            if (!aprobar)
            {
                TempData["Error"] = "El pago fue rechazado. Puedes volver a intentarlo.";
                return RedirectToAction(nameof(Pagar), new { id = pago.EventoId });
            }

            await _gratuitos.ConfirmarPagoAsync(codigo, Services.Pagos.PasarelaSimulada.NombreProveedor, "SIM-" + CodigoSeguro.Generar(6), pago.Monto, ct);
            return Pagado();
        }

        // Retorno de Mercado Pago: el estado se verifica consultando la API
        public async Task<IActionResult> Retorno([FromQuery(Name = "payment_id")] string? paymentId,
            [FromQuery(Name = "external_reference")] string? referenciaExterna, CancellationToken ct)
        {
            var pago = referenciaExterna == null ? null : await CargarPagoAsync(referenciaExterna, ct);
            if (pago == null)
                return RedirectToAction("MisEventos", "Eventos");

            if (!string.IsNullOrEmpty(paymentId) && paymentId != "null")
            {
                var externo = await _pasarela.ConsultarPagoAsync(paymentId, ct);
                if (externo is { Aprobado: true } && externo.CodigoOrden == pago.Codigo
                    && await _gratuitos.ConfirmarPagoAsync(pago.Codigo, _pasarela.Nombre, externo.Referencia, externo.Monto, ct))
                    return Pagado();
            }

            TempData["Error"] = "El pago no se completó.";
            return RedirectToAction(nameof(Pagar), new { id = pago.EventoId });
        }

        private IActionResult Pagado()
        {
            TempData["Mensaje"] = "¡Pago recibido! Tu evento gratuito ya está en revisión del administrador.";
            return RedirectToAction("MisEventos", "Eventos");
        }

        // Solo el organizador del evento (o un administrador) ve el cobro
        private Task<PagoPublicacion?> CargarPagoAsync(string codigo, CancellationToken ct) =>
            _db.PagosPublicacion.AsNoTracking().Include(p => p.Evento)
                .FirstOrDefaultAsync(p => p.Codigo == codigo && (EsAdmin || p.OrganizadorId == UsuarioId), ct);
    }
}
