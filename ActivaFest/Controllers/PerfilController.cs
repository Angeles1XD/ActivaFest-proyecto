using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Recomendaciones;

namespace ActivaFest.Controllers
{
    // =============================
    // 👤 MI PERFIL + 🎯 EVENTOS RECOMENDADOS PARA TI (HU17)
    // =============================
    [Authorize]
    public class PerfilController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IRecomendacionService _recomendaciones;

        public PerfilController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IRecomendacionService recomendaciones)
        {
            _context = context;
            _userManager = userManager;
            _recomendaciones = recomendaciones;
        }

        // lat/lng llegan solo si el usuario pulsa "Usar mi ubicación"; no se guardan
        public async Task<IActionResult> Index(double? lat, double? lng, CancellationToken ct)
        {
            var usuario = await _userManager.GetUserAsync(User);
            if (usuario == null)
                return Challenge();

            Ubicacion? ubicacion = lat is >= -90 and <= 90 && lng is >= -180 and <= 180 ? new Ubicacion(lat.Value, lng.Value) : null;

            var modelo = new PerfilViewModel
            {
                Nombre = usuario.NombreCompleto,
                Correo = usuario.Email ?? "",
                Roles = (await _userManager.GetRolesAsync(usuario)).Select(Roles.NombreVisible).ToList(),
                MiembroDesde = usuario.FechaRegistro,
                EntradasCompradas = await _context.Entradas.CountAsync(e => e.Orden!.UsuarioId == usuario.Id, ct),
                ActividadesRegistradas = await _recomendaciones.ContarHistorialAsync(usuario.Id, ct),
                UsaUbicacion = ubicacion != null,
                Recomendados = await _recomendaciones.RecomendarAsync(usuario.Id, ubicacion, RecomendacionService.MaximoRecomendaciones, ct)
            };

            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BorrarHistorial(CancellationToken ct)
        {
            var borradas = await _recomendaciones.BorrarHistorialAsync(_userManager.GetUserId(User)!, ct);
            TempData["Mensaje"] = borradas > 0
                ? "Listo: borramos tu historial de búsquedas y eventos vistos."
                : "No tenías historial guardado.";
            return RedirectToAction(nameof(Index));
        }
    }
}
