using System.Globalization;

namespace ActivaFest.Models
{
    // Formatos de presentación compartidos por las vistas
    public static class Formato
    {
        private static readonly CultureInfo Peru = CultureInfo.GetCultureInfo("es-PE");

        public static string Precio(decimal precio) =>
            precio == 0 ? "Gratis" : Monto(precio);

        // Para importes contables (ingresos, totales): siempre en soles, también el cero
        public static string Monto(decimal monto) => "S/ " + monto.ToString("#,0.00", Peru);

        // Solo deja pasar colores #rrggbb para usarlos en estilos en línea sin riesgo de inyección CSS
        public static string Color(string? color) =>
            color != null && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") ? color : "#8b5cf6";

        // "miércoles 04 de noviembre de 2026" -> "Miércoles 04 de noviembre de 2026"
        public static string Fecha(DateTime fecha, string formato)
        {
            var texto = fecha.ToString(formato, Peru);
            return texto.Length == 0 ? texto : char.ToUpper(texto[0], Peru) + texto[1..];
        }

        public static string PrecioDesde(decimal? precio) => precio switch
        {
            null => "Precio por confirmar",
            0 => "Gratis",
            _ => "Desde " + Precio(precio.Value)
        };
    }
}
