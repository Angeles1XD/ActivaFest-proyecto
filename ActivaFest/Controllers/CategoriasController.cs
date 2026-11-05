using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Recomendaciones;

namespace ActivaFest.Controllers
{
    // =============================
    // 🗂️ EXPLORA POR CATEGORÍAS (RF02)
    // =============================
    public class CategoriasController : Controller
    {
        private readonly ICatalogoService _catalogo;
        private readonly IRecomendacionService _recomendaciones;
        private readonly UserManager<ApplicationUser> _userManager;

        public CategoriasController(ICatalogoService catalogo, IRecomendacionService recomendaciones, UserManager<ApplicationUser> userManager)
        {
            _catalogo = catalogo;
            _recomendaciones = recomendaciones;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index([FromQuery] CatalogoFiltro filtro, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                filtro = new CatalogoFiltro();

            var categorias = await _catalogo.ObtenerCategoriasAsync(ct);
            var conteo = await _catalogo.ContarPorCategoriaAsync(ct);

            // Una categoría inexistente en la URL se ignora en lugar de mostrar una página vacía
            var seleccionada = categorias.FirstOrDefault(c => c.Id == filtro.CategoriaId);
            if (seleccionada == null)
                filtro.CategoriaId = null;

            var modelo = new CategoriasViewModel
            {
                Filtro = filtro,
                Categorias = categorias,
                Seleccionada = seleccionada,
                Resultado = await _catalogo.BuscarAsync(filtro, ct),
                TotalEventos = conteo.Values.Sum(),
                Resumen = categorias
                    .Select(c => new CategoriaResumen { Categoria = c, Cantidad = conteo.GetValueOrDefault(c.Id) })
                    .ToList()
            };

            // Explorar una categoría cuenta como interés para las recomendaciones (HU17)
            if (User.Identity?.IsAuthenticated == true && filtro.Pagina == 1 && (filtro.CategoriaId != null || !string.IsNullOrWhiteSpace(filtro.Buscar)))
                await _recomendaciones.RegistrarBusquedaAsync(_userManager.GetUserId(User)!, filtro.CategoriaId, filtro.Buscar, ct);

            return View(modelo);
        }
    }
}
