using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // Plantillas del plano del recinto (HU25 / RF21). El organizador no dibuja: elige una y la configura.
    public enum PlantillaPlano
    {
        [Display(Name = "Campo abierto")]
        CampoAbierto = 0,     // conciertos al aire libre, festivales: se vende por zona

        [Display(Name = "Teatro / auditorio")]
        Teatro = 1,           // asientos numerados (zona, fila y número)

        [Display(Name = "Estadio")]
        Estadio = 2,          // tribunas por zona

        [Display(Name = "Feria / exposición")]
        Feria = 3,            // entrada general, stands y servicios

        [Display(Name = "Coliseo / arena")]
        Coliseo = 4,          // escenario o ring al centro: campo y anillos de tribuna, por zona

        [Display(Name = "Discoteca / local nocturno")]
        Discoteca = 5         // pista, VIP, boxes y mezzanine, por zona
    }

    // Ciclo de vida del Asiento (diagrama de estados)
    public enum EstadoAsiento
    {
        Disponible = 0,
        Bloqueado = 1,
        Vendido = 2
    }

    // ASIENTO: un lugar numerado de una zona en los recintos con asientos (teatro)
    public class Asiento
    {
        public int Id { get; set; }

        public int TipoEntradaId { get; set; }
        public TipoEntrada? TipoEntrada { get; set; }

        [StringLength(3)]
        public string Fila { get; set; } = "";

        public int Numero { get; set; }

        // Posición en el plano SVG
        public int PosX { get; set; }
        public int PosY { get; set; }

        public EstadoAsiento Estado { get; set; } = EstadoAsiento.Disponible;

        public string Etiqueta => $"{Fila}-{Numero}";
    }
}

namespace ActivaFest.Models
{
    // HU27: dónde se ubica el titular de una entrada (zona, fila, asiento y puerta de ingreso)
    public record UbicacionEntrada(string Zona, string? Fila, int? Numero, string? Puerta)
    {
        public static UbicacionEntrada De(Entrada e) =>
            new(e.TipoEntrada?.Nombre ?? "", e.Asiento?.Fila, e.Asiento?.Numero, e.TipoEntrada?.PuertaIngreso);

        public bool TieneAsiento => Fila != null && Numero != null;

        public string? Asiento => TieneAsiento ? $"{Fila}-{Numero}" : null;

        // Lo que viaja cifrado dentro del QR (HU27): "Zona|Fila|Número" o solo la zona
        public string ParaQr => TieneAsiento ? $"{Zona}|{Fila}|{Numero}" : Zona;

        public string Texto =>
            (TieneAsiento ? $"{Zona} · Fila {Fila}, asiento {Numero}" : Zona) + (string.IsNullOrEmpty(Puerta) ? "" : $" · Ingreso por {Puerta}");
    }
}
