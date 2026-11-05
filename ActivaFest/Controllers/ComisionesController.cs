using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Comisiones;

namespace ActivaFest.Controllers
{
    // =============================
    // 💰 REPORTE DE COMISIONES Y LIQUIDACIONES (HU16 / OE5)
    // =============================
    [Authorize(Roles = Roles.Administrador)]
    public class ComisionesController : Controller
    {
        private readonly IComisionService _comisiones;
        private readonly UserManager<ApplicationUser> _userManager;

        public ComisionesController(IComisionService comisiones, UserManager<ApplicationUser> userManager)
        {
            _comisiones = comisiones;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index(DateTime? desde, DateTime? hasta, CancellationToken ct)
        {
            if (desde > hasta)
                (desde, hasta) = (hasta, desde);

            ViewBag.Liquidaciones = await _comisiones.LiquidacionesAsync(maximo: 15, ct: ct);
            return View(await _comisiones.ReporteAsync(desde, hasta, ct: ct));
        }

        public async Task<IActionResult> Exportar(DateTime? desde, DateTime? hasta, CancellationToken ct)
        {
            var reporte = await _comisiones.ReporteAsync(desde, hasta, ct: ct);
            var nombre = $"comisiones-activafest-{DateTime.Now:yyyyMMdd-HHmm}.csv";
            return File(_comisiones.ExportarCsv(reporte), "text/csv; charset=utf-8", nombre);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Liquidar(int eventoId, decimal monto, string referencia, string? nota,
            DateTime? desde, DateTime? hasta, CancellationToken ct)
        {
            var resultado = await _comisiones.RegistrarLiquidacionAsync(eventoId, monto, referencia, nota, _userManager.GetUserId(User)!, ct);
            TempData[resultado.Exito ? "Mensaje" : "Error"] = resultado.Mensaje;
            return RedirectToAction(nameof(Index), new { desde = desde?.ToString("yyyy-MM-dd"), hasta = hasta?.ToString("yyyy-MM-dd") });
        }
    }
}
