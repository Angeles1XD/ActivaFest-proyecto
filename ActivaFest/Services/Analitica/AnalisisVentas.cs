using ActivaFest.Models;

namespace ActivaFest.Services.Analitica;

// =============================
// 📈 ANÁLISIS DE VENTAS DEL DASHBOARD
// =============================
// Una sola fuente para los gráficos y para la IA: los dos muestran siempre las mismas cifras.
// Son datos agregados: los clientes van con su código anónimo, sin nombre ni correo.

public record TotalesPlataforma(decimal Ingresos, decimal Comisiones, int Compras, int Entradas, int Compradores, int EventosPublicados, int UsuariosRegistrados);
public record PuntoMes(string Etiqueta, decimal Ingresos, decimal Comisiones);
public record PuntoDiaSemana(string Dia, int Compras, decimal Ingresos);
public record PuntoDia(string Etiqueta, int Entradas, decimal Ingresos, decimal Acumulado);
public record PuntoHora(string Hora, int Compras);
public record EventoAnalisis(int EventoId, string Evento, string Categoria, int Compras, int Entradas, decimal Ingresos, decimal Comision, int Aforo, bool Destacado)
{
    public double? Ocupacion => Aforo == 0 ? null : Math.Round(100.0 * Entradas / Aforo, 1);
}
public record CategoriaAnalisis(int CategoriaId, string Categoria, decimal Ingresos, decimal Comisiones);
public record ConteoEtiqueta(string Etiqueta, int Total);
public record ZonaAnalisis(string Zona, int Entradas, decimal Ingresos);
public record OrganizadorAnalisis(string Organizador, int Eventos, decimal Ingresos);
public record CompradorAnalisis(string Cliente, string Segmento, int Compras, int Entradas, decimal Gasto);

// Evento con un paquete destacado vigente en el período y lo que vendió mientras estuvo destacado
public record DestacadoAnalisis(int EventoId, string Evento, string Paquete, string Vigencia, decimal PrecioPaquete, int Entradas, decimal Ingresos);

public record Recomendacion(string Prioridad, string Tema, string Hallazgo, string Dato, string Accion);

public record CategoriaEdad(string Categoria, int Asistieron);

// Un rango de edad en los eventos ya realizados. La edad es la de la cuenta que compró, el día del evento.
public record EdadAnalisis(string Rango, int Personas, int Entradas, int Asistieron, List<CategoriaEdad> PorCategoria)
{
    public double? Asistencia => Entradas == 0 ? null : Math.Round(100.0 * Asistieron / Entradas, 1);
    public string? CategoriaFavorita => PorCategoria.FirstOrDefault(c => c.Asistieron > 0)?.Categoria;
}

public static class RangoEdad
{
    public const string Menores = "Menor de 18", Mayores = "55 o más";
    public static readonly string[] Todos = { Menores, "18 a 24", "25 a 34", "35 a 44", "45 a 54", Mayores };

    public static string De(int edad) => edad switch
    {
        < 18 => Menores,
        < 25 => "18 a 24",
        < 35 => "25 a 34",
        < 45 => "35 a 44",
        < 55 => "45 a 54",
        _ => Mayores
    };
}

public class AnalisisVentas
{
    public DateTime Desde { get; set; }
    public DateTime Hasta { get; set; }
    public int? CategoriaId { get; set; }
    public int? EventoId { get; set; }
    public DateTime CalculadoEn { get; set; }

    // Las cifras son solo de los eventos de un organizador: las recomendaciones le hablan a él
    public bool ParaOrganizador { get; set; }

    // Del período: las mismas cifras de los indicadores del dashboard
    public decimal Ingresos { get; set; }
    public decimal IngresosAnterior { get; set; }
    public decimal Comisiones { get; set; }
    public int Compras { get; set; }
    public int Entradas { get; set; }
    public decimal TicketPromedio { get; set; }
    public double? Conversion { get; set; }
    public int VisitasEventos { get; set; }

    // Compradores únicos del período. Recurrente: realizó más de una compra dentro del período.
    public int Compradores { get; set; }
    public int CompradoresRecurrentes { get; set; }
    public int CompradoresNuevos => Compradores - CompradoresRecurrentes;

    public TotalesPlataforma Acumulado { get; set; } = null!;
    public List<PuntoMes> Meses { get; set; } = new();
    public List<PuntoDiaSemana> DiasSemana { get; set; } = new();

    // Cada día del período con su acumulado, y compras por hora del día
    public List<PuntoDia> VentasDiarias { get; set; } = new();
    public List<PuntoHora> ComprasPorHora { get; set; } = new();
    public List<EventoAnalisis> TopEventos { get; set; } = new();
    public List<CategoriaAnalisis> PorCategoria { get; set; } = new();
    public List<CompradorAnalisis> TopCompradores { get; set; } = new();
    public List<ResumenSegmento> Segmentos { get; set; } = new();
    public List<DestacadoAnalisis> Destacados { get; set; } = new();

    // Todos los eventos de la plataforma según su estado (no depende del período)
    public List<ConteoEtiqueta> EventosPorEstado { get; set; } = new();

    // Entradas vendidas por zona o tipo de entrada, y organizadores con más ingresos en el período
    public List<ZonaAnalisis> PorZona { get; set; } = new();
    public List<OrganizadorAnalisis> TopOrganizadores { get; set; } = new();

    // Asistencia por rango de edad en los eventos realizados del período. Solo para el administrador.
    public List<EdadAnalisis> PorEdad { get; set; } = new();
    public int EntradasSinEdad { get; set; }

    // En qué terminaron las órdenes creadas en el período: pagadas, reservas activas, vencidas…
    public List<ConteoEtiqueta> OrdenesPorEstado { get; set; } = new();

    // Eventos de los próximos 30 días con menos del 40 % de ocupación
    public List<EventoOcupacion> BajaOcupacion { get; set; } = new();

    public List<Recomendacion> Recomendaciones { get; set; } = new();
}

// =============================
// 💡 RECOMENDACIONES PARA EL ADMINISTRADOR
// =============================
// Reglas sobre las cifras reales: cada recomendación lleva el dato que la justifica, nunca un número inventado.
public static class Recomendador
{
    public const string Alta = "Alta", Media = "Media", Oportunidad = "Oportunidad";
    public const string TemaVentas = "ventas", TemaEventos = "eventos", TemaClientes = "clientes";

    public const int Maximo = 6;

    // Por debajo de estas cantidades un porcentaje no dice nada
    private const int MinimoCompradores = 5, MinimoCompras = 5, MinimoVisitas = 100;

    private static readonly string[] Orden = { Alta, Media, Oportunidad };

    private static string Pct(double v) => $"{v:0.#} %";

    public static List<Recomendacion> De(AnalisisVentas a)
    {
        var r = new List<Recomendacion>();
        Ventas(a, r);
        Eventos(a, r);
        Clientes(a, r);

        if (r.Count == 0)
            r.Add(new Recomendacion(Oportunidad, TemaVentas, "No hay alertas en este período",
                $"{Formato.Monto(a.Ingresos)} en {a.Compras} compras", "Mantén la estrategia actual y revisa de nuevo cuando haya más ventas."));

        return r.OrderBy(x => Array.IndexOf(Orden, x.Prioridad)).Take(Maximo).ToList();
    }

    private static void Ventas(AnalisisVentas a, List<Recomendacion> r)
    {
        var variacion = DashboardAnalitica.Variacion(a.Ingresos, a.IngresosAnterior);
        var comparacion = $"{Formato.Monto(a.Ingresos)} frente a {Formato.Monto(a.IngresosAnterior)} del período anterior";

        if (a.Compras == 0)
            r.Add(new Recomendacion(Alta, TemaVentas, "No hubo ventas en este período", comparacion,
                "Revisa si hay eventos publicados con entradas disponibles y lanza una promoción para reactivar la demanda."));
        else if (variacion <= -10)
            r.Add(new Recomendacion(Alta, TemaVentas, $"Las ventas bajaron {Pct(-variacion.Value)}", comparacion,
                "Lanza un cupón por tiempo limitado y revisa qué eventos dejaron de vender."));
        else if (variacion >= 10)
            r.Add(new Recomendacion(Oportunidad, TemaVentas, $"Las ventas crecieron {Pct(variacion.Value)}", comparacion,
                a.ParaOrganizador ? "Aprovecha el impulso: publica nuevas fechas de los eventos que más venden."
                    : "Aprovecha el impulso: invita a los organizadores de los eventos que más venden a publicar nuevas fechas."));

        var dia = a.DiasSemana.OrderByDescending(d => d.Compras).FirstOrDefault();
        if (dia != null && a.Compras >= MinimoCompras)
            r.Add(new Recomendacion(Oportunidad, TemaVentas, $"El {dia.Dia.ToLowerInvariant()} es el día que más se vende",
                $"{dia.Compras} de {a.Compras} compras ({Pct(100.0 * dia.Compras / a.Compras)})",
                "Programa lanzamientos y promociones para ese día."));

        if (a.CategoriaId == null && a.PorCategoria.Count >= 2 && a.Ingresos > 0)
        {
            var lider = a.PorCategoria[0];
            var parte = (double)(lider.Ingresos / a.Ingresos) * 100;
            if (parte >= 50)
                r.Add(new Recomendacion(Oportunidad, TemaVentas, $"{lider.Categoria} es la categoría líder",
                    $"{Formato.Monto(lider.Ingresos)}, el {Pct(parte)} de los ingresos",
                    a.ParaOrganizador ? "Programa más eventos de esta categoría y promociona los demás para no depender de una sola."
                        : "Busca más organizadores de esta categoría y promociona las demás para no depender de una sola."));
        }

        if (a.Conversion < 2 && a.VisitasEventos >= MinimoVisitas)
            r.Add(new Recomendacion(Media, TemaVentas, "Pocas visitas terminan en compra",
                $"Conversión de {Pct(a.Conversion!.Value)} sobre {a.VisitasEventos:N0} visitas",
                "Revisa precios, fotos y descripción de los eventos más visitados."));
    }

    private static void Eventos(AnalisisVentas a, List<Recomendacion> r)
    {
        var proximo = a.BajaOcupacion.FirstOrDefault();
        if (proximo != null)
        {
            var urgente = (proximo.Fecha - a.CalculadoEn).TotalDays <= 14;
            r.Add(new Recomendacion(urgente ? Alta : Media, TemaEventos,
                $"{a.BajaOcupacion.Count} {(a.BajaOcupacion.Count == 1 ? "evento próximo tiene" : "eventos próximos tienen")} baja ocupación",
                $"«{proximo.Evento}» va al {Pct(proximo.Ocupacion * 100)} y es el {proximo.Fecha:dd/MM/yyyy}",
                a.ParaOrganizador ? "Crea un cupón o contrata un paquete destacado antes de la fecha."
                    : "Sugiere al organizador un cupón o un paquete destacado antes de la fecha."));
        }

        var top = a.TopEventos.FirstOrDefault();
        if (top != null && a.Ingresos > 0)
        {
            var parte = (double)(top.Ingresos / a.Ingresos) * 100;
            if (parte >= 50)
                r.Add(new Recomendacion(Media, TemaEventos, "Las ventas dependen de pocos eventos",
                    $"«{top.Evento}» aporta el {Pct(parte)} de los ingresos ({Formato.Monto(top.Ingresos)})",
                    "Da visibilidad a otros eventos para repartir mejor las ventas."));
        }

        var destacado = a.Destacados.OrderByDescending(d => d.Ingresos).FirstOrDefault();
        if (destacado != null && destacado.Ingresos > 0)
            r.Add(new Recomendacion(Oportunidad, TemaEventos, "Un evento destacado está vendiendo",
                $"«{destacado.Evento}» vendió {Formato.Monto(destacado.Ingresos)} con el paquete {destacado.Paquete}",
                a.ParaOrganizador ? "Vuelve a destacar tus eventos en sus semanas clave."
                    : "Usa este resultado para ofrecer paquetes destacados a otros organizadores."));
        else if (a.Destacados.Count == 0 && a.TopEventos.Count > 0)
            r.Add(new Recomendacion(Oportunidad, TemaEventos, "Ningún evento usó un paquete destacado",
                "0 eventos destacados en este período",
                a.ParaOrganizador ? "Prueba un paquete destacado: da más visibilidad a tus eventos en la portada."
                    : "Ofrece paquetes destacados a los organizadores: es un ingreso adicional y da más visibilidad a sus eventos."));
    }

    private static void Clientes(AnalisisVentas a, List<Recomendacion> r)
    {
        var riesgo = a.Segmentos.FirstOrDefault(s => s.Segmento == SegmentoRfm.EnRiesgo);
        if (riesgo is { Clientes: > 0 })
            r.Add(new Recomendacion(Media, TemaClientes, $"{riesgo.Clientes} {(riesgo.Clientes == 1 ? "cliente dejó" : "clientes dejaron")} de comprar",
                $"Segmento En riesgo: {Formato.Monto(riesgo.GastoTotal)} de gasto histórico; {riesgo.ConConsentimiento} aceptan promociones",
                "Envía una campaña de reactivación solo a quienes aceptaron recibir promociones."));

        if (a.Compradores >= MinimoCompradores)
        {
            var recompra = 100.0 * a.CompradoresRecurrentes / a.Compradores;
            if (recompra < 30)
                r.Add(new Recomendacion(Media, TemaClientes, "Pocos compradores repiten",
                    $"{a.CompradoresRecurrentes} de {a.Compradores} compradores realizaron más de una compra en el período ({Pct(recompra)})",
                    "Crea un cupón para la segunda compra y avisa de eventos similares a los que ya asistieron."));
        }
    }
}
