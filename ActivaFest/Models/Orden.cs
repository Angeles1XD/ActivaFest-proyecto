using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    public enum EstadoOrden
    {
        PendientePago = 0,
        Pagada = 1,
        Rechazada = 2,
        Expirada = 3,
        Cancelada = 4,
        // Pago aprobado después de que la reserva caducó y ya no quedaba aforo: el organizador gestiona la devolución
        RequiereReembolso = 5
    }

    // =============================
    // 🧾 ORDEN DE COMPRA (HU06)
    // =============================
    // Mientras está PendientePago mantiene reservadas sus entradas durante unos minutos (HU11).
    public class Orden
    {
        public int Id { get; set; }

        // Referencia pública (va a la pasarela y a la URL); no expone el Id interno
        [Required]
        [StringLength(32)]
        public string Codigo { get; set; } = "";

        [Required]
        public string UsuarioId { get; set; } = "";

        public ApplicationUser? Usuario { get; set; }

        public int EventoId { get; set; }

        public Evento? Evento { get; set; }

        public EstadoOrden Estado { get; set; } = EstadoOrden.PendientePago;

        // Lo que se cobra: subtotal de las entradas menos el descuento del cupón
        public decimal Total { get; set; }

        // =============================
        // 🎁 CUPÓN APLICADO (HU30)
        // =============================
        public decimal Descuento { get; set; }

        public int? CuponId { get; set; }

        public Cupon? Cupon { get; set; }

        public decimal Subtotal => Total + Descuento;

        // =============================
        // 💰 COMISIÓN DE LA PLATAFORMA (HU16 / RF14)
        // =============================
        // Se calcula y congela al confirmar el pago: un cambio de tarifa posterior no altera el historial.
        public decimal PorcentajeComision { get; set; }

        public decimal Comision { get; set; }

        // Lo que corresponde al organizador
        public decimal Neto => Total - Comision;

        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

        // Hasta cuándo se mantienen reservadas las entradas
        public DateTime FechaExpiracion { get; set; }

        public DateTime? FechaPago { get; set; }

        [StringLength(30)]
        public string? ProveedorPago { get; set; }

        // Id del pago en la pasarela (Mercado Pago payment id, etc.)
        [StringLength(100)]
        public string? ReferenciaPago { get; set; }

        public ICollection<OrdenItem> Items { get; set; } = new List<OrdenItem>();

        public ICollection<Entrada> Entradas { get; set; } = new List<Entrada>();

        public int CantidadEntradas => Items.Sum(i => i.Cantidad);
    }

    public class OrdenItem
    {
        public int Id { get; set; }

        public int OrdenId { get; set; }

        public Orden? Orden { get; set; }

        public int TipoEntradaId { get; set; }

        public TipoEntrada? TipoEntrada { get; set; }

        public int Cantidad { get; set; }

        // HU26: en recintos con asientos numerados cada ítem es un asiento (Cantidad = 1)
        public int? AsientoId { get; set; }
        public Asiento? Asiento { get; set; }

        // Precio congelado al momento de la compra
        public decimal PrecioUnitario { get; set; }

        public decimal Subtotal => PrecioUnitario * Cantidad;
    }
}
