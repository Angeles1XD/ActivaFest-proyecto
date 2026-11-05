using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    public enum EstadoEntrada
    {
        Activa = 0,
        Usada = 1,
        Anulada = 2
    }

    // =============================
    // 🎟️ ENTRADA DIGITAL CON QR (HU07)
    // =============================
    // Una por persona. El QR lleva el Codigo firmado; la validación en puerta (HU08) lo marcará como Usada.
    public class Entrada
    {
        public int Id { get; set; }

        // Token aleatorio de 128 bits en base64url: imposible de adivinar
        [Required]
        [StringLength(32)]
        public string Codigo { get; set; } = "";

        public int OrdenId { get; set; }

        public Orden? Orden { get; set; }

        public int EventoId { get; set; }

        public Evento? Evento { get; set; }

        public int TipoEntradaId { get; set; }

        public TipoEntrada? TipoEntrada { get; set; }

        // ENTRADA.AsientoId (HU26): asiento exacto en recintos con asientos numerados
        public int? AsientoId { get; set; }
        public Asiento? Asiento { get; set; }

        [Required]
        public string UsuarioId { get; set; } = "";

        public EstadoEntrada Estado { get; set; } = EstadoEntrada.Activa;

        public DateTime FechaEmision { get; set; } = DateTime.UtcNow;

        public DateTime? FechaUso { get; set; }

        // Personal de control de acceso que la escaneó (HU08)
        public string? ValidadoPorId { get; set; }

        // Regla para la validación en puerta (HU08): activa y dentro del plazo del evento
        public bool VigenteParaIngreso(Evento evento, DateTime ahora) =>
            Estado == EstadoEntrada.Activa && ahora <= evento.FinValidezEntradas;
    }
}
