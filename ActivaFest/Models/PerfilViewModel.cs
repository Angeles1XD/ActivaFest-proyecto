namespace ActivaFest.Models
{
    public class PerfilViewModel
    {
        public string Nombre { get; set; } = "";
        public string Correo { get; set; } = "";
        public List<string> Roles { get; set; } = new();
        public DateTime MiembroDesde { get; set; }
        public int EntradasCompradas { get; set; }
        public int ActividadesRegistradas { get; set; }
        public bool UsaUbicacion { get; set; }
        public List<EventoCardViewModel> Recomendados { get; set; } = new();

        public string Inicial => string.IsNullOrWhiteSpace(Nombre) ? "?" : Nombre.Trim()[..1].ToUpperInvariant();
    }
}
