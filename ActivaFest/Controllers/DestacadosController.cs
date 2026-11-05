using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Entradas;
using ActivaFest.Services.Pagos;
using ActivaFest.Services.Publicidad;

namespace ActivaFest.Controllers
{
    public class ContratarDestacadoViewModel
    {
        public Evento Evento { get; set; } = new();
        public List<PaqueteDestacado> Paquetes { get; set; } = new();
        public List<ContratoDestacado> Historial { get; set; } = new();
        public int DiasDisponibles { get; set; }
        public ContratoDestacado? Vigente { get; set; }
        public bool PasarelaDisponible { get; set; }
    }

    // =============================
    // ⭐ PAQUETES PUBLICITARIOS PARA ORGANIZADORES (HU12)
    // =============================
    [Authorize(Roles = Roles.Organizador + "," + Roles.Administrador)]
    public class DestacadosController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly IDestacadoService _destacados;
        private readonly IPasarelaPago _pasarela;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<DestacadosController> _logger;

        public DestacadosController(ApplicationDbContext db, IDestacadoService destacados, IPasarelaPago pasarela,
            UserManager<ApplicationUser> userManager, ILogger<DestacadosController> logger)
        {
            _db = db;
            _destacados = destacados;
            _pasarela = pasarela;
            _userManager = userManager;
            _logger = logger;
        }

        private string UsuarioId => _userManager.GetUserId(User)!;

        private bool EsAdmin => User.IsInRole(Roles.Administrador);

        public async Task<IActionResult> Contratar(int id, CancellationToken ct)
        {
            var evento = await _db.Eventos.AsNoTracking()
                .Include(e => e.Imagenes.OrderBy(i => i.Orden).Take(1))
                .FirstOrDefaultAsync(e => e.Id == id && (EsAdmin || e.OrganizadorId == UsuarioId), ct);
            if (evento == null)
                return NotFound();

            var historial = await _destacados.HistorialAsync(id, ct);
            return View(new ContratarDestacadoViewModel
            {
                Evento = evento,
                Paquetes = await _destacados.PaquetesDisponiblesAsync(ct),
                Historial = historial,
                DiasDisponibles = _destacados.DiasDisponibles(evento, DateTime.Now),
                Vigente = historial.Where(c => c.VigenteEn(DateTime.Now)).OrderByDescending(c => c.FechaFin).FirstOrDefault(),
                PasarelaDisponible = _pasarela.Disponible
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Contratar(int id, int paqueteId, CancellationToken ct)
        {
            var resultado = await _destacados.ContratarAsync(UsuarioId, EsAdmin, id, paqueteId, ct);
            if (resultado.Error != null)
            {
                TempData["Error"] = resultado.Error;
                return RedirectToAction(nameof(Contratar), new { id });
            }

            if (!_pasarela.Disponible)
            {
                TempData["Error"] = "Los pagos en línea no están disponibles en este momento.";
                return RedirectToAction(nameof(Contratar), new { id });
            }

            var contrato = resultado.Contrato!;
            var evento = await _db.Eventos.AsNoTracking().FirstAsync(e => e.Id == id, ct);
            var paquete = await _db.PaquetesDestacado.AsNoTracking().FirstAsync(p => p.Id == paqueteId, ct);

            var retorno = Url.Action(nameof(Retorno), "Destacados", null, Request.Scheme)!;
            var urls = new UrlsRetorno(retorno, retorno, retorno, $"{Request.Scheme}://{Request.Host}/api/pagos/mercadopago");

            try
            {
                var email = (await _userManager.GetUserAsync(User))?.Email;
                var inicio = await _pasarela.IniciarAsync(SolicitudCobro.DesdeContrato(contrato, evento.Titulo, paquete.Nombre), email, urls, ct);
                return Redirect(inicio.UrlRedireccion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al iniciar el pago del destacado {Codigo}", contrato.Codigo);
                TempData["Error"] = "No pudimos conectar con la pasarela de pago. Inténtalo de nuevo.";
                return RedirectToAction(nameof(Contratar), new { id });
            }
        }

        // Pasarela simulada (solo cuando es la pasarela activa)
        public async Task<IActionResult> PasarelaSimulada(string codigo, CancellationToken ct)
        {
            if (_pasarela is not Services.Pagos.PasarelaSimulada)
                return NotFound();

            var contrato = await CargarContratoAsync(codigo, ct);
            return contrato == null || contrato.Estado != EstadoContrato.PendientePago ? NotFound() : View(contrato);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PasarelaSimulada(string codigo, bool aprobar, CancellationToken ct)
        {
            if (_pasarela is not Services.Pagos.PasarelaSimulada)
                return NotFound();

            var contrato = await CargarContratoAsync(codigo, ct);
            if (contrato == null)
                return NotFound();

            if (!aprobar)
            {
                TempData["Error"] = "El pago fue rechazado. Puedes volver a intentarlo.";
                return RedirectToAction(nameof(Contratar), new { id = contrato.EventoId });
            }

            await _destacados.ConfirmarPagoAsync(codigo, Services.Pagos.PasarelaSimulada.NombreProveedor, "SIM-" + CodigoSeguro.Generar(6), contrato.Precio, ct);
            return RedirectToAction(nameof(Confirmacion), new { codigo });
        }

        // Retorno de Mercado Pago: el estado se verifica consultando la API
        public async Task<IActionResult> Retorno([FromQuery(Name = "payment_id")] string? paymentId,
            [FromQuery(Name = "external_reference")] string? referenciaExterna, CancellationToken ct)
        {
            var contrato = referenciaExterna == null ? null : await CargarContratoAsync(referenciaExterna, ct);
            if (contrato == null)
                return RedirectToAction("MisEventos", "Eventos");

            if (!string.IsNullOrEmpty(paymentId) && paymentId != "null")
            {
                var pago = await _pasarela.ConsultarPagoAsync(paymentId, ct);
                if (pago is { Aprobado: true } && pago.CodigoOrden == contrato.Codigo)
                {
                    await _destacados.ConfirmarPagoAsync(contrato.Codigo, _pasarela.Nombre, pago.Referencia, pago.Monto, ct);
                    return RedirectToAction(nameof(Confirmacion), new { codigo = contrato.Codigo });
                }
            }

            TempData["Error"] = "El pago no se completó.";
            return RedirectToAction(nameof(Contratar), new { id = contrato.EventoId });
        }

        public async Task<IActionResult> Confirmacion(string codigo, CancellationToken ct)
        {
            var contrato = await CargarContratoAsync(codigo, ct);
            if (contrato == null)
                return NotFound();

            return contrato.Estado == EstadoContrato.Activo ? View(contrato) : RedirectToAction(nameof(Contratar), new { id = contrato.EventoId });
        }

        // Solo el organizador del contrato (o un administrador) lo ve
        private Task<ContratoDestacado?> CargarContratoAsync(string codigo, CancellationToken ct) =>
            _db.ContratosDestacado.AsNoTracking()
                .Include(c => c.Evento)
                .Include(c => c.Paquete)
                .FirstOrDefaultAsync(c => c.Codigo == codigo && (EsAdmin || c.OrganizadorId == UsuarioId), ct);
    }
}
