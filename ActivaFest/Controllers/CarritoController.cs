using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Compras;

namespace ActivaFest.Controllers
{
    // =============================
    // 🛒 CARRITO: entradas reservadas que faltan pagar
    // =============================
    [Authorize]
    public class CarritoController : Controller
    {
        private readonly ICarritoService _carrito;
        private readonly ICompraService _compras;
        private readonly UserManager<ApplicationUser> _userManager;

        public CarritoController(ICarritoService carrito, ICompraService compras, UserManager<ApplicationUser> userManager)
        {
            _carrito = carrito;
            _compras = compras;
            _userManager = userManager;
        }

        private string UsuarioId => _userManager.GetUserId(User)!;

        public async Task<IActionResult> Index(CancellationToken ct) =>
            View(await _carrito.ObtenerAsync(UsuarioId, ct));

        // Libera las entradas de la reserva; solo el dueño de la orden puede cancelarla
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancelar(string codigo, CancellationToken ct)
        {
            if (await _compras.CancelarAsync(codigo, UsuarioId, ct))
                TempData["Mensaje"] = "Reserva cancelada: las entradas volvieron a estar disponibles.";
            else
                TempData["Error"] = "No se pudo cancelar esa reserva: ya no está pendiente de pago.";
            return RedirectToAction(nameof(Index));
        }
    }
}
