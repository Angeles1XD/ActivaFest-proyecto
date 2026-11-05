using System.Globalization;
using System.Net;
using System.Text;
using ActivaFest.Models;

namespace ActivaFest.Services.Plano;

// Datos mínimos para dibujar (sirven tanto para un evento guardado como para la vista previa del formulario)
public record ZonaPlano(int Id, string Nombre, string Color, string? Puerta, decimal Precio, int Capacidad, int Disponibles,
    int? Filas = null, int? AsientosPorFila = null);

public record AsientoPlano(int Id, int ZonaId, string Fila, int Numero, int X, int Y, EstadoAsiento Estado);

// =============================
// 🗺️ PLANO DEL RECINTO DESDE PLANTILLAS (HU25 / RF21)
// =============================
// El organizador no dibuja: elige una plantilla (teatro, estadio, campo abierto o feria) y configura sus zonas.
// Este servicio genera los asientos numerados (teatro) y el plano en SVG con aspecto realista: butacas con
// respaldo y número (como al elegir asiento en un bus), filas en arco mirando al escenario, cancha con sus
// líneas, tribunas con gradas, zonas en abanico frente al escenario y stands de feria. Es puro (sin BD) para
// poder probarlo y usarlo en la vista previa del formulario. El PNG del correo (PlanoImagen) usa esta geometría.
public static class PlanoService
{
    // Límites por plantilla para que el plano sea legible
    public const int MaxFilasTeatro = 40;
    public const int MaxAsientosPorFila = 24; // con pasillo central, cabe en el ancho del plano
    public static readonly IReadOnlyDictionary<PlantillaPlano, int> MaxZonas = new Dictionary<PlantillaPlano, int>
    {
        [PlantillaPlano.Teatro] = 6,
        [PlantillaPlano.Estadio] = 4,
        [PlantillaPlano.CampoAbierto] = 5,
        [PlantillaPlano.Feria] = 3,
        [PlantillaPlano.Coliseo] = 4,
        [PlantillaPlano.Discoteca] = 4
    };

    public static readonly string[] Paleta = { "#6366f1", "#a855f7", "#0d9488", "#f59e0b", "#ec4899", "#22c55e" };

    public static string ColorPorDefecto(int indice) => Paleta[indice % Paleta.Length];

    // Colores aceptados: #RRGGBB (todo lo demás usa la paleta; evita inyectar CSS/SVG)
    public static string ColorValido(string? color, int indice) =>
        color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit) ? color.ToLowerInvariant() : ColorPorDefecto(indice);

    public static string Nombre(PlantillaPlano p) => p switch
    {
        PlantillaPlano.Teatro => "Teatro / auditorio",
        PlantillaPlano.Estadio => "Estadio",
        PlantillaPlano.Coliseo => "Coliseo / arena",
        PlantillaPlano.Discoteca => "Discoteca / local nocturno",
        PlantillaPlano.Feria => "Feria / exposición",
        _ => "Campo abierto"
    };

    public static string Descripcion(PlantillaPlano p) => p switch
    {
        PlantillaPlano.Teatro => "Asientos numerados: cada zona tiene sus filas y asientos por fila.",
        PlantillaPlano.Estadio => "Tribunas alrededor de la cancha (hasta 4 zonas).",
        PlantillaPlano.Coliseo => "Escenario o ring al centro: campo y anillos de tribuna (hasta 4 zonas).",
        PlantillaPlano.Discoteca => "Pista de baile, VIP, boxes y mezzanine (hasta 4 zonas).",
        PlantillaPlano.Feria => "Entrada general con stands, puertas y servicios.",
        _ => "Zonas delante del escenario (VIP, Preferencial, General...)."
    };

    // Copia a la zona los datos del plano del formulario (color, puerta, filas y asientos según la plantilla)
    public static TipoEntrada AplicarZona(TipoEntrada zona, TipoEntradaInput datos, PlantillaPlano plantilla, int indice)
    {
        zona.Color = ColorValido(datos.Color, indice);
        zona.PuertaIngreso = string.IsNullOrWhiteSpace(datos.Puerta) ? null : datos.Puerta.Trim();
        zona.AsientosNumerados = plantilla == PlantillaPlano.Teatro;
        zona.Filas = zona.AsientosNumerados ? datos.Filas : null;
        zona.AsientosPorFila = zona.AsientosNumerados ? datos.AsientosPorFila : null;
        if (zona.AsientosNumerados)
            zona.Capacidad = (zona.Filas ?? 0) * (zona.AsientosPorFila ?? 0);
        return zona;
    }

    // Filas A..Z, luego AA, AB...
    public static string LetraFila(int indice) =>
        indice < 26 ? ((char)('A' + indice)).ToString() : LetraFila(indice / 26 - 1) + (char)('A' + indice % 26);

    // "A" → 0, "Z" → 25, "AA" → 26 (inverso de LetraFila); -1 si no es una fila válida
    public static int IndiceFila(string? fila)
    {
        if (string.IsNullOrEmpty(fila) || !fila.All(c => c is >= 'A' and <= 'Z'))
            return -1;
        var valor = 0;
        foreach (var c in fila)
            valor = valor * 26 + (c - 'A' + 1);
        return valor - 1;
    }

    // =============================
    // 🧭 CÓMO LLEGAR A MI UBICACIÓN (HU28)
    // =============================
    // Indicaciones paso a paso a partir del plano: puerta, pasillos, filas y referencias del recinto.
    // Son datos calculados (no los inventa la IA): la IA solo puede redactarlos con otras palabras.

    // Dónde queda la zona i (de "total") dentro del recinto, según la plantilla
    public static string Referencia(PlantillaPlano plantilla, int i, int total, string? zonaAnterior = null) => plantilla switch
    {
        PlantillaPlano.Estadio => i switch
        {
            0 => "la tribuna del lado izquierdo de la cancha",
            1 => "la tribuna del lado derecho de la cancha",
            2 => "la tribuna detrás del arco del fondo (arriba en el plano)",
            _ => "la tribuna detrás del arco más cercano a las puertas (abajo en el plano)"
        },
        PlantillaPlano.Coliseo => i == 0 ? "el campo, alrededor del escenario central" : $"el {Ordinal(i)} anillo de tribuna desde el escenario",
        PlantillaPlano.Discoteca => i switch
        {
            0 => "la pista central, frente a la cabina del DJ",
            1 => "el área VIP, a la izquierda de la pista",
            2 => "los boxes, a la derecha de la pista",
            _ => "el mezzanine, al fondo detrás de la pista"
        },
        PlantillaPlano.Feria => "el acceso de tu entrada, al frente del pabellón",
        PlantillaPlano.Teatro => "la platea, frente al escenario",
        _ => i == 0 ? "la zona más cercana al escenario" : $"la {OrdinalF(i + 1)} franja desde el escenario" + (zonaAnterior != null ? $", detrás de {zonaAnterior}" : "")
    };

    public static List<string> Indicaciones(PlantillaPlano plantilla, IReadOnlyList<TipoEntrada> zonasOrdenadas, TipoEntrada zona, string? fila, int? numero)
    {
        var pasos = new List<string>
        {
            string.IsNullOrWhiteSpace(zona.PuertaIngreso)
                ? "Ingresa por la entrada principal del recinto y muestra tu QR."
                : $"Ingresa por la {zona.PuertaIngreso!.Trim()} y muestra tu QR."
        };

        var indice = Math.Max(0, zonasOrdenadas.ToList().FindIndex(z => z.Id == zona.Id));

        if (plantilla == PlantillaPlano.Teatro && fila != null && numero is > 0 && zona.AsientosPorFila is > 0)
        {
            var orden = IndiceFila(fila) + 1;
            pasos.Add($"Avanza hacia el escenario por el pasillo central hasta la fila {fila} ({OrdinalF(orden)} fila desde el escenario), en la zona {zona.Nombre}.");

            // Bloque izquierdo: 1..mitad; derecho: el resto (izquierda y derecha mirando al escenario)
            var izquierda = (zona.AsientosPorFila.Value + 1) / 2;
            var (lado, desdePasillo) = numero <= izquierda ? ("izquierda", izquierda - numero.Value + 1) : ("derecha", numero.Value - izquierda);
            pasos.Add($"Tu asiento es el {numero}: el {Ordinal(desdePasillo)} a la {lado} del pasillo central, mirando al escenario.");
            return pasos;
        }

        var anterior = indice > 0 && indice - 1 < zonasOrdenadas.Count ? zonasOrdenadas[indice - 1].Nombre : null;
        var destino = Referencia(plantilla, indice, zonasOrdenadas.Count, anterior);
        pasos.Add($"{(destino.StartsWith("el ") ? "Dirígete al " + destino[3..] : "Dirígete a " + destino)}: es la zona {zona.Nombre}.");
        pasos.Add(plantilla == PlantillaPlano.Feria
            ? "Tu entrada no tiene asiento numerado: con ella recorres todo el pabellón, sus stands y el patio de comidas."
            : "Tu entrada no tiene asiento numerado: ubícate en cualquier lugar libre de tu zona.");
        return pasos;
    }

    private static string Ordinal(int n) => $"{n}.º";
    private static string OrdinalF(int n) => $"{n}.ª";

    // =============================
    // 🪑 ASIENTOS NUMERADOS (plantilla de teatro)
    // =============================
    // Las filas son arcos concéntricos alrededor de un foco situado sobre el escenario: así cada butaca mira
    // hacia el escenario, como en un teatro real. Entre zonas queda un pasillo transversal.
    private const int Ancho = 800;
    private const int Separacion = 26;    // distancia entre butacas (a lo largo de la fila) y entre filas
    private const int Pasillo = 44;       // pasillo central
    private const int PasilloZonas = 24;  // pasillo transversal entre zonas
    private const int InicioFilas = 150;  // primera fila, en el centro
    private const double FocoX = 400, FocoY = -560;

    // Las filas siguen el orden de las zonas (la primera zona queda más cerca del escenario) y las letras continúan
    // de una zona a otra, así fila + número es único en todo el teatro. Cada fila se parte en dos bloques con pasillo.
    public static List<Asiento> GenerarAsientos(IReadOnlyList<TipoEntrada> zonas)
    {
        var asientos = new List<Asiento>();
        var fila = 0;
        var bloque = 0;

        foreach (var zona in zonas)
        {
            var filas = zona.Filas ?? 0;
            var porFila = zona.AsientosPorFila ?? 0;
            if (filas <= 0 || porFila <= 0)
                continue;

            for (var f = 0; f < filas; f++, fila++)
            {
                var radio = InicioFilas - FocoY + fila * Separacion + bloque * PasilloZonas;
                for (var n = 1; n <= porFila; n++)
                {
                    var (x, y) = PuntoEnArco(radio, Desplazamiento(n, porFila));
                    asientos.Add(new Asiento
                    {
                        TipoEntrada = zona,
                        TipoEntradaId = zona.Id,
                        Fila = LetraFila(fila),
                        Numero = n,
                        PosX = x,
                        PosY = y,
                        Estado = EstadoAsiento.Disponible
                    });
                }
            }
            bloque++;
        }

        return asientos;
    }

    // Recalcula la posición de asientos ya guardados con la geometría actual (mismas filas y números; no toca su estado).
    // Devuelve cuántos cambiaron. Se usa al arrancar para los eventos creados con una versión anterior del plano.
    public static int Reubicar(IReadOnlyList<TipoEntrada> zonasOrdenadas, IEnumerable<Asiento> asientos)
    {
        var esperadas = GenerarAsientos(zonasOrdenadas).ToDictionary(a => (a.TipoEntradaId, a.Fila, a.Numero), a => (a.PosX, a.PosY));
        var cambiados = 0;
        foreach (var a in asientos)
        {
            if (!esperadas.TryGetValue((a.TipoEntradaId, a.Fila, a.Numero), out var p) || (a.PosX == p.PosX && a.PosY == p.PosY))
                continue;
            (a.PosX, a.PosY) = p;
            cambiados++;
        }
        return cambiados;
    }

    // Distancia (con signo) desde el pasillo central: bloque izquierdo 1..mitad, bloque derecho el resto
    private static double Desplazamiento(int numero, int porFila)
    {
        var izquierda = (porFila + 1) / 2;
        return numero <= izquierda
            ? -(Pasillo / 2.0 + (izquierda - numero) * Separacion + Separacion / 2.0)
            : Pasillo / 2.0 + (numero - izquierda - 1) * Separacion + Separacion / 2.0;
    }

    private static (int X, int Y) PuntoEnArco(double radio, double desplazamiento)
    {
        var angulo = desplazamiento / radio;
        return ((int)Math.Round(FocoX + radio * Math.Sin(angulo)), (int)Math.Round(FocoY + radio * Math.Cos(angulo)));
    }

    // Giro de la butaca (en grados) para que mire al escenario
    private static double GiroButaca(double x, double y) => -Math.Atan2(x - FocoX, y - FocoY) * 180 / Math.PI;

    // Plano de un evento guardado (ficha del evento); las zonas se dibujan en el orden en que se crearon
    public static string SvgDeEvento(Evento evento, IEnumerable<Asiento> asientos, int? asientoResaltado = null, int? zonaResaltada = null)
    {
        var (zonas, lista) = DatosDeEvento(evento, asientos);
        return GenerarSvg(evento.PlantillaPlano, zonas, lista, asientoResaltado, zonaResaltada);
    }

    // HU27: imagen PNG del plano con varias ubicaciones resaltadas (para el correo de la compra)
    public static byte[] PngDeEvento(Evento evento, IEnumerable<Asiento> asientos, IReadOnlySet<int> asientosResaltados, IReadOnlySet<int> zonasResaltadas)
    {
        var (zonas, lista) = DatosDeEvento(evento, asientos);
        return PlanoImagen.GenerarPng(evento.PlantillaPlano, zonas, lista, asientosResaltados, zonasResaltadas);
    }

    private static (List<ZonaPlano> Zonas, List<AsientoPlano> Asientos) DatosDeEvento(Evento evento, IEnumerable<Asiento> asientos) =>
    (
        evento.TiposEntrada.OrderBy(t => t.Id)
            .Select((t, i) => new ZonaPlano(t.Id, t.Nombre, ColorValido(t.Color, i), t.PuertaIngreso, t.Precio, t.Capacidad, t.Disponibles, t.Filas, t.AsientosPorFila))
            .ToList(),
        asientos.Select(a => new AsientoPlano(a.Id, a.TipoEntradaId, a.Fila, a.Numero, a.PosX, a.PosY, a.Estado)).ToList()
    );

    // Vista previa del formulario: el plano tal como quedaría con los datos escritos (sin guardar nada)
    public static string SvgVistaPrevia(PlantillaPlano plantilla, IReadOnlyList<TipoEntradaInput> datos)
    {
        var zonas = datos.Take(MaxZonas.GetValueOrDefault(plantilla, 5)).Select((d, i) =>
        {
            var z = AplicarZona(new TipoEntrada { Id = i + 1, Nombre = string.IsNullOrWhiteSpace(d.Nombre) ? $"Zona {i + 1}" : d.Nombre.Trim(),
                                                  Precio = Math.Max(0, d.Precio), Capacidad = Math.Max(0, d.Capacidad) }, d, plantilla, i);
            if (z.AsientosNumerados)
            {
                z.Filas = Math.Clamp(z.Filas ?? 0, 0, MaxFilasTeatro);
                z.AsientosPorFila = Math.Clamp(z.AsientosPorFila ?? 0, 0, MaxAsientosPorFila);
            }
            return z;
        }).ToList();

        // Límite total de filas para que la vista previa no crezca sin control
        var filasRestantes = MaxFilasTeatro;
        foreach (var z in zonas.Where(z => z.AsientosNumerados))
        {
            z.Filas = Math.Min(z.Filas ?? 0, filasRestantes);
            filasRestantes -= z.Filas.Value;
        }

        var asientos = plantilla == PlantillaPlano.Teatro ? GenerarAsientos(zonas) : new List<Asiento>();
        var id = 0;
        var planoAsientos = asientos.Select(a => new AsientoPlano(++id, a.TipoEntradaId, a.Fila, a.Numero, a.PosX, a.PosY, a.Estado)).ToList();
        var planoZonas = zonas.Select((z, i) => new ZonaPlano(z.Id, z.Nombre, z.Color!, z.PuertaIngreso, z.Precio, z.Capacidad, z.Capacidad, z.Filas, z.AsientosPorFila)).ToList();
        return GenerarSvg(plantilla, planoZonas, planoAsientos);
    }

    // =============================
    // 📐 GEOMETRÍA COMPARTIDA (SVG de la web y PNG del correo)
    // =============================
    public static int AltoPlano(PlantillaPlano plantilla, int zonas, IReadOnlyCollection<AsientoPlano>? asientos) => plantilla switch
    {
        PlantillaPlano.Teatro => Math.Max(320, (asientos?.Count > 0 ? asientos.Max(a => a.Y) : InicioFilas) + 124),
        PlantillaPlano.Estadio => 560,
        PlantillaPlano.Feria => 500,
        PlantillaPlano.Coliseo => (int)(2 * CentroColiseoY(zonas) + 56),
        PlantillaPlano.Discoteca => 520,
        _ => (int)(CentroCampo.Y + RadioExterior(Math.Max(zonas, 1) - 1) + 136)
    };

    // Campo abierto: las zonas son franjas en abanico frente al escenario
    private static readonly (double X, double Y) CentroCampo = (400, 60);
    private const double AperturaCampo = 0.9; // radianes a cada lado (≈ 52°)
    private static double RadioInterior(int i) => 66 + i * 80;
    private static double RadioExterior(int i) => RadioInterior(i) + 72;

    private static (double X, double Y) Polar((double X, double Y) centro, double radio, double angulo) =>
        (centro.X + radio * Math.Sin(angulo), centro.Y + radio * Math.Cos(angulo));

    // Contorno de la zona i (de "total") en las plantillas por zonas
    public static IReadOnlyList<(double X, double Y)> PoligonoZona(PlantillaPlano plantilla, int i, int total)
    {
        switch (plantilla)
        {
            case PlantillaPlano.Estadio:
                return TribunasEstadio[Math.Min(i, TribunasEstadio.Length - 1)];

            case PlantillaPlano.Coliseo:
                var cy = CentroColiseoY(total);
                return i == 0
                    ? Elipse(cy, 176, 110, false)
                    : Elipse(cy, RxExterior(i), RyExterior(i), false).Concat(Elipse(cy, RxInterior(i), RyInterior(i), true)).ToList();

            case PlantillaPlano.Discoteca:
                return AreasDiscoteca[Math.Min(i, AreasDiscoteca.Length - 1)];

            case PlantillaPlano.Feria:
                var ancho = total == 0 ? 0 : 680.0 / total;
                var x = 60 + i * ancho;
                var w = ancho - (i < total - 1 ? 12 : 0);
                return new[] { (x, 352.0), (x + w, 352.0), (x + w, 412.0), (x, 412.0) };

            default:
                var puntos = new List<(double, double)>();
                const int pasos = 24;
                for (var k = 0; k <= pasos; k++)
                    puntos.Add(Polar(CentroCampo, RadioExterior(i), -AperturaCampo + 2 * AperturaCampo * k / pasos));
                for (var k = pasos; k >= 0; k--)
                    puntos.Add(Polar(CentroCampo, RadioInterior(i), -AperturaCampo + 2 * AperturaCampo * k / pasos));
                return puntos;
        }
    }

    // Punto donde se rotula la zona (y que siempre cae dentro de ella)
    public static (double X, double Y) CentroZona(PlantillaPlano plantilla, int i, int total)
    {
        if (plantilla == PlantillaPlano.CampoAbierto)
            return (CentroCampo.X, CentroCampo.Y + (RadioInterior(i) + RadioExterior(i)) / 2);
        if (plantilla == PlantillaPlano.Coliseo)
            return (400, CentroColiseoY(total) + (i == 0 ? 76 : (RyInterior(i) + RyExterior(i)) / 2));

        var p = PoligonoZona(plantilla, i, total);
        return ((p.Min(q => q.X) + p.Max(q => q.X)) / 2, (p.Min(q => q.Y) + p.Max(q => q.Y)) / 2);
    }

    // Coliseo: la zona 0 es el campo (ringside) alrededor del escenario central; las siguientes, anillos elípticos de tribuna
    private static double RxInterior(int i) => 190 + (i - 1) * 68;
    private static double RxExterior(int i) => RxInterior(i) + 58;
    private static double RyInterior(int i) => 122 + (i - 1) * 56;
    private static double RyExterior(int i) => RyInterior(i) + 48;

    public static double CentroColiseoY(int zonas) => (zonas >= 2 ? RyExterior(Math.Min(zonas, 4) - 1) : 110) + 30;

    private static List<(double X, double Y)> Elipse(double cy, double rx, double ry, bool alReves)
    {
        const int pasos = 48;
        var puntos = Enumerable.Range(0, pasos + 1)
            .Select(k => (400 + rx * Math.Cos(2 * Math.PI * k / pasos), cy + ry * Math.Sin(2 * Math.PI * k / pasos)))
            .ToList();
        if (alReves)
            puntos.Reverse();
        return puntos;
    }

    // Discoteca: pista al centro, VIP a la izquierda, boxes a la derecha y mezzanine abajo
    private static readonly (double X, double Y)[][] AreasDiscoteca =
    {
        new[] { (250.0, 92.0), (550.0, 92.0), (550.0, 330.0), (250.0, 330.0) },
        new[] { (40.0, 92.0), (232.0, 92.0), (232.0, 330.0), (40.0, 330.0) },
        new[] { (568.0, 92.0), (760.0, 92.0), (760.0, 330.0), (568.0, 330.0) },
        new[] { (250.0, 346.0), (550.0, 346.0), (550.0, 410.0), (250.0, 410.0) }
    };

    // Estadio: occidente (izquierda), oriente (derecha), norte (arriba) y sur (abajo); el borde exterior es más ancho
    private static readonly (double X, double Y)[][] TribunasEstadio =
    {
        new[] { (232.0, 170.0), (232.0, 350.0), (66.0, 398.0), (66.0, 122.0) },
        new[] { (568.0, 170.0), (568.0, 350.0), (734.0, 398.0), (734.0, 122.0) },
        new[] { (250.0, 152.0), (550.0, 152.0), (640.0, 44.0), (160.0, 44.0) },
        new[] { (250.0, 368.0), (550.0, 368.0), (640.0, 476.0), (160.0, 476.0) }
    };

    // =============================
    // 🖼️ SVG
    // =============================
    public static string GenerarSvg(PlantillaPlano plantilla, IReadOnlyList<ZonaPlano> zonas, IReadOnlyList<AsientoPlano>? asientos = null,
        int? asientoResaltado = null, int? zonaResaltada = null)
    {
        var sb = new StringBuilder();
        var alto = AltoPlano(plantilla, zonas.Count, asientos);

        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Ancho} {alto}\" class=\"plano-svg plano-{plantilla.ToString().ToLowerInvariant()}\" role=\"img\" aria-label=\"Plano del recinto: {E(Nombre(plantilla))}\">");
        Definiciones(sb);

        switch (plantilla)
        {
            case PlantillaPlano.Teatro: Teatro(sb, zonas, asientos ?? Array.Empty<AsientoPlano>(), asientoResaltado, alto); break;
            case PlantillaPlano.Estadio: Estadio(sb, zonas, zonaResaltada); break;
            case PlantillaPlano.Feria: Feria(sb, zonas, zonaResaltada); break;
            case PlantillaPlano.Coliseo: Coliseo(sb, zonas, zonaResaltada); break;
            case PlantillaPlano.Discoteca: Discoteca(sb, zonas, zonaResaltada); break;
            default: CampoAbierto(sb, zonas, zonaResaltada, alto); break;
        }

        Puertas(sb, zonas, alto);
        sb.Append("</svg>");
        return sb.ToString();
    }

    // Texturas y degradados (mismos ids en todos los planos de la página: el contenido es idéntico)
    private static void Definiciones(StringBuilder sb) => sb.Append(
        "<defs>" +
        "<pattern id=\"af-telon\" width=\"14\" height=\"24\" patternUnits=\"userSpaceOnUse\"><rect width=\"14\" height=\"24\" fill=\"#7f1d3a\"/><rect x=\"9\" width=\"5\" height=\"24\" fill=\"#5b1029\"/></pattern>" +
        "<linearGradient id=\"af-tablado\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#5b4636\"/><stop offset=\"1\" stop-color=\"#3a2c22\"/></linearGradient>" +
        "<linearGradient id=\"af-led\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#38bdf8\"/><stop offset=\"1\" stop-color=\"#a855f7\"/></linearGradient>" +
        "<pattern id=\"af-publico\" width=\"12\" height=\"12\" patternUnits=\"userSpaceOnUse\"><circle cx=\"3\" cy=\"3\" r=\"1.6\" fill=\"#fff\" fill-opacity=\".22\"/><circle cx=\"9\" cy=\"9\" r=\"1.6\" fill=\"#fff\" fill-opacity=\".22\"/></pattern>" +
        "<pattern id=\"af-cesped\" width=\"18\" height=\"18\" patternUnits=\"userSpaceOnUse\"><rect width=\"18\" height=\"18\" fill=\"#0f2318\"/><circle cx=\"4\" cy=\"5\" r=\"1\" fill=\"#1f4d33\"/><circle cx=\"13\" cy=\"13\" r=\"1\" fill=\"#1f4d33\"/></pattern>" +
        "<pattern id=\"af-losetas\" width=\"24\" height=\"24\" patternUnits=\"userSpaceOnUse\"><rect width=\"24\" height=\"24\" fill=\"#17171f\"/><path d=\"M24 0H0V24\" fill=\"none\" stroke=\"#26262f\" stroke-width=\"1\"/></pattern>" +
        "<pattern id=\"af-toldo\" width=\"10\" height=\"14\" patternUnits=\"userSpaceOnUse\"><rect width=\"5\" height=\"14\" fill=\"#fff\" fill-opacity=\".85\"/><rect x=\"5\" width=\"5\" height=\"14\" fill=\"#fff\" fill-opacity=\".15\"/></pattern>" +
        "<pattern id=\"af-parquet\" width=\"40\" height=\"10\" patternUnits=\"userSpaceOnUse\"><rect width=\"40\" height=\"10\" fill=\"#b45309\"/><rect width=\"20\" height=\"5\" fill=\"#92400e\"/><rect x=\"20\" y=\"5\" width=\"20\" height=\"5\" fill=\"#92400e\"/></pattern>" +
        "<pattern id=\"af-pista\" width=\"30\" height=\"30\" patternUnits=\"userSpaceOnUse\"><rect width=\"15\" height=\"15\" fill=\"#f0abfc\" fill-opacity=\".35\"/><rect x=\"15\" y=\"15\" width=\"15\" height=\"15\" fill=\"#67e8f9\" fill-opacity=\".3\"/></pattern>" +
        "</defs>");

    // ---------- 🎭 Teatro / auditorio ----------
    private static void Teatro(StringBuilder sb, IReadOnlyList<ZonaPlano> zonas, IReadOnlyList<AsientoPlano> asientos, int? resaltado, int alto)
    {
        // Sala, telón, tablado con borde curvo y candilejas
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"18\" y=\"8\" width=\"764\" height=\"{alto - 16}\" rx=\"28\" class=\"plano-sala\"/>");
        sb.Append("<rect x=\"186\" y=\"14\" width=\"428\" height=\"24\" rx=\"4\" fill=\"url(#af-telon)\" class=\"plano-telon\"/>");
        sb.Append("<path d=\"M186 38H614V84Q400 126 186 84Z\" fill=\"url(#af-tablado)\" class=\"plano-tablado\"/>");
        for (var k = 1; k < 12; k++)
        {
            var t = k / 12.0;
            var (lx, ly) = (Math.Pow(1 - t, 2) * 186 + 2 * (1 - t) * t * 400 + t * t * 614, Math.Pow(1 - t, 2) * 84 + 2 * (1 - t) * t * 126 + t * t * 84);
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{F(lx)}\" cy=\"{F(ly - 3)}\" r=\"2.2\" class=\"plano-candileja\"/>");
        }
        sb.Append("<text x=\"400\" y=\"72\" text-anchor=\"middle\" class=\"plano-escenario-texto\">ESCENARIO</text>");

        if (asientos.Count == 0)
            return;

        var colores = zonas.ToDictionary(z => z.Id, z => z.Color);
        var nombres = zonas.ToDictionary(z => z.Id, z => z.Nombre);
        var ultima = asientos.Max(a => a.Y);

        // Pasillo central alfombrado
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"386\" y=\"{InicioFilas - 18}\" width=\"28\" height=\"{ultima - InicioFilas + 36}\" rx=\"6\" class=\"plano-pasillo\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"400\" y1=\"{InicioFilas - 12}\" x2=\"400\" y2=\"{ultima + 12}\" class=\"plano-pasillo-linea\"/>");

        // Rótulo de cada zona en el pasillo transversal que la precede
        foreach (var zona in zonas)
        {
            var deZona = asientos.Where(a => a.ZonaId == zona.Id).ToList();
            if (deZona.Count == 0)
                continue;
            var radio = deZona.Min(a => Distancia(a.X, a.Y));
            var y = FocoY + radio - 25;
            var texto = $"{zona.Nombre} · {(zona.Precio == 0 ? "Gratis" : $"S/ {zona.Precio.ToString("0.00", CultureInfo.InvariantCulture)}")}";
            var ancho = Math.Min(300, 22 + texto.Length * 6.4);
            sb.Append(CultureInfo.InvariantCulture,
                $"<g class=\"plano-rotulo-zona\"><rect x=\"{F(400 - ancho / 2)}\" y=\"{F(y - 9)}\" width=\"{F(ancho)}\" height=\"18\" rx=\"9\" fill=\"{colores[zona.Id]}\"/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"400\" y=\"{F(y + 3.5)}\" text-anchor=\"middle\">{E(texto)}</text></g>");
        }

        // Letra de la fila en ambos extremos, siguiendo la curva
        foreach (var fila in asientos.GroupBy(a => a.Fila))
        {
            var ordenados = fila.OrderBy(a => a.X).ToList();
            foreach (var (a, lado) in new[] { (ordenados[0], -1), (ordenados[^1], 1) })
            {
                var radio = Distancia(a.X, a.Y);
                var angulo = Math.Atan2(a.X - FocoX, a.Y - FocoY) + lado * 24 / radio;
                var (lx, ly) = (FocoX + radio * Math.Sin(angulo), FocoY + radio * Math.Cos(angulo));
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{F(lx)}\" y=\"{F(ly + 4)}\" text-anchor=\"middle\" transform=\"rotate({F(-angulo * 180 / Math.PI)} {F(lx)} {F(ly)})\" class=\"plano-fila\">{E(fila.Key)}</text>");
            }
        }

        // Butacas: asiento, respaldo y apoyabrazos, con su número (como al elegir asiento en un bus)
        foreach (var a in asientos)
        {
            var libre = a.Estado == EstadoAsiento.Disponible;
            var tuyo = a.Id == resaltado;
            var clase = tuyo ? "asiento asiento-tuyo" : libre ? "asiento" : "asiento asiento-ocupado";
            var color = tuyo ? "#ec4899" : libre ? colores.GetValueOrDefault(a.ZonaId, Paleta[0]) : "#3f3f46";
            var zona = nombres.GetValueOrDefault(a.ZonaId, "");
            var escala = tuyo ? " scale(1.3)" : "";
            sb.Append(CultureInfo.InvariantCulture,
                $"<g class=\"{clase}\" data-asiento=\"{a.Id}\" data-zona=\"{a.ZonaId}\" data-fila=\"{E(a.Fila)}\" data-numero=\"{a.Numero}\" data-libre=\"{(libre ? 1 : 0)}\" fill=\"{color}\" transform=\"translate({a.X} {a.Y}) rotate({F(GiroButaca(a.X, a.Y))}){escala}\">");
            sb.Append("<rect class=\"butaca-respaldo\" x=\"-10.5\" y=\"5\" width=\"21\" height=\"6\" rx=\"3\"/>");
            sb.Append("<rect class=\"butaca-brazo\" x=\"-11.5\" y=\"-6\" width=\"3\" height=\"12\" rx=\"1.5\"/>");
            sb.Append("<rect class=\"butaca-brazo\" x=\"8.5\" y=\"-6\" width=\"3\" height=\"12\" rx=\"1.5\"/>");
            sb.Append("<rect class=\"butaca\" x=\"-8.5\" y=\"-9\" width=\"17\" height=\"14\" rx=\"4\"/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text y=\"0.5\" class=\"butaca-num\">{a.Numero}</text>");
            sb.Append($"<title>{E(zona)} · Fila {E(a.Fila)} · Asiento {a.Numero}{(libre ? "" : " (no disponible)")}</title></g>");
            if (tuyo)
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{a.X + 20}\" y=\"{a.Y + 4}\" class=\"plano-etiqueta-tuya\">{E(a.Fila)}-{a.Numero}</text>");
        }

        // Fondo de la sala: cabina de control y servicios
        Servicio(sb, 350, ultima + 32, "🎛️ Cabina", 100);
        Servicio(sb, 34, ultima + 32, "🚻 SS.HH.", 96);
        Servicio(sb, Ancho - 130, ultima + 32, "🟩 Salida", 96);
    }

    private static double Distancia(double x, double y) => Math.Sqrt((x - FocoX) * (x - FocoX) + (y - FocoY) * (y - FocoY));

    // ---------- 🏟️ Estadio / coliseo ----------
    private static void Estadio(StringBuilder sb, IReadOnlyList<ZonaPlano> zonas, int? resaltada)
    {
        // Casco del estadio y explanada
        sb.Append("<rect x=\"18\" y=\"10\" width=\"764\" height=\"494\" rx=\"140\" class=\"plano-estadio-casco\"/>");
        sb.Append("<rect x=\"48\" y=\"32\" width=\"704\" height=\"452\" rx=\"118\" class=\"plano-estadio-explanada\"/>");

        // Torres de iluminación en las esquinas
        foreach (var (x, y) in new[] { (96, 74), (704, 74), (96, 440), (704, 440) })
        {
            sb.Append($"<circle cx=\"{x}\" cy=\"{y}\" r=\"20\" class=\"plano-luz-halo\"/>");
            sb.Append($"<circle cx=\"{x}\" cy=\"{y}\" r=\"7\" class=\"plano-luz\"/>");
        }

        // Tribunas: las que no tienen zona se ven sin venta
        for (var i = 0; i < TribunasEstadio.Length; i++)
        {
            if (i < zonas.Count)
                Zona(sb, zonas[i], TribunasEstadio[i], CentroZona(PlantillaPlano.Estadio, i, zonas.Count), resaltada, Gradas(TribunasEstadio[i]));
            else
                sb.Append($"<path d=\"{Ruta(TribunasEstadio[i])}\" class=\"plano-tribuna-vacia\"/>{Gradas(TribunasEstadio[i])}");
        }

        // Cancha con franjas de césped y líneas reglamentarias
        sb.Append("<g class=\"plano-cancha\">");
        for (var k = 0; k < 10; k++)
            sb.Append($"<rect x=\"{250 + k * 30}\" y=\"170\" width=\"30\" height=\"180\" fill=\"{(k % 2 == 0 ? "#15803d" : "#166534")}\"/>");
        sb.Append("<g class=\"plano-cancha-linea\" fill=\"none\">" +
                  "<rect x=\"258\" y=\"178\" width=\"284\" height=\"164\"/>" +
                  "<line x1=\"400\" y1=\"178\" x2=\"400\" y2=\"342\"/>" +
                  "<circle cx=\"400\" cy=\"260\" r=\"26\"/>" +
                  "<rect x=\"258\" y=\"222\" width=\"44\" height=\"76\"/><rect x=\"258\" y=\"244\" width=\"16\" height=\"32\"/>" +
                  "<rect x=\"498\" y=\"222\" width=\"44\" height=\"76\"/><rect x=\"526\" y=\"244\" width=\"16\" height=\"32\"/>" +
                  "<path d=\"M302 246A18 18 0 0 1 302 274\"/><path d=\"M498 246A18 18 0 0 0 498 274\"/>" +
                  "</g>");
        sb.Append("<circle cx=\"400\" cy=\"260\" r=\"2.5\" fill=\"#fff\"/>");
        sb.Append("<rect x=\"252\" y=\"250\" width=\"6\" height=\"20\" class=\"plano-arco\"/><rect x=\"542\" y=\"250\" width=\"6\" height=\"20\" class=\"plano-arco\"/>");
        sb.Append("</g>");
    }

    // Líneas de gradas: paralelas al borde que da a la cancha, del interior al exterior
    private static string Gradas(IReadOnlyList<(double X, double Y)> p)
    {
        var sb = new StringBuilder("<g class=\"plano-gradas\">");
        for (var k = 1; k < 8; k++)
        {
            var t = k / 8.0;
            var (a, b) = (Mezcla(p[0], p[3], t), Mezcla(p[1], p[2], t));
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(a.X)}\" y1=\"{F(a.Y)}\" x2=\"{F(b.X)}\" y2=\"{F(b.Y)}\"/>");
        }
        return sb.Append("</g>").ToString();
    }

    private static (double X, double Y) Mezcla((double X, double Y) a, (double X, double Y) b, double t) =>
        (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    // ---------- 🎤 Campo abierto (conciertos y festivales) ----------
    private static void CampoAbierto(StringBuilder sb, IReadOnlyList<ZonaPlano> zonas, int? resaltada, int alto)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"12\" y=\"8\" width=\"776\" height=\"{alto - 16}\" rx=\"24\" fill=\"url(#af-cesped)\" class=\"plano-terreno\"/>");

        // Escenario con estructura de truss, pantallas LED y torres de parlantes
        sb.Append("<path d=\"M246 22H554L588 92H212Z\" fill=\"url(#af-tablado)\" class=\"plano-tablado\"/>");
        sb.Append("<rect x=\"246\" y=\"14\" width=\"308\" height=\"8\" class=\"plano-truss\"/>");
        sb.Append("<line x1=\"252\" y1=\"14\" x2=\"222\" y2=\"92\" class=\"plano-truss-linea\"/><line x1=\"548\" y1=\"14\" x2=\"578\" y2=\"92\" class=\"plano-truss-linea\"/>");
        sb.Append("<rect x=\"140\" y=\"26\" width=\"62\" height=\"40\" rx=\"3\" fill=\"url(#af-led)\" class=\"plano-led\"/><rect x=\"598\" y=\"26\" width=\"62\" height=\"40\" rx=\"3\" fill=\"url(#af-led)\" class=\"plano-led\"/>");
        sb.Append("<text x=\"171\" y=\"80\" text-anchor=\"middle\" class=\"plano-nota\">Pantalla</text><text x=\"629\" y=\"80\" text-anchor=\"middle\" class=\"plano-nota\">Pantalla</text>");
        sb.Append("<rect x=\"214\" y=\"60\" width=\"16\" height=\"28\" rx=\"2\" class=\"plano-parlante\"/><rect x=\"570\" y=\"60\" width=\"16\" height=\"28\" rx=\"2\" class=\"plano-parlante\"/>");
        sb.Append("<text x=\"400\" y=\"62\" text-anchor=\"middle\" class=\"plano-escenario-texto\">ESCENARIO</text>");

        // Zonas en abanico, con vallas entre ellas
        var n = Math.Min(zonas.Count, MaxZonas[PlantillaPlano.CampoAbierto]);
        sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{Arco(RadioInterior(0) - 6)}\" class=\"plano-valla\"/>");
        for (var i = 0; i < n; i++)
        {
            Zona(sb, zonas[i], PoligonoZona(PlantillaPlano.CampoAbierto, i, n), CentroZona(PlantillaPlano.CampoAbierto, i, n), resaltada,
                $"<path d=\"{Ruta(PoligonoZona(PlantillaPlano.CampoAbierto, i, n))}\" fill=\"url(#af-publico)\" class=\"plano-textura\"/>");
            if (i < n - 1)
                sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{Arco(RadioExterior(i) + 4)}\" class=\"plano-valla\"/>");
        }

        // Torre de sonido (FOH) detrás de la última zona y servicios a los costados
        var fondo = CentroCampo.Y + RadioExterior(Math.Max(n, 1) - 1);
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"382\" y=\"{F(fondo + 12)}\" width=\"36\" height=\"22\" rx=\"4\" class=\"plano-foh\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"426\" y=\"{F(fondo + 27)}\" class=\"plano-nota\">Torre de sonido</text>");

        // Los servicios (x de 26 a 138) van debajo del arco exterior para no tapar ninguna zona
        var radio = RadioExterior(Math.Max(n, 1) - 1);
        const double bordeServicio = 400 - 138;
        var lateral = radio * Math.Sin(AperturaCampo) > bordeServicio
            ? CentroCampo.Y + Math.Sqrt(radio * radio - bordeServicio * bordeServicio) + 12
            : 110;
        Servicio(sb, 26, (int)lateral, "🚻 Baños", 112);
        Servicio(sb, 26, (int)lateral + 44, "⛑️ Tópico", 112);
        Servicio(sb, Ancho - 138, (int)lateral, "🍔 Comida", 112);
        Servicio(sb, Ancho - 138, (int)lateral + 44, "🥤 Bar", 112);
    }

    private static string Arco(double radio)
    {
        var (a, b) = (Polar(CentroCampo, radio, -AperturaCampo), Polar(CentroCampo, radio, AperturaCampo));
        return string.Create(CultureInfo.InvariantCulture, $"M{F(a.X)} {F(a.Y)}A{F(radio)} {F(radio)} 0 0 0 {F(b.X)} {F(b.Y)}");
    }

    // ---------- 🎪 Feria / exposición ----------
    private static void Feria(StringBuilder sb, IReadOnlyList<ZonaPlano> zonas, int? resaltada)
    {
        // Pabellón con piso de losetas
        sb.Append("<rect x=\"30\" y=\"18\" width=\"740\" height=\"318\" rx=\"16\" fill=\"url(#af-losetas)\" class=\"plano-pabellon\"/>");

        // Stands con toldo a rayas, mostrador y número
        var toldos = new[] { "#ef4444", "#f59e0b", "#22c55e", "#3b82f6", "#a855f7" };
        for (var fila = 0; fila < 3; fila++)
            for (var col = 0; col < 5; col++)
            {
                var x = 56 + col * 100;
                var y = 36 + fila * 96;
                var color = toldos[(fila + col) % toldos.Length];
                sb.Append($"<g class=\"plano-stand\"><rect x=\"{x}\" y=\"{y}\" width=\"84\" height=\"66\" rx=\"5\" class=\"plano-stand-base\"/>");
                sb.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"84\" height=\"14\" rx=\"4\" fill=\"{color}\"/><rect x=\"{x}\" y=\"{y}\" width=\"84\" height=\"14\" rx=\"4\" fill=\"url(#af-toldo)\" opacity=\".35\"/>");
                sb.Append($"<rect x=\"{x + 10}\" y=\"{y + 50}\" width=\"64\" height=\"6\" rx=\"2\" class=\"plano-mostrador\"/>");
                sb.Append($"<text x=\"{x + 42}\" y=\"{y + 38}\" text-anchor=\"middle\" class=\"plano-stand-num\">{(char)('A' + fila)}{col + 1}</text></g>");
            }

        // Patio de comidas: mesas con sillas
        sb.Append("<rect x=\"566\" y=\"40\" width=\"184\" height=\"262\" rx=\"10\" class=\"plano-patio\"/>");
        sb.Append("<text x=\"658\" y=\"62\" text-anchor=\"middle\" class=\"plano-nota\">🍽️ Patio de comidas</text>");
        for (var f = 0; f < 3; f++)
            for (var c = 0; c < 2; c++)
            {
                var (mx, my) = (616 + c * 84, 104 + f * 70);
                sb.Append($"<circle cx=\"{mx}\" cy=\"{my}\" r=\"13\" class=\"plano-mesa\"/>");
                foreach (var (dx, dy) in new[] { (0, -20), (0, 20), (-20, 0), (20, 0) })
                    sb.Append($"<circle cx=\"{mx + dx}\" cy=\"{my + dy}\" r=\"4.5\" class=\"plano-silla\"/>");
            }

        Servicio(sb, 300, 300, "ℹ️ Informes", 110);

        // Accesos por tipo de entrada, con molinetes
        var n = Math.Min(zonas.Count, MaxZonas[PlantillaPlano.Feria]);
        for (var i = 0; i < n; i++)
        {
            var p = PoligonoZona(PlantillaPlano.Feria, i, n);
            var molinetes = new StringBuilder();
            for (var x = p[0].X + 16; x < p[1].X - 10; x += 22)
                molinetes.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(x)}\" y=\"352\" width=\"4\" height=\"10\" rx=\"1\" class=\"plano-molinete\"/>");
            Zona(sb, zonas[i], p, CentroZona(PlantillaPlano.Feria, i, n), resaltada, molinetes.ToString());
        }

        Servicio(sb, 60, 426, "🚻 Baños", 110);
        Servicio(sb, 630, 426, "🅿️ Parqueo", 110);
    }

    // ---------- 🥊 Coliseo / arena ----------
    private static void Coliseo(StringBuilder sb, IReadOnlyList<ZonaPlano> zonas, int? resaltada)
    {
        var n = Math.Min(zonas.Count, MaxZonas[PlantillaPlano.Coliseo]);
        var cy = CentroColiseoY(n);
        var (rx, ry) = n >= 2 ? (RxExterior(n - 1), RyExterior(n - 1)) : (176.0, 110.0);

        // Casco del coliseo y explanada
        sb.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"400\" cy=\"{F(cy)}\" rx=\"{F(rx + 16)}\" ry=\"{F(ry + 16)}\" class=\"plano-estadio-casco\"/>");

        // Anillos de tribuna (de afuera hacia adentro) con sus gradas
        for (var i = n - 1; i >= 1; i--)
        {
            var gradas = new StringBuilder("<g class=\"plano-gradas\">");
            foreach (var t in new[] { 0.33, 0.66 })
                gradas.Append(CultureInfo.InvariantCulture,
                    $"<ellipse cx=\"400\" cy=\"{F(cy)}\" rx=\"{F(RxInterior(i) + (RxExterior(i) - RxInterior(i)) * t)}\" ry=\"{F(RyInterior(i) + (RyExterior(i) - RyInterior(i)) * t)}\"/>");
            Zona(sb, zonas[i], PoligonoZona(PlantillaPlano.Coliseo, i, n), CentroZona(PlantillaPlano.Coliseo, i, n), resaltada, gradas.Append("</g>").ToString(), RutaAnillo(cy, i));
        }

        // Campo / ringside con piso de parquet (o solo el piso si no hay zonas)
        if (n >= 1)
            Zona(sb, zonas[0], PoligonoZona(PlantillaPlano.Coliseo, 0, n), CentroZona(PlantillaPlano.Coliseo, 0, n), resaltada,
                $"<path d=\"{Ruta(PoligonoZona(PlantillaPlano.Coliseo, 0, n))}\" fill=\"url(#af-parquet)\" class=\"plano-textura\" opacity=\".35\"/>");
        else
            sb.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"400\" cy=\"{F(cy)}\" rx=\"176\" ry=\"110\" fill=\"url(#af-parquet)\"/>");

        // Escenario o ring al centro: lona, cuerdas y esquineros
        var y = cy - 40;
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"346\" y=\"{F(y)}\" width=\"108\" height=\"80\" rx=\"4\" class=\"plano-ring\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"354\" y=\"{F(y + 8)}\" width=\"92\" height=\"64\" class=\"plano-ring-cuerdas\"/>");
        foreach (var (px, py) in new[] { (354.0, y + 8), (446.0, y + 8), (354.0, y + 72), (446.0, y + 72) })
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{F(px)}\" cy=\"{F(py)}\" r=\"3.5\" class=\"plano-ring-poste\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"400\" y=\"{F(cy + 4)}\" text-anchor=\"middle\" class=\"plano-ring-texto\">ESCENARIO</text>");
    }

    // Anillo como dos elipses (exterior e interior) con regla par-impar: sin la costura que deja un polígono continuo
    private static string RutaAnillo(double cy, int i)
    {
        string Contorno(double rx, double ry) => string.Create(CultureInfo.InvariantCulture,
            $"M{F(400 - rx)} {F(cy)}A{F(rx)} {F(ry)} 0 1 0 {F(400 + rx)} {F(cy)}A{F(rx)} {F(ry)} 0 1 0 {F(400 - rx)} {F(cy)}Z");
        return Contorno(RxExterior(i), RyExterior(i)) + Contorno(RxInterior(i), RyInterior(i));
    }

    // ---------- 🪩 Discoteca / local nocturno ----------
    private static void Discoteca(StringBuilder sb, IReadOnlyList<ZonaPlano> zonas, int? resaltada)
    {
        var n = Math.Min(zonas.Count, MaxZonas[PlantillaPlano.Discoteca]);
        sb.Append("<rect x=\"20\" y=\"10\" width=\"760\" height=\"440\" rx=\"20\" class=\"plano-local\"/>");

        // Áreas: con zona se venden; sin zona se ven como lounge o guardarropa
        var decoraciones = new Func<string>[] { DecoracionPista, DecoracionVip, DecoracionBoxes, DecoracionMezzanine };
        var vacias = new[] { "Pista", "🛋️ Lounge", "🛋️ Lounge", "🧥 Guardarropa" };
        for (var i = 0; i < AreasDiscoteca.Length; i++)
        {
            if (i < n)
            {
                Zona(sb, zonas[i], AreasDiscoteca[i], CentroZona(PlantillaPlano.Discoteca, i, n), resaltada, decoraciones[i]());
            }
            else
            {
                var (cx, cyArea) = CentroZona(PlantillaPlano.Discoteca, i, n);
                sb.Append($"<path d=\"{Ruta(AreasDiscoteca[i])}\" class=\"plano-area-vacia\"/>");
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(cx)}\" y=\"{F(cyArea + 4)}\" text-anchor=\"middle\" class=\"plano-nota\">{E(vacias[i])}</text>");
            }
        }

        // Haces de luz desde la cabina del DJ sobre la pista
        foreach (var (x1, x2, color) in new[] { (300, 360, "#a855f7"), (380, 420, "#22d3ee"), (440, 500, "#ec4899") })
            sb.Append($"<polygon points=\"400,58 {x1},300 {x2},300\" fill=\"{color}\" class=\"plano-haz\"/>");

        // Cabina del DJ con sus platos
        sb.Append("<rect x=\"330\" y=\"24\" width=\"140\" height=\"46\" rx=\"8\" class=\"plano-dj\"/>");
        sb.Append("<circle cx=\"362\" cy=\"47\" r=\"12\" class=\"plano-plato\"/><circle cx=\"438\" cy=\"47\" r=\"12\" class=\"plano-plato\"/>");
        sb.Append("<text x=\"400\" y=\"52\" text-anchor=\"middle\" class=\"plano-dj-texto\">DJ</text>");

        // Barras y servicios
        foreach (var x in new[] { 40, 568 })
        {
            sb.Append($"<rect x=\"{x}\" y=\"346\" width=\"192\" height=\"64\" rx=\"10\" class=\"plano-barra\"/>");
            sb.Append($"<rect x=\"{x + 14}\" y=\"356\" width=\"164\" height=\"10\" rx=\"5\" class=\"plano-barra-mostrador\"/>");
            sb.Append($"<text x=\"{x + 96}\" y=\"394\" text-anchor=\"middle\" class=\"plano-nota\">🍸 Barra</text>");
        }
        Servicio(sb, 40, 416, "🚻 Baños", 110);
        Servicio(sb, 650, 416, "🚬 Terraza", 110);
    }

    private static string DecoracionPista() =>
        "<rect x=\"250\" y=\"92\" width=\"300\" height=\"238\" fill=\"url(#af-pista)\" class=\"plano-textura\" opacity=\".55\"/>";

    private static string DecoracionVip()
    {
        var sb = new StringBuilder("<g class=\"plano-mobiliario\">");
        foreach (var k in new[] { 0, 1 })
        {
            var y = 100 + k * 172;
            sb.Append($"<rect x=\"54\" y=\"{y}\" width=\"96\" height=\"14\" rx=\"7\" class=\"plano-sofa\"/><rect x=\"54\" y=\"{y}\" width=\"14\" height=\"44\" rx=\"7\" class=\"plano-sofa\"/>");
            sb.Append($"<circle cx=\"104\" cy=\"{y + 30}\" r=\"9\" class=\"plano-mesa\"/>");
        }
        return sb.Append("</g>").ToString();
    }

    private static string DecoracionBoxes()
    {
        var sb = new StringBuilder("<g class=\"plano-mobiliario\">");
        foreach (var f in new[] { 0, 2 })
            for (var c = 0; c < 2; c++)
            {
                var (x, y) = (584 + c * 92, 108 + f * 72);
                sb.Append($"<path d=\"M{x} {y + 30}V{y + 6}Q{x} {y} {x + 6} {y}H{x + 64}Q{x + 70} {y} {x + 70} {y + 6}V{y + 30}\" class=\"plano-sofa-box\"/>");
                sb.Append($"<circle cx=\"{x + 35}\" cy=\"{y + 26}\" r=\"9\" class=\"plano-mesa\"/>");
            }
        return sb.Append("</g>").ToString();
    }

    private static string DecoracionMezzanine() =>
        "<line x1=\"256\" y1=\"350\" x2=\"544\" y2=\"350\" class=\"plano-baranda\"/>";

    // ---------- Piezas comunes ----------
    private static void Zona(StringBuilder sb, ZonaPlano z, IReadOnlyList<(double X, double Y)> contorno, (double X, double Y) centro,
        int? resaltada, string? decoracion = null, string? rutaSvg = null)
    {
        var agotada = z.Disponibles <= 0;
        var clase = z.Id == resaltada ? "plano-zona plano-zona-tuya" : agotada ? "plano-zona plano-zona-agotada" : "plano-zona";
        sb.Append($"<g class=\"{clase}\" data-zona=\"{z.Id}\">");
        sb.Append($"<path d=\"{rutaSvg ?? Ruta(contorno)}\" fill=\"{(agotada ? "#3f3f46" : z.Color)}\" fill-rule=\"evenodd\" class=\"plano-zona-forma\"/>");
        if (decoracion != null)
            sb.Append(decoracion);
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(centro.X)}\" y=\"{F(centro.Y - 2)}\" text-anchor=\"middle\" class=\"plano-zona-nombre\">{E(z.Nombre)}</text>");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{F(centro.X)}\" y=\"{F(centro.Y + 16)}\" text-anchor=\"middle\" class=\"plano-zona-dato\">{(agotada ? "Agotado" : z.Precio == 0 ? "Gratis" : $"S/ {z.Precio:0.00}")}</text>");
        sb.Append($"<title>{E(z.Nombre)}: {z.Disponibles} de {z.Capacidad} disponibles</title></g>");
    }

    private static string Ruta(IReadOnlyList<(double X, double Y)> p) =>
        "M" + string.Join("L", p.Select(q => string.Create(CultureInfo.InvariantCulture, $"{F(q.X)} {F(q.Y)}"))) + "Z";

    private static void Servicio(StringBuilder sb, int x, int y, string texto, int ancho = 100)
    {
        sb.Append($"<g class=\"plano-servicio\"><rect x=\"{x}\" y=\"{y}\" width=\"{ancho}\" height=\"30\" rx=\"8\"/>");
        sb.Append($"<text x=\"{x + ancho / 2}\" y=\"{y + 20}\" text-anchor=\"middle\">{E(texto)}</text></g>");
    }

    // Las puertas de las zonas se reparten a lo largo del borde inferior
    private static void Puertas(StringBuilder sb, IReadOnlyList<ZonaPlano> zonas, int alto)
    {
        var puertas = zonas.Select(z => z.Puerta?.Trim()).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < puertas.Count; i++)
        {
            var x = (int)(Ancho * (i + 1) / (double)(puertas.Count + 1));
            var ancho = Math.Clamp(44 + puertas[i]!.Length * 8, 116, Ancho / Math.Max(1, puertas.Count) - 12);
            sb.Append($"<g class=\"plano-puerta\"><rect x=\"{x - ancho / 2}\" y=\"{alto - 44}\" width=\"{ancho}\" height=\"30\" rx=\"7\"/>");
            sb.Append($"<text x=\"{x}\" y=\"{alto - 24}\" text-anchor=\"middle\">🚪 {E(puertas[i]!)}</text></g>");
        }
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string E(string texto) => WebUtility.HtmlEncode(texto);
}
