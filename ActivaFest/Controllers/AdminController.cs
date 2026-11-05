using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Email;

namespace ActivaFest.Controllers
{
    // =============================
    // 🛡️ PANEL DE MODERACIÓN (HU10)
    // =============================
    [Authorize(Roles = Roles.Administrador)]
    public class AdminController : Controller
    {
        public const int LargoMaximoMotivo = 500;

        private readonly ApplicationDbContext _context;
        private readonly ICatalogoService _catalogo;
        private readonly IEmailService _email;

        public AdminController(ApplicationDbContext context, ICatalogoService catalogo, IEmailService email)
        {
            _context = context;
            _catalogo = catalogo;
            _email = email;
        }

        public async Task<IActionResult> Index(EstadoEvento estado = EstadoEvento.Pendiente)
        {
            var eventos = await _context.Eventos
                .Include(e => e.Organizador)
                .Include(e => e.Categoria)
                .Where(e => e.Estado == estado)
                .OrderBy(e => e.FechaCreacion)
                .ToListAsync();

            ViewBag.EstadoActual = estado;
            ViewBag.Conteos = await _context.Eventos
                .GroupBy(e => e.Estado)
                .ToDictionaryAsync(g => g.Key, g => g.Count());

            return View(eventos);
        }

        // Diagrama de estados: solo un evento Pendiente se modera. Uno Aprobado pasa a Cancelado o Finalizado
        // (nunca a Rechazado: podría tener entradas vendidas) y uno Rechazado vuelve a Pendiente al corregirlo.
        private IActionResult NoModerable(Evento evento)
        {
            TempData["Error"] = $"\"{evento.Titulo}\" ya fue moderado (estado: {evento.Estado}). Solo se aprueban o rechazan eventos pendientes.";
            return RedirectToAction(nameof(Index), new { estado = evento.Estado });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Aprobar(int id)
        {
            var evento = await _context.Eventos.Include(e => e.Organizador).FirstOrDefaultAsync(e => e.Id == id);
            if (evento == null)
                return NotFound();

            if (evento.Estado != EstadoEvento.Pendiente)
                return NoModerable(evento);

            // HU34: un evento gratuito se modera después de pagar su publicación
            if (evento.PendientePagoPublicacion)
            {
                TempData["Error"] = $"\"{evento.Titulo}\" es un evento gratuito que aún no pagó su publicación.";
                return RedirectToAction(nameof(Index));
            }

            // Publicar un evento cuya fecha ya pasó no tiene sentido: se debe rechazar o pedir otra fecha
            if (evento.YaOcurrio(DateTime.Now))
            {
                TempData["Error"] = $"No se puede aprobar \"{evento.Titulo}\": su fecha ya pasó.";
                return RedirectToAction(nameof(Index), new { estado = evento.Estado });
            }

            // HU03 / OE1: todo evento publicado muestra al menos 3 fotos del recinto
            var fotos = await _context.EventoImagenes.CountAsync(i => i.EventoId == evento.Id);
            if (fotos < EventoFormViewModel.MinFotos)
            {
                TempData["Error"] = $"No se puede aprobar \"{evento.Titulo}\": tiene {fotos} fotos y se requieren al menos {EventoFormViewModel.MinFotos}. Recházalo pidiendo más fotos del recinto.";
                return RedirectToAction(nameof(Index));
            }

            evento.Estado = EstadoEvento.Aprobado;
            evento.MotivoRechazo = null;
            evento.FechaModeracion = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _catalogo.InvalidarAsync();

            if (evento.Organizador?.Email != null)
            {
                var url = Url.Action("Detalle", "Eventos", new { id = evento.Id }, Request.Scheme)!;
                await _email.EnviarAsync(evento.Organizador.Email, $"Evento aprobado: {evento.Titulo}",
                    EmailTemplates.EventoAprobado(evento, url));
            }

            TempData["Mensaje"] = $"Evento \"{evento.Titulo}\" aprobado y publicado.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rechazar(int id, string? motivo)
        {
            if (string.IsNullOrWhiteSpace(motivo))
            {
                TempData["Error"] = "Debes indicar el motivo del rechazo.";
                return RedirectToAction(nameof(Index));
            }

            if (motivo.Trim().Length > LargoMaximoMotivo)
            {
                TempData["Error"] = $"El motivo no puede superar los {LargoMaximoMotivo} caracteres.";
                return RedirectToAction(nameof(Index));
            }

            var evento = await _context.Eventos.Include(e => e.Organizador).FirstOrDefaultAsync(e => e.Id == id);
            if (evento == null)
                return NotFound();

            if (evento.Estado != EstadoEvento.Pendiente)
                return NoModerable(evento);

            evento.Estado = EstadoEvento.Rechazado;
            evento.MotivoRechazo = motivo.Trim();
            evento.FechaModeracion = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (evento.Organizador?.Email != null)
            {
                var url = Url.Action("MisEventos", "Eventos", null, Request.Scheme)!;
                await _email.EnviarAsync(evento.Organizador.Email, $"Evento no aprobado: {evento.Titulo}",
                    EmailTemplates.EventoRechazado(evento, url));
            }

            TempData["Mensaje"] = $"Evento \"{evento.Titulo}\" rechazado.";
            return RedirectToAction(nameof(Index));
        }
    }
}
