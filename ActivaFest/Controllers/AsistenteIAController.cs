using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Asistente;

namespace ActivaFest.Controllers
{
    // =============================
    // 🧠 API DEL ASISTENTE VIRTUAL CON IA (chat y voz)
    // =============================
    // Primero responde el agente de IA según el rol; si el modelo no está disponible, responde el asistente
    // base de /api/chat (palabras clave + ML.NET), así el chat nunca se queda sin respuesta.
    [ApiController]
    public class AsistenteIAController : ControllerBase
    {
        private const int LargoMaximo = 500;

        private readonly IAgenteIA _agente;
        private readonly IAsistenteService _asistente;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<AsistenteIAController> _logger;

        public AsistenteIAController(IAgenteIA agente, IAsistenteService asistente, UserManager<ApplicationUser> userManager,
            ApplicationDbContext db, ILogger<AsistenteIAController> logger)
        {
            _agente = agente;
            _asistente = asistente;
            _userManager = userManager;
            _db = db;
            _logger = logger;
        }

        [HttpPost("/api/asistente-ia")]
        [EnableRateLimiting(ChatController.PoliticaLimite)]
        public async Task<IActionResult> Preguntar([FromBody] SolicitudAgente solicitud, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(solicitud.Mensaje))
                return BadRequest("El mensaje no puede estar vacío.");

            if (solicitud.Mensaje.Length > LargoMaximo)
                solicitud.Mensaje = solicitud.Mensaje[..LargoMaximo];

            if (solicitud.Latitud is < -90 or > 90 || solicitud.Longitud is < -180 or > 180)
                solicitud.Latitud = solicitud.Longitud = null;

            var autenticado = User.Identity?.IsAuthenticated == true;
            var usuarioId = autenticado ? _userManager.GetUserId(User) : null;
            var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

            var respuesta = await _agente.ResponderAsync(solicitud, usuarioId, roles, ct);
            if (respuesta != null)
                await RegistrarAsync(solicitud.Mensaje, respuesta, usuarioId != null, ct);
            else
                respuesta = await _asistente.ResponderAsync(solicitud, usuarioId, ct); // ya registra la consulta

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
                intencion = respuesta.Intencion,
                ia = respuesta.Intencion.StartsWith("IA")
            });
        }

        // Misma métrica del OE3 que el asistente base (sin guardar quién preguntó)
        private async Task RegistrarAsync(string mensaje, RespuestaChat respuesta, bool conSesion, CancellationToken ct)
        {
            var pregunta = mensaje.Trim();
            _db.ConsultasAsistente.Add(new ConsultaAsistente
            {
                Fecha = DateTime.UtcNow,
                Pregunta = pregunta.Length > 200 ? pregunta[..200] : pregunta,
                Intencion = respuesta.Intencion.Length > 40 ? respuesta.Intencion[..40] : respuesta.Intencion,
                Resuelta = respuesta.Resuelta,
                EventoId = respuesta.EventoId,
                ConSesion = conSesion,
                RedactadaPorLlm = respuesta.RedactadaPorLlm
            });
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "No se pudo registrar la consulta al asistente con IA");
            }
        }
    }
}
