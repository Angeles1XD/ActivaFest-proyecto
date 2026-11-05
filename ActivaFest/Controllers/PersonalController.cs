using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Organizadores;

namespace ActivaFest.Controllers
{
    // =============================
    // 🚪 EL ORGANIZADOR PIDE PERSONAL DE CONTROL PARA SU EVENTO (HU32)
    // =============================
    [Authorize(Roles = Roles.Organizador + "," + Roles.Administrador)]
    public class PersonalController : Controller
    {
        private readonly ISolicitudPersonalService _personal;

        public PersonalController(ISolicitudPersonalService personal)
        {
            _personal = personal;
        }

        private string UsuarioId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        private bool EsAdmin => User.IsInRole(Roles.Administrador);

        public async Task<IActionResult> Index(int? eventoId, CancellationToken ct)
        {
            ViewBag.Eventos = await _personal.EventosAsync(UsuarioId, EsAdmin, ct);
            var mias = await _personal.MiasAsync(UsuarioId, ct);
            ViewBag.Mias = mias;
            // Por cada correo pedido, solo si ya está habilitado en ese evento (sin nombres ni otros datos de la cuenta)
            ViewBag.Habilitados = await _personal.HabilitadosAsync(mias, ct);
            ViewBag.EventoId = eventoId;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Solicitar(int eventoId, string correos, string? mensaje, CancellationToken ct)
        {
            var r = await _personal.CrearAsync(UsuarioId, EsAdmin, eventoId, correos, mensaje, Url.Action("Index", "Solicitudes", null, Request.Scheme)!, ct);
            if (r.Exito)
                TempData["Mensaje"] = "Solicitud enviada al administrador. Te avisaremos por correo cuando la apruebe.";
            else
            {
                TempData["Error"] = r.Error;
                TempData["Correos"] = correos;
                TempData["MensajePersonal"] = mensaje;
            }
            return RedirectToAction(nameof(Index), new { eventoId });
        }
    }
}
