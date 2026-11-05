using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    public enum OrdenCatalogo
    {
        FechaAsc = 0,
        PrecioAsc = 1,
        PrecioDesc = 2,
        Recientes = 3
    }

    // =============================
    // 🔎 FILTROS DEL CATÁLOGO (HU05) — se reciben por query string
    // =============================
    public class CatalogoFiltro
    {
        public const int TamanoPagina = 9;

        [StringLength(100)]
        public string? Buscar { get; set; }

        public int? CategoriaId { get; set; }

        [DataType(DataType.Date)]
        public DateTime? Desde { get; set; }

        [DataType(DataType.Date)]
        public DateTime? Hasta { get; set; }

        [Range(0, 100000)]
        public decimal? PrecioMin { get; set; }

        [Range(0, 100000)]
        public decimal? PrecioMax { get; set; }

        public bool SoloGratis { get; set; }

        public OrdenCatalogo Orden { get; set; } = OrdenCatalogo.FechaAsc;

        public int Pagina { get; set; } = 1;

        public bool TieneFiltros =>
            !string.IsNullOrWhiteSpace(Buscar) || CategoriaId.HasValue || Desde.HasValue || Hasta.HasValue
            || PrecioMin.HasValue || PrecioMax.HasValue || SoloGratis;

        // Corrige valores fuera de rango antes de consultar
        public void Normalizar()
        {
            Buscar = string.IsNullOrWhiteSpace(Buscar) ? null : Buscar.Trim();
            if (Pagina < 1) Pagina = 1;
            if (PrecioMin < 0) PrecioMin = 0;
            if (PrecioMax < 0) PrecioMax = 0;
            if (PrecioMin.HasValue && PrecioMax.HasValue && PrecioMin > PrecioMax)
                (PrecioMin, PrecioMax) = (PrecioMax, PrecioMin);
            if (Desde.HasValue && Hasta.HasValue && Desde > Hasta)
                (Desde, Hasta) = (Hasta, Desde);
        }

        public string ClaveCache() =>
            $"{Buscar?.ToLowerInvariant()}|{CategoriaId}|{Desde:yyyyMMdd}|{Hasta:yyyyMMdd}|{PrecioMin}|{PrecioMax}|{SoloGratis}|{Orden}|{Pagina}";

        // Parámetros actuales para construir los enlaces de paginación
        public Dictionary<string, string> RutaConPagina(int pagina)
        {
            var ruta = new Dictionary<string, string> { ["Pagina"] = pagina.ToString() };
            if (Buscar != null) ruta["Buscar"] = Buscar;
            if (CategoriaId.HasValue) ruta["CategoriaId"] = CategoriaId.Value.ToString();
            if (Desde.HasValue) ruta["Desde"] = Desde.Value.ToString("yyyy-MM-dd");
            if (Hasta.HasValue) ruta["Hasta"] = Hasta.Value.ToString("yyyy-MM-dd");
            if (PrecioMin.HasValue) ruta["PrecioMin"] = PrecioMin.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (PrecioMax.HasValue) ruta["PrecioMax"] = PrecioMax.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (SoloGratis) ruta["SoloGratis"] = "true";
            if (Orden != OrdenCatalogo.FechaAsc) ruta["Orden"] = Orden.ToString();
            return ruta;
        }
    }

    public class EventoCardViewModel
    {
        public int Id { get; set; }
        public string Titulo { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string CategoriaIcono { get; set; } = "";
        public string CategoriaColor { get; set; } = "#8b5cf6";
        public string Ubicacion { get; set; } = "";
        public DateTime Fecha { get; set; }
        public decimal? PrecioDesde { get; set; }
        public string? ImagenPortada { get; set; }

        // Tiene un paquete publicitario vigente (HU12)
        public bool Destacado { get; set; }

        // Recomendaciones (HU17): por qué se sugiere y a qué distancia está
        public string? Motivo { get; set; }
        public double? DistanciaKm { get; set; }
    }

    public class ResultadoCatalogo
    {
        public List<EventoCardViewModel> Eventos { get; set; } = new();
        public int Total { get; set; }
        public int Pagina { get; set; }
        public int TotalPaginas { get; set; }
    }

    public class CatalogoViewModel
    {
        public CatalogoFiltro Filtro { get; set; } = new();
        public ResultadoCatalogo Resultado { get; set; } = new();
        public List<Categoria> Categorias { get; set; } = new();
    }

    // =============================
    // 🗂️ PÁGINA "EXPLORA POR CATEGORÍAS"
    // =============================
    public class CategoriaResumen
    {
        public Categoria Categoria { get; set; } = new();
        public int Cantidad { get; set; }
    }

    public class InicioViewModel
    {
        public string? NombreUsuario { get; set; }
        public int TotalEventos { get; set; }
        public List<EventoCardViewModel> Proximos { get; set; } = new();
        public List<EventoCardViewModel> Destacados { get; set; } = new();
        public List<EventoCardViewModel> Recomendados { get; set; } = new();
        public bool RecomendacionesPersonalizadas { get; set; }
        public bool UsaUbicacion { get; set; }
        public List<CategoriaResumen> Categorias { get; set; } = new();

        // Solo el primer nombre para el saludo
        public string? PrimerNombre => NombreUsuario?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    public class CategoriasViewModel : CatalogoViewModel
    {
        public List<CategoriaResumen> Resumen { get; set; } = new();
        public int TotalEventos { get; set; }
        public Categoria? Seleccionada { get; set; }

        public static string TextoCantidad(int cantidad) =>
            cantidad == 1 ? "1 evento" : $"{cantidad} eventos";
    }
}
