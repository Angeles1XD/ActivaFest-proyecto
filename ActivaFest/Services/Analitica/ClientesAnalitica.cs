using ActivaFest.Models;

namespace ActivaFest.Services.Analitica;

// Segmentos RFM del documento (HU22)
public static class SegmentoRfm
{
    public const string Vip = "VIP";
    public const string Frecuente = "Frecuente";
    public const string EnRiesgo = "En riesgo";
    public const string Nuevo = "Nuevo";
    public const string Ocasional = "Ocasional";

    public static readonly string[] Todos = { Vip, Frecuente, EnRiesgo, Nuevo, Ocasional };

    // Reglas del documento:
    //  · Nuevo: una sola compra en los últimos 30 días
    //  · En riesgo: compraba seguido (2+ compras) pero no compra hace más de 60 días
    //  · VIP: puntajes altos (4 o 5) en Recencia, Frecuencia y Monto
    //  · Frecuente: frecuencia alta (4 o 5) y monto medio (hasta 3)
    public static string De(int compras, int diasDesdeUltima, int r, int f, int m) =>
        compras == 1 && diasDesdeUltima <= 30 ? Nuevo
        : compras >= 2 && diasDesdeUltima > 60 ? EnRiesgo
        : r >= 4 && f >= 4 && m >= 4 ? Vip
        : f >= 4 && m <= 3 ? Frecuente
        : Ocasional;
}

// Una fila del ranking de clientes (sin nombre ni correo: se identifica con un código)
public class ClienteAnalitica
{
    public string UsuarioId { get; set; } = "";
    public string Codigo => CodigoDe(UsuarioId);

    public decimal Gasto { get; set; }
    public int Compras { get; set; }
    public int Entradas { get; set; }
    public DateTime UltimaCompra { get; set; }
    public int DiasDesdeUltima { get; set; }

    // Puntajes RFM de 1 a 5 (5 = mejor)
    public int R { get; set; }
    public int F { get; set; }
    public int M { get; set; }
    public string Segmento { get; set; } = SegmentoRfm.Ocasional;
    public bool AceptaPromociones { get; set; }

    public decimal GastoPromedio => Compras == 0 ? 0 : Math.Round(Gasto / Compras, 2);

    // Seudónimo estable: el administrador ve "Cliente #3F9A2C" en los listados; el nombre solo en la ficha (auditada)
    public static string CodigoDe(string usuarioId) =>
        "#" + new string(usuarioId.Where(char.IsLetterOrDigit).Take(6).ToArray()).ToUpperInvariant();
}

public record CompraCliente(string Codigo, DateTime Fecha, int EventoId, string Evento, int Entradas, decimal Total);
public record EventoAsistido(int EventoId, string Evento, DateTime Fecha, int Entradas);

// Ficha analítica de un cliente (HU22)
public class FichaCliente
{
    public ClienteAnalitica Resumen { get; set; } = new();
    public string Nombre { get; set; } = "";
    public string Correo { get; set; } = "";
    public DateTime MiembroDesde { get; set; }

    public List<CompraCliente> UltimasCompras { get; set; } = new();
    public List<EventoAsistido> EventosAsistidos { get; set; } = new();

    // Evento con el mayor precio por entrada y evento en el que más gastó en total
    public (string Evento, decimal Precio)? MayorPrecioEntrada { get; set; }
    public (string Evento, decimal Total)? MayorGastoEvento { get; set; }
    public (string Categoria, int Entradas)? CategoriaPreferida { get; set; }
}

// Filtros combinables de clientes (HU29). Gasto, entradas y compras se miden dentro del período y la categoría;
// el segmento RFM siempre se calcula con todo el historial del cliente.
public class FiltroClientes
{
    public decimal? GastoMin { get; set; }
    public decimal? GastoMax { get; set; }
    public int? EntradasMin { get; set; }
    public int? ComprasMin { get; set; }
    public DateTime? Desde { get; set; }
    public DateTime? Hasta { get; set; }
    public int? CategoriaId { get; set; }
    public int? EventoId { get; set; }
    public string? Segmento { get; set; }

    // gasto (por defecto), entradas o compras; siempre de mayor a menor
    public string? Orden { get; set; }

    public static readonly string[] Ordenes = { "gasto", "entradas", "compras" };

    public bool TieneFiltros =>
        GastoMin != null || GastoMax != null || EntradasMin != null || ComprasMin != null ||
        Desde != null || Hasta != null || CategoriaId != null || !string.IsNullOrEmpty(Segmento);

    // Texto para la auditoría (sin datos personales)
    public string Descripcion()
    {
        var partes = new List<string>();
        if (GastoMin != null) partes.Add($"gasto ≥ {GastoMin}");
        if (GastoMax != null) partes.Add($"gasto ≤ {GastoMax}");
        if (EntradasMin != null) partes.Add($"entradas ≥ {EntradasMin}");
        if (ComprasMin != null) partes.Add($"compras ≥ {ComprasMin}");
        if (Desde != null) partes.Add($"desde {Desde:dd/MM/yyyy}");
        if (Hasta != null) partes.Add($"hasta {Hasta:dd/MM/yyyy}");
        if (CategoriaId != null) partes.Add($"categoría {CategoriaId}");
        if (!string.IsNullOrEmpty(Segmento)) partes.Add($"segmento {Segmento}");
        return partes.Count == 0 ? "sin filtros" : string.Join(", ", partes);
    }
}
