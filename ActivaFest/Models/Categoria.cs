using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    public class Categoria
    {
        public int Id { get; set; }

        [Required]
        [StringLength(60)]
        public string Nombre { get; set; } = "";

        [StringLength(8)]
        public string Icono { get; set; } = "🎫";

        // Color de acento (hex) e imagen de portada para la página de categorías
        [StringLength(9)]
        [RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Usa un color hexadecimal, por ejemplo #a855f7.")]
        public string Color { get; set; } = "#8b5cf6";

        [StringLength(200)]
        public string? Imagen { get; set; }

        public ICollection<Evento> Eventos { get; set; } = new List<Evento>();
    }
}
