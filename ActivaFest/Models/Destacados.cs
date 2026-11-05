using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // =============================
    // ⭐ PAQUETE PUBLICITARIO (HU12 / RF10) — catálogo administrado por el administrador
    // =============================
    public class PaqueteDestacado
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "El nombre es obligatorio.")]
        [StringLength(60)]
        public string Nombre { get; set; } = "";

        [StringLength(300)]
        [Display(Name = "Descripción")]
        public string Descripcion { get; set; } = "";

        [Range(1, 90, ErrorMessage = "La vigencia debe estar entre 1 y 90 días.")]
        [Display(Name = "Vigencia (días)")]
        public int Dias { get; set; }

        [Range(1, 100000, ErrorMessage = "El precio debe ser mayor a 0.")]
        public decimal Precio { get; set; }

        // Mayor prioridad = aparece primero en la sección de destacados
        [Range(1, 10)]
        public int Prioridad { get; set; } = 1;

        public bool Activo { get; set; } = true;
    }

    public enum EstadoContrato
    {
        PendientePago = 0,
        Activo = 1,
        Cancelado = 2
    }

    // =============================
    // 🧾 CONTRATO DE DESTACADO — compra de un paquete para un evento
    // =============================
    // Precio, días y prioridad se congelan al contratar: cambiar el paquete después no altera lo vendido.
    public class ContratoDestacado
    {
        public const string PrefijoCodigo = "DST-";

        public int Id { get; set; }

        [Required]
        [StringLength(40)]
        public string Codigo { get; set; } = "";

        public int EventoId { get; set; }

        public Evento? Evento { get; set; }

        public int PaqueteId { get; set; }

        public PaqueteDestacado? Paquete { get; set; }

        [Required]
        public string OrganizadorId { get; set; } = "";

        public ApplicationUser? Organizador { get; set; }

        public decimal Precio { get; set; }

        public int Dias { get; set; }

        public int Prioridad { get; set; }

        public EstadoContrato Estado { get; set; } = EstadoContrato.PendientePago;

        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

        public DateTime? FechaPago { get; set; }

        // Vigencia en hora local (la misma que Evento.Fecha): desde el pago, o al terminar el destacado vigente,
        // hasta Inicio + Dias, sin pasar la fecha del evento
        public DateTime? FechaInicio { get; set; }

        public DateTime? FechaFin { get; set; }

        [StringLength(30)]
        public string? ProveedorPago { get; set; }

        [StringLength(100)]
        public string? ReferenciaPago { get; set; }

        public bool VigenteEn(DateTime ahora) =>
            Estado == EstadoContrato.Activo && FechaInicio <= ahora && FechaFin > ahora;
    }

    // =============================
    // 💸 LIQUIDACIÓN AL ORGANIZADOR (HU16 / OE5) — registro de solo inserción, auditable
    // =============================
    public class Liquidacion
    {
        public int Id { get; set; }

        [Required]
        public string OrganizadorId { get; set; } = "";

        public ApplicationUser? Organizador { get; set; }

        // Evento al que corresponde el pago (la liquidación se hace por evento)
        public int EventoId { get; set; }

        public Evento? Evento { get; set; }

        public decimal Monto { get; set; }

        // N.° de operación de la transferencia u otro comprobante
        [Required]
        [StringLength(100)]
        public string Referencia { get; set; } = "";

        [StringLength(300)]
        public string? Nota { get; set; }

        public DateTime Fecha { get; set; } = DateTime.UtcNow;

        [Required]
        public string RegistradoPorId { get; set; } = "";
    }
}
