using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Organizadores;

namespace ActivaFest.Controllers
{
    // =============================
    // 📝 SOLICITAR SER ORGANIZADOR (HU31)
    // =============================
    // Los requisitos se ven sin iniciar sesión; para enviar la solicitud hace falta una cuenta.
    public class OrganizadorController : Controller
    {
        private readonly ISolicitudOrganizadorService _solicitudes;

        public OrganizadorController(ISolicitudOrganizadorService solicitudes)
        {
            _solicitudes = solicitudes;
        }

        private string? UsuarioId => User.FindFirstValue(ClaimTypes.NameIdentifier);

        public async Task<IActionResult> Solicitar(CancellationToken ct)
        {
            ViewBag.Ultima = UsuarioId == null ? null : await _solicitudes.UltimaAsync(UsuarioId, ct);
            await CargarAlcanceGratuitosAsync(ct);
            return View(new SolicitudOrganizadorInput());
        }

        // HU34: cifras de alcance de los eventos gratuitos para animar a más organizadores a publicar
        private async Task CargarAlcanceGratuitosAsync(CancellationToken ct)
        {
            var gratuitos = HttpContext.RequestServices.GetRequiredService<Services.Eventos.IEventoGratuitoService>();
            ViewBag.Gratuitos = await gratuitos.ResumenAsync(ct: ct);
            ViewBag.Tramos = gratuitos.Tramos;
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Solicitar(SolicitudOrganizadorInput datos, CancellationToken ct)
        {
            await CargarAlcanceGratuitosAsync(ct);
            if (!ModelState.IsValid)
            {
                ViewBag.Ultima = await _solicitudes.UltimaAsync(UsuarioId!, ct);
                return View(datos);
            }

            var urlAdmin = Url.Action("Index", "Solicitudes", null, Request.Scheme)!;
            var r = await _solicitudes.CrearAsync(UsuarioId!, datos, urlAdmin, ct);
            if (!r.Exito)
            {
                ModelState.AddModelError(string.Empty, r.Error!);
                ViewBag.Ultima = await _solicitudes.UltimaAsync(UsuarioId!, ct);
                return View(datos);
            }

            TempData["Mensaje"] = "¡Solicitud enviada! El equipo la revisará y te avisará por correo.";
            return RedirectToAction(nameof(Solicitar));
        }
    }
}
