using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    public enum TipoDescuento
    {
        Porcentaje = 0,
        Monto = 1
    }

    // =============================
    // 🎁 PROMOCIÓN (HU30 / RF24)
    // =============================
    // La crea el administrador para un grupo de clientes filtrados (HU29); genera un CUPON personal por cliente.
    public class Promocion
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Nombre { get; set; } = "";

        public TipoDescuento TipoDescuento { get; set; }

        // Porcentaje (1 a 100) o monto fijo en soles
        public decimal Valor { get; set; }

        // Vigencia (fechas locales; FechaFin incluye todo el día)
        public DateTime FechaInicio { get; set; }
        public DateTime FechaFin { get; set; }

        // Filtros con los que se eligió a los clientes (HU29), para trazabilidad
        [StringLength(300)]
        public string CriterioSegmento { get; set; } = "";

        // Máximo de cupones que se pueden canjear en total (null = uno por cliente, sin tope adicional)
        public int? LimiteUsos { get; set; }

        // Cupones apartados en compras en curso o ya canjeados: se controla con UPDATE condicional contra LimiteUsos
        public int Usos { get; set; }

        public bool Activa { get; set; } = true;

        public string CreadaPorId { get; set; } = "";
        public ApplicationUser? CreadaPor { get; set; }

        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

        public ICollection<Cupon> Cupones { get; set; } = new List<Cupon>();

        public bool Vigente(DateTime ahoraLocal) =>
            Activa && ahoraLocal >= FechaInicio.Date && ahoraLocal < FechaFin.Date.AddDays(1);

        // Descuento sobre el subtotal de una compra (redondeo comercial a céntimos)
        public decimal DescuentoPara(decimal subtotal)
        {
            var descuento = TipoDescuento == TipoDescuento.Porcentaje
                ? Math.Round(subtotal * Valor / 100m, 2, MidpointRounding.AwayFromZero)
                : Valor;
            return Math.Min(descuento, subtotal);
        }

        public string Etiqueta => TipoDescuento == TipoDescuento.Porcentaje ? $"{Valor:0.##} % de descuento" : $"{Formato.Precio(Valor)} de descuento";
    }

    // =============================
    // 🎟️ CUPÓN PERSONAL Y DE UN SOLO USO (HU30)
    // =============================
    public class Cupon
    {
        public int Id { get; set; }

        public int PromocionId { get; set; }
        public Promocion? Promocion { get; set; }

        // Solo su dueño puede usarlo (se valida en el servidor)
        [Required]
        public string UsuarioId { get; set; } = "";
        public ApplicationUser? Usuario { get; set; }

        [Required]
        [StringLength(20)]
        public string Codigo { get; set; } = "";

        public bool Usado { get; set; }

        // Compra en la que se aplica: se aparta al reservar y queda fija al aprobarse el pago
        public int? OrdenId { get; set; }
        public Orden? Orden { get; set; }

        public DateTime? FechaUso { get; set; }
    }
}
