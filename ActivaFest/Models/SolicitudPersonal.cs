using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // =============================
    // 🚪 SOLICITUD DE PERSONAL DE CONTROL (HU32)
    // =============================
    // El organizador pide al administrador, desde la misma página, qué cuentas validarán las entradas en la puerta
    // de su evento. Al aprobarla, el administrador les da el rol de Control de acceso y las asigna a ese evento.
    public class SolicitudPersonal
    {
        public int Id { get; set; }

        [Required]
        public string OrganizadorId { get; set; } = "";
        public ApplicationUser? Organizador { get; set; }

        public int EventoId { get; set; }
        public Evento? Evento { get; set; }

        // Correos de las cuentas (ya registradas) separados por coma; máximo 10
        [Required, StringLength(1000)]
        public string Correos { get; set; } = "";

        [StringLength(500)]
        public string? Mensaje { get; set; }

        public EstadoSolicitud Estado { get; set; } = EstadoSolicitud.Pendiente;

        public DateTime FechaSolicitud { get; set; } = DateTime.UtcNow;
        public DateTime? FechaRespuesta { get; set; }
        public string? RespondidaPorId { get; set; }

        [StringLength(500)]
        public string? MotivoRechazo { get; set; }

        // HU33: si nadie responde, el sistema la resuelve 1 hora antes del evento (RespondidaPorId queda vacío)
        public bool RespuestaAutomatica { get; set; }

        // Cuentas que no se habilitaron y por qué (no existe, desactivada, mala reputación o no elegida)
        [StringLength(1000)]
        public string? Observacion { get; set; }

        public IEnumerable<string> ListaCorreos => Correos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
