using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // =============================
    // ⭐ CALIFICACION_PERSONAL (HU33)
    // =============================
    // Después de entrar al evento, el asistente califica el trato de la persona que escaneó su entrada.
    // Una calificación por entrada; el personal no ve quién lo calificó (solo el administrador ve los comentarios,
    // sin nombres de los asistentes).
    public class CalificacionPersonal
    {
        public const int PuntajeReporte = 2;   // 1 o 2 estrellas cuentan como reporte de mal trato

        public int Id { get; set; }

        public int EntradaId { get; set; }
        public Entrada? Entrada { get; set; }

        public int EventoId { get; set; }
        public Evento? Evento { get; set; }

        // Quien escaneó la entrada (ENTRADA.ValidadoPorId)
        [Required]
        public string PersonalId { get; set; } = "";
        public ApplicationUser? Personal { get; set; }

        // Asistente que califica (dueño de la entrada)
        [Required]
        public string UsuarioId { get; set; } = "";

        [Range(1, 5)]
        public int Puntaje { get; set; }

        [StringLength(300)]
        public string? Comentario { get; set; }

        public DateTime Fecha { get; set; } = DateTime.UtcNow;

        public bool EsReporte => Puntaje <= PuntajeReporte;
    }
}
