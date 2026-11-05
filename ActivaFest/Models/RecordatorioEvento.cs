using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // =============================
    // 🔔 RECORDATORIO DE UN EVENTO PRÓXIMO (asistente virtual)
    // =============================
    // El usuario le pide al asistente "avísame de este evento": se le envía un correo antes de que empiece.
    public class RecordatorioEvento
    {
        // Horas antes del inicio en que se envía el aviso
        public const int HorasAntes = 24;

        public int Id { get; set; }

        public int EventoId { get; set; }
        public Evento? Evento { get; set; }

        [Required]
        public string UsuarioId { get; set; } = "";

        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

        // null mientras el aviso no se ha enviado
        public DateTime? EnviadoEn { get; set; }
    }
}
