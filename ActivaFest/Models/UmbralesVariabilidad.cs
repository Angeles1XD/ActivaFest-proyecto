namespace ActivaFest.Models
{
    // =============================
    // 🚦 UMBRALES DEL SEMÁFORO DE VARIABILIDAD (HU23)
    // =============================
    // Configurables por el administrador (una sola fila). Valores por defecto del documento:
    // CV < 20 % estable, 20–50 % monitorear, > 50 % inestable; ocupación < 40 % baja y ≥ 70 % alta.
    public class UmbralesVariabilidad
    {
        public int Id { get; set; }

        public double CvEstable { get; set; } = 20;
        public double CvInestable { get; set; } = 50;

        // Porcentajes (0–100)
        public double OcupacionBaja { get; set; } = 40;
        public double OcupacionAlta { get; set; } = 70;

        public DateTime ActualizadoEn { get; set; } = DateTime.UtcNow;

        public string? ActualizadoPorId { get; set; }

        public string? Validar()
        {
            if (CvEstable <= 0 || CvInestable <= CvEstable || CvInestable > 500)
                return "El CV estable debe ser mayor que 0 y menor que el CV inestable (máximo 500 %).";
            if (OcupacionBaja < 0 || OcupacionAlta <= OcupacionBaja || OcupacionAlta > 100)
                return "La ocupación baja debe ser menor que la alta, entre 0 % y 100 %.";
            return null;
        }
    }
}
