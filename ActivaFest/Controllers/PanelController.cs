using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using ActivaFest.Services.Panel;

namespace ActivaFest.Controllers
{
    // =============================
    // 📊 PANEL DEL ORGANIZADOR (HU13)
    // =============================
    [Authorize(Roles = Roles.Organizador + "," + Roles.Administrador)]
    public class PanelController : Controller
    {
        private readonly IPanelService _panel;
        private readonly IAnaliticaService _analitica;
        private readonly UserManager<ApplicationUser> _userManager;

        public PanelController(IPanelService panel, IAnaliticaService analitica, UserManager<ApplicationUser> userManager)
        {
            _panel = panel;
            _analitica = analitica;
            _userManager = userManager;
        }

        private string UsuarioId => _userManager.GetUserId(User)!;

        private bool EsAdmin => User.IsInRole(Roles.Administrador);

        // El organizador solo llega a sus eventos aunque pida otro en la URL. Por defecto, lo que va del año.
        private (FiltroAnalitica Filtro, string Rango) FiltroAnalisis(string? rango, DateTime? desde, DateTime? hasta, int? catAnalisis, int? eventoId) =>
            FiltroAnalitica.DeRango(rango ?? (desde == null && hasta == null ? "anio" : null), desde, hasta, catAnalisis, DateTime.Today, eventoId, EsAdmin ? null : UsuarioId);

        // Búsqueda, filtros y paginación (con muchos eventos no se cargan todos a la vez)
        public async Task<IActionResult> Index([FromQuery] FiltroPanel filtro, string? rango, DateTime? desde, DateTime? hasta, int? catAnalisis, int? eventoId, CancellationToken ct)
        {
            (ViewBag.FiltroAnalisis, ViewBag.RangoAnalisis) = FiltroAnalisis(rango, desde, hasta, catAnalisis, eventoId);
            ViewBag.EventosAnalisis = await _analitica.OpcionesEventoAsync(EsAdmin ? null : UsuarioId, ct);
            return View(await _panel.BuscarAsync(UsuarioId, EsAdmin, filtro, ct));
        }

        // Gráficos del panel: la misma fuente que el dashboard del administrador y que la IA
        public async Task<IActionResult> Analisis(string? rango, DateTime? desde, DateTime? hasta, int? catAnalisis, int? eventoId, CancellationToken ct)
        {
            var filtro = FiltroAnalisis(rango, desde, hasta, catAnalisis, eventoId).Filtro;
            var analisis = await _analitica.AnalisisAsync(filtro, ct);
            if (EsAdmin)
                await _analitica.RegistrarAuditoriaAsync(UsuarioId, $"Análisis del panel ({filtro.Desde:dd/MM/yyyy} - {filtro.Hasta:dd/MM/yyyy})", "Ordenes, Eventos", ct);
            return Json(analisis);
        }

        public async Task<IActionResult> Evento(int id, CancellationToken ct)
        {
            var panel = await _panel.DetalleAsync(id, UsuarioId, EsAdmin, ct);
            return panel == null ? NotFound() : View(panel);
        }
    }
}
