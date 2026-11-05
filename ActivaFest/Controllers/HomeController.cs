using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Recomendaciones;

namespace ActivaFest.Controllers;

public class HomeController : Controller
{
    private const int EventosEnPortada = 4;
    private const int CategoriasEnPortada = 6;

    private readonly ICatalogoService _catalogo;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IRecomendacionService _recomendaciones;

    public HomeController(ICatalogoService catalogo, UserManager<ApplicationUser> userManager, IRecomendacionService recomendaciones)
    {
        _catalogo = catalogo;
        _userManager = userManager;
        _recomendaciones = recomendaciones;
    }

    // =============================
    // 🏠 PÁGINA DE BIENVENIDA
    // =============================
    // lat/lng llegan solo si el usuario pulsa "Usar mi ubicación"; no se guardan
    public async Task<IActionResult> Index(double? lat, double? lng, CancellationToken ct)
    {
        var categorias = await _catalogo.ObtenerCategoriasAsync(ct);
        var conteo = await _catalogo.ContarPorCategoriaAsync(ct);
        var proximos = await _catalogo.BuscarAsync(new CatalogoFiltro(), ct);

        var modelo = new InicioViewModel
        {
            Proximos = proximos.Eventos.Take(EventosEnPortada).ToList(),
            Destacados = await _catalogo.DestacadosAsync(EventosEnPortada, ct),
            TotalEventos = conteo.Values.Sum(),
            // Primero las categorías con más eventos; con la base vacía se mantiene el orden alfabético
            Categorias = categorias
                .Select(c => new CategoriaResumen { Categoria = c, Cantidad = conteo.GetValueOrDefault(c.Id) })
                .OrderByDescending(c => c.Cantidad)
                .Take(CategoriasEnPortada)
                .ToList()
        };

        if (User.Identity?.IsAuthenticated == true)
        {
            var usuario = await _userManager.GetUserAsync(User);
            modelo.NombreUsuario = usuario?.NombreCompleto;

            // HU17: hasta 6 recomendados según historial y ubicación; sin historial, los más populares cercanos
            if (usuario != null)
            {
                Ubicacion? ubicacion = lat is >= -90 and <= 90 && lng is >= -180 and <= 180 ? new Ubicacion(lat.Value, lng.Value) : null;
                modelo.UsaUbicacion = ubicacion != null;
                modelo.RecomendacionesPersonalizadas = await _recomendaciones.ContarHistorialAsync(usuario.Id, ct) > 0;
                modelo.Recomendados = await _recomendaciones.RecomendarAsync(usuario.Id, ubicacion, RecomendacionService.MaximoRecomendaciones, ct);
            }
        }

        return View(modelo);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
