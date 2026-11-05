namespace ActivaFest.Models
{
    // HISTORIAL_BUSQUEDA: lo que el usuario busca en el catálogo (texto o nombre de categoría) — HU17
    public class HistorialBusqueda
    {
        public int Id { get; set; }

        public string UsuarioId { get; set; } = "";

        public string Termino { get; set; } = "";

        public DateTime Fecha { get; set; } = DateTime.UtcNow;
    }

    // VISUALIZACION_EVENTO: fichas de eventos que el usuario abrió — HU17
    public class VisualizacionEvento
    {
        public int Id { get; set; }

        public int EventoId { get; set; }

        public string UsuarioId { get; set; } = "";

        public DateTime Fecha { get; set; } = DateTime.UtcNow;
    }
}
