using System.Text.RegularExpressions;

namespace ActivaFest.Services.Asistente;

public record EventoCandidato(int Id, string Titulo, DateTime Fecha = default);

// Encontrado: un solo evento. Opciones: varios posibles, hay que preguntar cuál.
// NoEncontrado: se pidió un evento por su nombre ("el evento de cumbia peruana") y ninguno coincide.
// Mencion: lo que parece un nombre aunque no se haya dicho la palabra "evento" ("las ventas de cumbia peruana").
public record EventoReconocido(EventoCandidato? Encontrado, List<EventoCandidato> Opciones, string? NoEncontrado, string? Mencion, string TextoSinEvento)
{
    public bool Ambiguo => Opciones.Count > 1;
}

// =============================
// 🏷️ RECONOCE EL NOMBRE DE UN EVENTO DENTRO DE UNA FRASE
// =============================
// "dime cuánto he vendido en el evento de cumbia peruana": la palabra "evento" solo anuncia el nombre, el nombre es
// "cumbia peruana". Lo usan todos los roles, cada uno con sus candidatos: el administrador todos los eventos, el
// organizador solo los suyos y el usuario los que se pueden comprar. Tolera tildes, plurales y errores de tipeo.
public static class ReconocedorEventos
{
    // Palabras que no sirven para reconocer un título
    private static readonly HashSet<string> Vacias = new()
    {
        "el", "la", "los", "las", "lo", "de", "del", "en", "y", "a", "un", "una", "unos", "unas", "al", "por", "para", "con", "que", "es", "se", "su", "sus",
        "evento", "eventos", "donde", "queda", "cuando", "hora", "cuanto", "cuanta", "cuantos", "cuantas", "cuesta", "precio", "precios", "entrada", "entradas"
    };

    // Lo que acompaña a un nombre en una frase y nunca forma parte de él
    private static readonly HashSet<string> NoSonNombre = new(Vacias)
    {
        "mi", "mis", "tu", "tus", "este", "esta", "estos", "estas", "ese", "esa", "todo", "todos", "toda", "todas", "cada", "cual", "cuales", "sobre",
        "ventas", "venta", "vendido", "vendidos", "vendidas", "vendi", "vendio", "vendieron", "ingresos", "ingreso", "comision", "comisiones", "ocupacion",
        "grafico", "graficos", "grafica", "diagrama", "barras", "pastel", "torta", "dona", "lineas", "linea", "tabla", "datos", "estadisticas", "resumen",
        "mas", "menos", "mejor", "mejores", "peor", "top", "total", "totales", "general", "plataforma", "pagina", "categoria", "categorias", "zona", "zonas",
        "publicado", "publicados", "publicada", "publicadas", "aprobado", "aprobados", "aprobada", "aprobadas", "pendiente", "pendientes", "rechazado", "rechazados",
        "cancelado", "cancelados", "realizado", "realizados", "finalizado", "finalizados", "proximo", "proximos", "proxima", "proximas", "pasado", "pasados", "pasada", "pasadas",
        "ultimo", "ultimos", "ultima", "ultimas", "siguiente", "aprobe", "rechace", "ya",
        "hoy", "ayer", "manana", "semana", "semanas", "mes", "meses", "ano", "anos", "dia", "dias", "quincena", "trimestre", "periodo", "fecha", "fechas",
        "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "setiembre", "octubre", "noviembre", "diciembre",
        "lunes", "martes", "miercoles", "jueves", "viernes", "sabado", "domingo", "favor", "hay", "tiene", "tienen", "tengo", "he", "fue", "son",
        "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve", "diez", "vip", "boleto", "boletos", "ticket", "tickets", "persona", "personas",
        "radar", "polar", "radial", "area", "burbujas", "dispersion", "apiladas", "horizontales", "columnas", "histograma", "tarta", "anillo", "circular",
        "reserva", "reservas", "orden", "ordenes", "compra", "compras", "segmento", "segmentos", "demanda", "variabilidad", "tendencia", "evolucion",
        "acumulado", "acumulados", "acumuladas", "ganancia", "ganancias", "facturacion", "recaudacion", "destacado", "destacados",
        "mayor", "menor", "alta", "alto", "baja", "bajo", "promedio", "organizador", "organizadores", "usuario", "usuarios", "cliente", "clientes", "comprador", "compradores"
    };

    // Después de estas palabras viene el nombre con seguridad; si no aparece, hay que decirlo
    private static readonly Regex MarcaFuerte = new(
        @"\bevento(?: de(?:l| la| los| las)?| llamado| llamada| denominado| titulado| que se llama| con nombre| con el nombre)? (?<nombre>.+)$", RegexOptions.Compiled);

    // Después de estas palabras puede venir un nombre, pero también una categoría o un período
    private static readonly Regex MarcaDebil = new(
        @"\b(?:ventas?|vendido|vendi|vendio|vendieron|ingresos|recaudado|ocupacion|comisiones|entradas?|boletos?|tickets?|reserva|reservame|reservar|compra|comprar|comprame|grafico|diagrama)(?: [a-z]+)?? (?:de(?:l| la| los| las)?|en(?: el| la)?|para(?: el| la)?|a(?:l)?) (?<nombre>.+)$", RegexOptions.Compiled);

    // Lo que suele ir después del nombre: período, tipo de gráfico o zona
    private static readonly Regex Cola = new(
        @" (?:este|esta|estos|estas|ultimo|ultima|ultimos|ultimas|del? \d|el \d|hoy|ayer|en (?:un|una|barras|pastel|torta|dona|lineas|forma|grafico)|como (?:un|una)|con (?:un|una|grafico)|por (?:zona|zonas|dia|dias|mes|meses|semana|semanas|hora|horas|categoria|favor|estado)|de (?:barras|pastel|torta|dona|lineas)|zona|y (?:cuanto|cuantas|cuantos|que|como|dame|muestrame|hazme))\b.*$",
        RegexOptions.Compiled);

    // comoNombre: todo el texto es el nombre, porque el asistente acaba de preguntar "¿para qué evento?"
    public static EventoReconocido Reconocer(string texto, IEnumerable<EventoCandidato> candidatos, bool comoNombre = false)
    {
        var palabras = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fuerte = comoNombre ? Limpiar(texto) : Mencion(texto, MarcaFuerte);
        var mencion = fuerte ?? Mencion(texto, MarcaDebil);
        var buscadas = mencion?.Split(' ') ?? Array.Empty<string>();

        var puntuados = candidatos
            .Select(c => (Evento: c, Puntaje: Puntaje(c.Titulo, texto, palabras, buscadas)))
            .Where(x => x.Puntaje > 0)
            .OrderByDescending(x => x.Puntaje)
            .ThenByDescending(x => x.Evento.Fecha)
            .ToList();

        if (puntuados.Count == 0)
            return new EventoReconocido(null, new(), fuerte, mencion, texto);

        // Empatados en el puntaje. Dos ediciones con el mismo título no se pueden distinguir: vale la más reciente
        var mejores = puntuados.TakeWhile(x => Math.Abs(x.Puntaje - puntuados[0].Puntaje) < 0.001)
            .Select(x => x.Evento)
            .DistinctBy(e => BaseConocimiento.Normalizar(e.Titulo))
            .ToList();

        return mejores.Count == 1
            ? new EventoReconocido(mejores[0], new(), null, mencion, SinElNombre(palabras, mejores[0].Titulo))
            : new EventoReconocido(null, mejores.Take(4).ToList(), null, mencion, texto);
    }

    // Lo que quedó después de la marca, sin período, tipo de gráfico ni palabras que no son nombre
    private static string? Mencion(string texto, Regex marca)
    {
        var m = marca.Match(texto);
        return m.Success ? Limpiar(m.Groups["nombre"].Value) : null;
    }

    private static string? Limpiar(string nombre)
    {
        var sinCola = Cola.Replace(" " + nombre, "").Trim();
        var utiles = sinCola.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !NoSonNombre.Contains(p) && (!p.All(char.IsDigit) || p.Length == 4)).ToList();
        return utiles.Count == 0 ? null : string.Join(' ', utiles);
    }

    private static double Puntaje(string titulo, string texto, string[] palabras, string[] buscadas)
    {
        var normal = BaseConocimiento.Normalizar(titulo);
        if (normal.Length == 0)
            return 0;
        if (BaseConocimiento.Contiene(texto, normal))
            return 3 + normal.Length / 1000.0;

        var clave = Clave(normal);
        if (clave.Count == 0)
            return 0;

        var cobertura = (double)clave.Count(c => palabras.Any(p => Parecida(c, p))) / clave.Count;

        // Todo lo que se nombró está en el título: "cumbia peruana" encuentra «Gran Noche de Cumbia Peruana en Lima»
        if (buscadas.Length > 0)
        {
            var nombradas = (double)buscadas.Count(b => clave.Any(c => Parecida(c, b))) / buscadas.Length;
            if (nombradas == 1)
                return 2 + cobertura;
            if (nombradas >= 0.6 && buscadas.Length > 1)
                return 1 + nombradas / 2;
        }

        return cobertura >= 0.6 ? 1 + cobertura / 2 : 0;
    }

    private static List<string> Clave(string tituloNormal)
    {
        var todas = tituloNormal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var clave = todas.Where(p => p.Length > 2 && !Vacias.Contains(p)).Distinct().ToList();
        return clave.Count > 0 ? clave : todas.Distinct().ToList();
    }

    // Misma palabra, en singular o plural, o con un error de tipeo (nunca en palabras cortas ni en números)
    public static bool Parecida(string a, string b)
    {
        if (a == b || a + "s" == b || b + "s" == a)
            return true;
        var menor = Math.Min(a.Length, b.Length);
        if (menor < 4 || a.Any(char.IsDigit) || b.Any(char.IsDigit))
            return false;
        return Distancia(a, b) <= (menor >= 8 ? 2 : 1);
    }

    private static int Distancia(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
            return 3;

        var previa = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var fila = new int[b.Length + 1];
            fila[0] = i;
            for (var j = 1; j <= b.Length; j++)
                fila[j] = Math.Min(Math.Min(fila[j - 1], previa[j]) + 1, previa[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previa = fila;
        }
        return previa[b.Length];
    }

    // El resto de la frase sin el nombre: "gráfico de ventas de Rock en la Zona Norte" no es un gráfico por zona
    private static string SinElNombre(string[] palabras, string titulo)
    {
        var clave = BaseConocimiento.Normalizar(titulo).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', palabras.Where(p => p is not ("evento" or "eventos") && !clave.Any(c => c.Length > 2 && Parecida(c, p))));
    }
}
