using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // TIPO_ENTRADA: zona del recinto (General, VIP, Platea A...) con su precio, aforo, color en el plano y puerta (HU25)
    public class TipoEntrada
    {
        public int Id { get; set; }

        public int EventoId { get; set; }

        public Evento? Evento { get; set; }

        [Required]
        [StringLength(60)]
        public string Nombre { get; set; } = "General";

        [Range(0, 100000)]
        public decimal Precio { get; set; }

        [Range(1, 1000000)]
        public int Capacidad { get; set; }

        // =============================
        // 🗺️ PLANO DEL RECINTO (HU25)
        // =============================
        // Color de la zona en el plano (#RRGGBB)
        [StringLength(7)]
        public string? Color { get; set; }

        [StringLength(40)]
        public string? PuertaIngreso { get; set; }

        // En teatro la zona tiene asientos numerados: su capacidad es Filas x AsientosPorFila
        public bool AsientosNumerados { get; set; }
        public int? Filas { get; set; }
        public int? AsientosPorFila { get; set; }

        public ICollection<Asiento> Asientos { get; set; } = new List<Asiento>();

        // =============================
        // 📊 CONTROL DE AFORO (HU11)
        // =============================
        // Se modifican solo con UPDATE condicionales atómicos (ver CompraService), nunca leyendo y escribiendo.
        public int Vendidas { get; set; }

        // Entradas apartadas por órdenes pendientes de pago
        public int Reservadas { get; set; }

        public int Disponibles => Math.Max(0, Capacidad - Vendidas - Reservadas);

        // Token de concurrencia (HU11, "RowVersion" del modelo de datos): aumenta en cada cambio de la zona.
        // Una actualización basada en una lectura vieja no encuentra la fila y se reintenta con datos frescos.
        public long RowVersion { get; set; }
    }
}
