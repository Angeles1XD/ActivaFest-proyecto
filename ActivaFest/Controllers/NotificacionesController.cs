using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Notificaciones;

namespace ActivaFest.Controllers
{
    // =============================
    // 🔔 PANEL DE NOTIFICACIONES (campana de la barra superior y página completa)
    // =============================
    // Aquí llegan los recordatorios y alertas que el usuario le pidió al asistente virtual según su rol.
    [Authorize]
    public class NotificacionesController : Controller
    {
        private readonly INotificacionService _notificaciones;
        private readonly UserManager<ApplicationUser> _userManager;

        public NotificacionesController(INotificacionService notificaciones, UserManager<ApplicationUser> userManager)
        {
            _notificaciones = notificaciones;
            _userManager = userManager;
        }

        private string UsuarioId => _userManager.GetUserId(User)!;

        // Página completa: al abrirla quedan todas como leídas
        public async Task<IActionResult> Index(CancellationToken ct)
        {
            var panel = await _notificaciones.PanelAsync(UsuarioId, ct);
            await _notificaciones.MarcarLeidasAsync(UsuarioId, ct);
            return View(panel);
        }

        [HttpGet("/api/notificaciones")]
        public async Task<IActionResult> Listar(CancellationToken ct)
        {
            var panel = await _notificaciones.PanelAsync(UsuarioId, ct);
            return Ok(new
            {
                noLeidas = panel.NoLeidas,
                notificaciones = panel.Notificaciones.Take(10).Select(n => new
                {
                    n.Id, n.Icono, n.Titulo, n.Mensaje, n.Url, n.Leida,
                    fecha = n.Fecha.ToLocalTime().ToString("dd/MM HH:mm")
                }),
                programadas = panel.Programadas.Select(p => new
                {
                    p.Icono, p.Texto,
                    cuando = p.Cuando?.ToString("dd/MM HH:mm")
                })
            });
        }

        [HttpGet("/api/notificaciones/contador")]
        public async Task<IActionResult> Contador(CancellationToken ct) =>
            Ok(new { noLeidas = await _notificaciones.NoLeidasAsync(UsuarioId, ct) });

        [HttpPost("/api/notificaciones/leidas")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> MarcarLeidas(CancellationToken ct)
        {
            await _notificaciones.MarcarLeidasAsync(UsuarioId, ct);
            return NoContent();
        }
    }
}
