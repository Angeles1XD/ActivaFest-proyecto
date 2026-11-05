using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // Foto del recinto / evento. Ruta es la URL pública devuelta por el almacenamiento de imágenes.
    public class EventoImagen
    {
        public int Id { get; set; }

        public int EventoId { get; set; }

        public Evento? Evento { get; set; }

        [Required]
        [StringLength(500)]
        public string Ruta { get; set; } = "";

        public int Orden { get; set; }
    }
}
