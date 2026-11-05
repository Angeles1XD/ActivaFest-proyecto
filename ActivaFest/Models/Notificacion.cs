using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // =============================
    // 🔔 NOTIFICACIÓN DEL PANEL (campana de la barra superior)
    // =============================
    // Llegan los recordatorios y alertas que el usuario le pidió al asistente virtual, según su rol.
    public class Notificacion
    {
        public int Id { get; set; }

        [Required]
        public string UsuarioId { get; set; } = "";

        [Required, MaxLength(120)]
        public string Titulo { get; set; } = "";

        [MaxLength(500)]
        public string Mensaje { get; set; } = "";

        // Página a la que lleva al pulsarla (siempre interna)
        [MaxLength(200)]
        public string? Url { get; set; }

        [MaxLength(8)]
        public string Icono { get; set; } = "🔔";

        public int? EventoId { get; set; }

        public DateTime Fecha { get; set; } = DateTime.UtcNow;

        public bool Leida { get; set; }
    }

    public enum TipoAlertaAsistente
    {
        // Organizador: "avísame cuando aprueben mi evento"
        AprobacionEvento = 0,
        // Administrador: "avísame cuando haya eventos por aprobar" (se repite hasta que la quite)
        EventosPorAprobar = 1
    }

    // Alerta que el asistente vigila en segundo plano y que termina en una notificación
    public class AlertaAsistente
    {
        public int Id { get; set; }

        [Required]
        public string UsuarioId { get; set; } = "";

        public TipoAlertaAsistente Tipo { get; set; }

        public int? EventoId { get; set; }
        public Evento? Evento { get; set; }

        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

        // Última vez que se revisó (EventosPorAprobar avisa solo de los eventos llegados después)
        public DateTime UltimaRevision { get; set; } = DateTime.UtcNow;

        public bool Activa { get; set; } = true;
    }
}
