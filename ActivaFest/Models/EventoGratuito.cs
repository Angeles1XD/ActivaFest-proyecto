using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // =============================
    // 🎉 EVENTOS GRATUITOS (HU34)
    // =============================
    // Un evento es gratuito cuando todas sus zonas tienen precio 0: no se venden entradas ni se pide personal de
    // control. La plataforma cobra una sola vez una tarifa de publicación según el aforo, y mide su alcance con las
    // visitas y los «Me interesa».

    public enum EstadoPagoPublicacion
    {
        PendientePago = 0,
        Pagado = 1
    }

    // PAGO_PUBLICACION: tarifa que paga el organizador por publicar un evento gratuito
    public class PagoPublicacion
    {
        public const string PrefijoCodigo = "PUB-";

        public int Id { get; set; }

        [Required]
        [StringLength(40)]
        public string Codigo { get; set; } = "";

        public int EventoId { get; set; }
        public Evento? Evento { get; set; }

        [Required]
        public string OrganizadorId { get; set; } = "";

        // Monto y aforo se congelan al generar el cobro
        public decimal Monto { get; set; }
        public int Aforo { get; set; }

        public EstadoPagoPublicacion Estado { get; set; } = EstadoPagoPublicacion.PendientePago;

        [StringLength(30)]
        public string? Proveedor { get; set; }

        [StringLength(100)]
        public string? Referencia { get; set; }

        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
        public DateTime? FechaPago { get; set; }
    }

    // INTERES_EVENTO: un usuario marcó «Me interesa» en un evento gratuito (uno por usuario y evento)
    public class InteresEvento
    {
        public int Id { get; set; }

        public int EventoId { get; set; }
        public Evento? Evento { get; set; }

        [Required]
        public string UsuarioId { get; set; } = "";

        public DateTime Fecha { get; set; } = DateTime.UtcNow;
    }

    // Tarifa por tramos de aforo (configurable en appsettings: "EventosGratuitos")
    public class TramoTarifa
    {
        // null = sin límite (último tramo)
        public int? HastaAforo { get; set; }
        public decimal Tarifa { get; set; }
    }

    public class EventosGratuitosOptions
    {
        public List<TramoTarifa> Tramos { get; set; } = new();

        // Tramos por defecto si la configuración no define ninguno
        public static readonly TramoTarifa[] TramosPorDefecto =
        {
            new() { HastaAforo = 100, Tarifa = 0m },
            new() { HastaAforo = 500, Tarifa = 29m },
            new() { HastaAforo = null, Tarifa = 59m }
        };
    }
}
