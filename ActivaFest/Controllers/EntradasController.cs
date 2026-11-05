using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Acceso;
using ActivaFest.Services.Entradas;

namespace ActivaFest.Controllers
{
    // =============================
    // 🎟️ MIS ENTRADAS (HU07)
    // =============================
    [Authorize]
    public class EntradasController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly IQrEntradaService _qr;
        private readonly UserManager<ApplicationUser> _userManager;

        public EntradasController(ApplicationDbContext db, IQrEntradaService qr, UserManager<ApplicationUser> userManager)
        {
            _db = db;
            _qr = qr;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index([FromServices] IReputacionPersonalService reputacion, CancellationToken ct)
        {
            var userId = _userManager.GetUserId(User);

            var entradas = await _db.Entradas
                .AsNoTracking()
                .Include(e => e.Evento)
                .Include(e => e.TipoEntrada)
                .Include(e => e.Orden)
                .Include(e => e.Asiento) // HU27: fila y asiento
                .Where(e => e.UsuarioId == userId)
                .ToListAsync(ct);

            // Próximos eventos primero (por fecha), luego los pasados (más recientes primero)
            var hoy = DateTime.Today;
            var grupos = entradas
                .GroupBy(e => e.EventoId)
                .Select(g => g.OrderBy(e => e.TipoEntrada!.Nombre).ThenBy(e => e.Id).ToList())
                .OrderBy(g => g[0].Evento!.Fecha < hoy)
                .ThenBy(g => g[0].Evento!.Fecha < hoy ? -g[0].Evento!.Fecha.Ticks : g[0].Evento!.Fecha.Ticks)
                .ToList();

            // HU33: calificaciones que ya dio a la atención en la puerta

            ViewData["Calificaciones"] = await reputacion.CalificacionesDeAsync(userId!, entradas.Where(e => e.Estado == EstadoEntrada.Usada).Select(e => e.Id), ct);


            return View(grupos);
        }

        // HU33: el asistente califica el trato de quien escaneó su entrada (1 a 5 estrellas)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Calificar(int id, int puntaje, string? comentario, [FromServices] IReputacionPersonalService reputacion, CancellationToken ct)
        {
            var r = await reputacion.CalificarAsync(_userManager.GetUserId(User)!, id, puntaje, comentario, ct);
            TempData[r.Exito ? "Mensaje" : "Error"] = r.Exito ? "¡Gracias! Tu calificación ayuda a cuidar la atención en las puertas." : r.Error;
            return RedirectToAction(nameof(Index));
        }

        // Imagen del QR: solo para el dueño de la entrada. El token cifrado incluye zona, fila y asiento (HU27)
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
        public async Task<IActionResult> Qr(int id, CancellationToken ct)
        {
            var entrada = await EntradaPropiaAsync(id, ct);
            if (entrada == null)
                return NotFound();

            return File(_qr.GenerarPng(entrada.Codigo, UbicacionEntrada.De(entrada).ParaQr), "image/png", $"entrada-{id}.png");
        }

        // HU27: plano del recinto con el asiento (o la zona) de esta entrada resaltado
        public async Task<IActionResult> Plano(int id, CancellationToken ct)
        {
            var entrada = await EntradaPropiaAsync(id, ct);
            if (entrada == null)
                return NotFound();

            var evento = await _db.Eventos.AsNoTracking().Include(e => e.TiposEntrada).FirstAsync(e => e.Id == entrada.EventoId, ct);
            var asientos = evento.PlantillaPlano == PlantillaPlano.Teatro
                ? await _db.Asientos.AsNoTracking().Where(a => a.TipoEntrada!.EventoId == evento.Id).ToListAsync(ct)
                : new List<Asiento>();

            var svg = Services.Plano.PlanoService.SvgDeEvento(evento, asientos,
                asientoResaltado: entrada.AsientoId, zonaResaltada: entrada.AsientoId == null ? entrada.TipoEntradaId : null);
            return Content(svg, "image/svg+xml; charset=utf-8");
        }

        private Task<Entrada?> EntradaPropiaAsync(int id, CancellationToken ct)
        {
            var userId = _userManager.GetUserId(User);
            return _db.Entradas.AsNoTracking()
                .Include(e => e.TipoEntrada)
                .Include(e => e.Asiento)
                .FirstOrDefaultAsync(e => e.Id == id && e.UsuarioId == userId, ct);
        }
    }
}
