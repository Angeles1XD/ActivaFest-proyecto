using ActivaFest.Models;

namespace ActivaFest.Services.Analitica;

// =============================
// 🔎 RESULTADOS DE LAS CONSULTAS DE SOLO LECTURA (HU24, HU23)
// =============================
// Son datos agregados: ningún resultado lleva nombres ni correos (los clientes van con su código anónimo).

public record VentaEvento(int EventoId, string Evento, string Categoria, int Entradas, decimal Ingresos, int Aforo)
{
    public double? Ocupacion => Aforo == 0 ? null : (double)Entradas / Aforo;
}

public record VentaCategoriaPeriodo(string Categoria, int Eventos, int Entradas, decimal Ingresos);

// Organizador: null cuando quien consulta es el propio organizador
public record EventoListado(int EventoId, string Evento, string Categoria, string? Organizador, DateTime Fecha, string Estado, DateTime? Moderado, int Vendidas, int Aforo);

public record OpcionEvento(int Id, string Titulo, int CategoriaId, DateTime Fecha);
public record OpcionCategoria(int Id, string Nombre);

public static class EstadosListado
{
    public const string Aprobados = "aprobados", Publicados = "publicados", Realizados = "realizados",
        Pendientes = "pendientes", Rechazados = "rechazados", Cancelados = "cancelados";

    public static string Nombre(EstadoEvento estado, bool faltaPago, bool yaOcurrio) => estado switch
    {
        EstadoEvento.Aprobado => yaOcurrio ? "Realizado" : "Publicado",
        EstadoEvento.Pendiente => faltaPago ? "Falta pagar la publicación" : "Pendiente",
        EstadoEvento.Rechazado => "Rechazado",
        _ => "Cancelado"
    };
}

// Variabilidad de las ventas semanales de un evento: media, desviación estándar muestral y coeficiente de variación
public record VariabilidadEvento(int EventoId, string Evento, string Categoria, IReadOnlyList<int> Semanas, double Media, double Desviacion)
{
    // Umbrales del documento: CV < 20 % estable, 20–50 % monitorear, > 50 % inestable
    public const double UmbralEstable = 20, UmbralInestable = 50;

    // Umbrales configurados por el administrador (HU23); por defecto, los del documento
    public double LimiteEstable { get; init; } = UmbralEstable;
    public double LimiteInestable { get; init; } = UmbralInestable;

    public double? Cv => Media == 0 ? null : Desviacion / Media * 100;

    public string Nivel => Cv == null ? "Sin ventas" : Cv < LimiteEstable ? "Estable" : Cv <= LimiteInestable ? "Monitorear" : "Inestable";
}

public record ResumenSegmento(string Segmento, int Clientes, decimal GastoTotal, int ConConsentimiento)
{
    public decimal GastoPromedio => Clientes == 0 ? 0 : Math.Round(GastoTotal / Clientes, 2);
}

public record EventoOcupacion(int EventoId, string Evento, string Categoria, DateTime Fecha, int Vendidas, int Aforo)
{
    public double Ocupacion => Aforo == 0 ? 0 : (double)Vendidas / Aforo;
}

// =============================
// 📈 REPORTE DE VARIABILIDAD (HU23)
// =============================
// Estadísticos de una serie: media, desviación estándar muestral, CV y cuartiles (IQR) calculados con MathNet.Numerics
public record Estadisticos(int N, double Media, double? Desviacion, double? Q1, double? Q3)
{
    public double? Cv => Desviacion == null || Media == 0 ? null : Desviacion / Media * 100;
    public double? Iqr => Q1 == null || Q3 == null ? null : Q3 - Q1;

    // Valores mayores que Q3 + 1,5 × IQR se consideran atípicos
    public double? LimiteAtipico => Iqr == null ? null : Q3 + 1.5 * Iqr;

    public static Estadisticos De(IReadOnlyCollection<double> valores)
    {
        if (valores.Count == 0)
            return new Estadisticos(0, 0, null, null, null);
        var datos = valores.ToArray();
        var media = MathNet.Numerics.Statistics.Statistics.Mean(datos);
        if (datos.Length < 2)
            return new Estadisticos(1, Math.Round(media, 2), null, null, null);
        // Cuartiles con la definición R7 (la misma de Excel)
        var q1 = MathNet.Numerics.Statistics.Statistics.QuantileCustom(datos, 0.25, MathNet.Numerics.Statistics.QuantileDefinition.R7);
        var q3 = MathNet.Numerics.Statistics.Statistics.QuantileCustom(datos, 0.75, MathNet.Numerics.Statistics.QuantileDefinition.R7);
        return new Estadisticos(datos.Length, Math.Round(media, 2), Math.Round(MathNet.Numerics.Statistics.Statistics.StandardDeviation(datos), 2),
            Math.Round(q1, 2), Math.Round(q3, 2));
    }
}

public enum Semaforo { Verde, Ambar, Rojo }

public class FilaVariabilidadEvento
{
    public int EventoId { get; set; }
    public string Evento { get; set; } = "";
    public string Categoria { get; set; } = "";
    public DateTime Fecha { get; set; }
    public List<int> Semanas { get; set; } = new();
    public int Entradas { get; set; }
    public Estadisticos Ventas { get; set; } = Estadisticos.De(Array.Empty<double>());

    // Precios de las zonas del evento (variabilidad de la oferta de precios)
    public Estadisticos Precios { get; set; } = Estadisticos.De(Array.Empty<double>());
    public decimal PrecioMin { get; set; }
    public decimal PrecioMax { get; set; }

    // Ocupación actual: entradas emitidas ÷ aforo
    public double? Ocupacion { get; set; }

    public Semaforo Semaforo { get; set; }
    public string Decision { get; set; } = "";
    public bool Atipico { get; set; }
}

public class FilaVariabilidadCategoria
{
    public string Categoria { get; set; } = "";
    public int Eventos { get; set; }
    public int Entradas { get; set; }
    public Estadisticos VentasSemanales { get; set; } = Estadisticos.De(Array.Empty<double>());
    public Estadisticos PrecioPagado { get; set; } = Estadisticos.De(Array.Empty<double>());
    public Estadisticos GastoPorCompra { get; set; } = Estadisticos.De(Array.Empty<double>());
}

public record ClienteAtipico(string UsuarioId, string Codigo, decimal Gasto, int Compras);

public class ReporteVariabilidad
{
    public DateTime Desde { get; set; }
    public DateTime Hasta { get; set; }
    public int? CategoriaId { get; set; }
    public UmbralesVariabilidad Umbrales { get; set; } = new();
    public List<FilaVariabilidadEvento> Eventos { get; set; } = new();
    public List<FilaVariabilidadCategoria> Categorias { get; set; } = new();

    // Gasto por compra de todo el período y clientes con gasto atípico (> Q3 + 1,5 × IQR)
    public Estadisticos GastoPorCompra { get; set; } = Estadisticos.De(Array.Empty<double>());
    public Estadisticos GastoPorCliente { get; set; } = Estadisticos.De(Array.Empty<double>());
    public List<ClienteAtipico> ClientesAtipicos { get; set; } = new();
}
