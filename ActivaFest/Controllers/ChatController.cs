using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ActivaFest.Models;
using ActivaFest.Services.Asistente;

namespace ActivaFest.Controllers
{
    // =============================
    // 🤖 API DEL ASISTENTE VIRTUAL (HU14)
    // =============================
    [ApiController]
    public class ChatController : ControllerBase
    {
        public const string PoliticaLimite = "chat";
        private const int LargoMaximo = 500;

        private readonly IAsistenteService _asistente;
        private readonly UserManager<ApplicationUser> _userManager;

        public ChatController(IAsistenteService asistente, UserManager<ApplicationUser> userManager)
        {
            _asistente = asistente;
            _userManager = userManager;
        }

        [HttpPost("/api/chat")]
        [EnableRateLimiting(PoliticaLimite)]
        public async Task<IActionResult> Preguntar([FromBody] SolicitudChat solicitud, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(solicitud.Mensaje))
                return BadRequest("El mensaje no puede estar vacío.");

            if (solicitud.Mensaje.Length > LargoMaximo)
                solicitud.Mensaje = solicitud.Mensaje[..LargoMaximo];

            // Coordenadas fuera de rango se ignoran
            if (solicitud.Latitud is < -90 or > 90 || solicitud.Longitud is < -180 or > 180)
                solicitud.Latitud = solicitud.Longitud = null;

            var usuarioId = User.Identity?.IsAuthenticated == true ? _userManager.GetUserId(User) : null;
            var respuesta = await _asistente.ResponderAsync(solicitud, usuarioId, ct);

            return Ok(new
            {
                reply = respuesta.Texto,
                enlaces = respuesta.Enlaces.Select(e => new { texto = e.Texto, url = e.Url }),
                sugerencias = respuesta.Sugerencias,
                eventoId = respuesta.EventoId,
                plano = respuesta.Plano,
                dashboard = respuesta.Dashboard,
                tabla = respuesta.Tabla == null ? null : new { titulo = respuesta.Tabla.Titulo, columnas = respuesta.Tabla.Columnas, filas = respuesta.Tabla.Filas },
                grafico = respuesta.Grafico,
                recomendaciones = respuesta.Recomendaciones,
                pendiente = respuesta.Pendiente,
                redirigir = respuesta.Redirigir,
                intencion = respuesta.Intencion
            });
        }
    }
}
