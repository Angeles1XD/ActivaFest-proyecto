using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Acceso;
using ActivaFest.Services.Analitica;
using ActivaFest.Services.Organizadores;

namespace ActivaFest.Controllers
{
    // =============================
    // 📥 SOLICITUDES AL ADMINISTRADOR (HU31 organizadores, HU32 personal de control)
    // =============================
    [Authorize(Roles = Roles.Administrador)]
    public class SolicitudesController : Controller
    {
        private readonly ISolicitudOrganizadorService _organizadores;
        private readonly ISolicitudPersonalService _personal;

        public SolicitudesController(ISolicitudOrganizadorService organizadores, ISolicitudPersonalService personal)
        {
            _organizadores = organizadores;
            _personal = personal;
        }

        private string AdministradorId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        public async Task<IActionResult> Index(EstadoSolicitud estado = EstadoSolicitud.Pendiente, CancellationToken ct = default)
        {
            ViewBag.Estado = estado;
            ViewBag.PendientesOrganizador = await _organizadores.PendientesAsync(ct);
            ViewBag.PendientesPersonal = await _personal.PendientesAsync(ct);
            var personal = await _personal.ListarAsync(estado, ct);
            ViewBag.Personal = personal;
            ViewBag.Cuentas = await _personal.CuentasAsync(personal.SelectMany(s => s.ListaCorreos), ct);
            return View(await _organizadores.ListarAsync(estado, ct));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AprobarOrganizador(int id, PlazoOrganizador plazo, CancellationToken ct)
        {
            var r = await _organizadores.AprobarAsync(id, AdministradorId, plazo, Url.Action("Crear", "Eventos", null, Request.Scheme)!, ct);
            TempData[r.Exito ? "Mensaje" : "Error"] = r.Exito
                ? $"Cuenta habilitada para crear eventos {(plazo == PlazoOrganizador.Indefinido ? "sin vencimiento" : $"por {(int)plazo} días")}."
                : r.Error;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RechazarOrganizador(int id, string motivo, CancellationToken ct)
        {
            var r = await _organizadores.RechazarAsync(id, AdministradorId, motivo, Url.Action("Solicitar", "Organizador", null, Request.Scheme)!, ct);
            TempData[r.Exito ? "Mensaje" : "Error"] = r.Exito ? "Solicitud rechazada; se avisó por correo con el motivo." : r.Error;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AprobarPersonal(int id, List<string>? correos, bool conSeleccion = false, CancellationToken ct = default)
        {
            // conSeleccion: el formulario trae las casillas de cada cuenta (si no marcó ninguna, la lista llega vacía)
            var elegidos = conSeleccion ? (IReadOnlyCollection<string>)(correos ?? new List<string>()) : null;
            var r = await _personal.AprobarAsync(id, AdministradorId, Url.Action("Index", "Acceso", null, Request.Scheme)!, elegidos, ct);
            TempData[r.Exito ? "Mensaje" : "Error"] = r.Exito ? "Personal asignado: ya puede validar las entradas de ese evento." : r.Error;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RechazarPersonal(int id, string motivo, CancellationToken ct)
        {
            var r = await _personal.RechazarAsync(id, AdministradorId, motivo, Url.Action("Index", "Personal", null, Request.Scheme)!, ct);
            TempData[r.Exito ? "Mensaje" : "Error"] = r.Exito ? "Solicitud rechazada; se avisó al organizador." : r.Error;
            return RedirectToAction(nameof(Index));
        }

        // =============================
        // ⭐ REPUTACIÓN DE UNA CUENTA DEL PERSONAL (HU33): para decidir si se aprueba
        // =============================
        // Es información individual: solo el administrador la ve y cada consulta queda en AUDITORIA_ANALITICA (Ley N.° 29733)
        public async Task<IActionResult> Reputacion(string id, [FromServices] IReputacionPersonalService reputacion,
            [FromServices] IAnaliticaService analitica, CancellationToken ct)
        {
            var detalle = await reputacion.DetalleAsync(id, ct);
            if (detalle == null)
                return NotFound();

            await analitica.RegistrarAuditoriaAsync(AdministradorId, $"Reputación del personal {detalle.Usuario.Email}", "CalificacionesPersonal, AsignacionesAcceso", ct);
            return View(detalle);
        }
    }
}
