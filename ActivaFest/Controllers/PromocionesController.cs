using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Promociones;

namespace ActivaFest.Controllers
{
    // =============================
    // 🎁 PROMOCIONES Y CUPONES (HU30)
    // =============================
    [Authorize(Roles = Roles.Administrador)]
    public class PromocionesController : Controller
    {
        private readonly IPromocionService _promociones;
        private readonly ApplicationDbContext _db;

        public PromocionesController(IPromocionService promociones, ApplicationDbContext db)
        {
            _promociones = promociones;
            _db = db;
        }

        public async Task<IActionResult> Index(CancellationToken ct) =>
            View(await _promociones.ListarAsync(ct));

        // Llega desde el filtro de clientes (HU29) con los clientes seleccionados
        public async Task<IActionResult> Nueva([FromQuery(Name = "clientes")] List<string>? clientes, string? criterio, CancellationToken ct)
        {
            var input = new PromocionInput
            {
                TipoDescuento = TipoDescuento.Porcentaje,
                FechaInicio = DateTime.Today,
                FechaFin = DateTime.Today.AddDays(15),
                CriterioSegmento = criterio,
                Clientes = clientes?.Distinct().ToList() ?? new()
            };
            await DatosFormularioAsync(input, ct);
            return View(input);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Crear(PromocionInput input, CancellationToken ct)
        {
            var administradorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var resultado = await _promociones.CrearAsync(input, administradorId, Url.Action("Index", "Home", null, Request.Scheme)!, ct);

            if (resultado.Error != null)
            {
                ViewBag.Error = resultado.Error;
                await DatosFormularioAsync(input, ct);
                return View(nameof(Nueva), input);
            }

            TempData["Mensaje"] = $"Promoción creada: enviamos {resultado.Enviados} {(resultado.Enviados == 1 ? "cupón" : "cupones")} por correo." +
                (resultado.Omitidos > 0 ? $" {resultado.Omitidos} {(resultado.Omitidos == 1 ? "cliente no aceptó" : "clientes no aceptaron")} recibir promociones y no se les envió." : "");
            return RedirectToAction(nameof(Detalle), new { id = resultado.Promocion!.Id });
        }

        public async Task<IActionResult> Detalle(int id, CancellationToken ct)
        {
            var promocion = await _promociones.DetalleAsync(id, ct);
            return promocion == null ? NotFound() : View(promocion);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Desactivar(int id, CancellationToken ct)
        {
            if (await _promociones.DesactivarAsync(id, ct))
                TempData["Mensaje"] = "La promoción quedó desactivada: sus cupones ya no se pueden usar.";
            return RedirectToAction(nameof(Detalle), new { id });
        }

        // Cuántos de los seleccionados recibirán el cupón (solo quienes aceptaron promociones)
        private async Task DatosFormularioAsync(PromocionInput input, CancellationToken ct)
        {
            ViewBag.ConConsentimiento = await _db.Users.CountAsync(u => input.Clientes.Contains(u.Id) && u.AceptaPromociones && u.Activo, ct);
        }
    }
}
