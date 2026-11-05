using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Plano;
using ActivaFest.Services.Recomendaciones;

namespace ActivaFest.Services.Asistente;

public class SolicitudChat
{
    public string Mensaje { get; set; } = "";

    // Evento del que se está hablando (ficha abierta o respuesta anterior)
    public int? EventoId { get; set; }

    // Ubicación del navegador, solo si el usuario la compartió
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }

    // Reserva o compra a medio conversar: el chat la devuelve tal como la recibió en la respuesta anterior
    public BorradorCompra? Pendiente { get; set; }
}

// Lo que ya se sabe de la reserva o compra que se está conversando. Nada se reserva hasta que el usuario confirma,
// y al confirmar el servidor vuelve a validar el evento, la zona y el stock.
public class BorradorCompra
{
    public const string Reservar = "reservar", Comprar = "comprar";

    public string Modo { get; set; } = Reservar;
    public int? EventoId { get; set; }
    public string? Zona { get; set; }
    public int? Cantidad { get; set; }

    // Ya se mostró el resumen: falta el "sí" del usuario
    public bool PorConfirmar { get; set; }

    // Lo preparó el agente de IA en este mismo mensaje: aún no hay nada que responder
    public bool Nuevo { get; set; }
}

public record EnlaceChat(string Texto, string Url);

public class RespuestaChat
{
    public string Texto { get; set; } = "";
    public List<EnlaceChat> Enlaces { get; set; } = new();
    public List<string> Sugerencias { get; set; } = new();
    public string Intencion { get; set; } = Intenciones.Desconocida;

    // Evento al que se refiere la respuesta: el chat lo reenvía para preguntas de seguimiento ("¿y a qué hora?")
    public int? EventoId { get; set; }

    // HU28: plano con la ubicación del usuario resaltada (URL del SVG de su entrada)
    public string? Plano { get; set; }

    // Dashboard de ventas del organizador: el chat lo dibuja como tarjeta con indicadores y barras
    public DashboardChat? Dashboard { get; set; }

    // Administrador: tabla de cifras, gráfico y recomendaciones, iguales a las del panel de analítica
    public TablaCifras? Tabla { get; set; }
    public GraficoSugerido? Grafico { get; set; }
    public List<Recomendacion> Recomendaciones { get; set; } = new();

    // Reserva o compra en curso: el chat la reenvía con el siguiente mensaje
    public BorradorCompra? Pendiente { get; set; }

    // El usuario confirmó una compra: el chat lo lleva al pago
    public string? Redirigir { get; set; }

    // OE3: la consulta se resolvió sin necesidad de un humano
    public bool Resuelta { get; set; }

    // HU14: Llama 3.2 redactó la respuesta (los datos siguen siendo los de la BD)
    public bool RedactadaPorLlm { get; set; }
}

public record IntencionMetrica(string Intencion, int Total, int Resueltas);
public record ConsultaPendiente(DateTime Fecha, string Pregunta, string Intencion);

// HU14 / OE3: el asistente debe resolver sin ayuda humana al menos el 80 % de las consultas
public class MetricasAsistente
{
    public const double Meta = 0.80;

    public int Dias { get; set; }
    public int Total { get; set; }
    public int Resueltas { get; set; }
    public int ConLlm { get; set; }
    public double? Tasa => Total == 0 ? null : (double)Resueltas / Total;
    public bool CumpleMeta => Tasa >= Meta;
    public List<IntencionMetrica> PorIntencion { get; set; } = new();
    public List<ConsultaPendiente> NoResueltas { get; set; } = new();
}

public interface IAsistenteService
{
    Task<RespuestaChat> ResponderAsync(SolicitudChat solicitud, string? usuarioId, CancellationToken ct = default);

    string DetectarIntencion(string mensaje);

    // HU14 / OE3: porcentaje de consultas resueltas sin ayuda humana y preguntas por mejorar
    Task<MetricasAsistente> MetricasAsync(int dias = 30, CancellationToken ct = default);
}

// =============================
// 🤖 ASISTENTE VIRTUAL (HU14 / RF12 / OE3)
// =============================
// Híbrido: palabras clave + modelo ML.NET para entender la pregunta, y respuestas armadas con datos reales
// (base de conocimiento y base de datos). Un LLM (Ollama) es opcional y solo mejora la redacción:
// si no está disponible, el asistente responde igual (funciona en Render y sin conexión al modelo).
public class AsistenteService : IAsistenteService
{
    private const float ConfianzaMinimaMl = 0.30f;

    private static readonly string[] SugerenciasGenerales =
        { "¿Qué eventos hay este fin de semana?", "¿Cómo compro entradas?", "Recomiéndame algo", "¿Dónde veo mis entradas?" };

    private static readonly Dictionary<string, int> SinonimosCategoria = new()
    {
        ["concierto"] = 1, ["conciertos"] = 1, ["musica"] = 1, ["banda"] = 1, ["rock"] = 1, ["jazz"] = 1,
        ["teatro"] = 2, ["obra"] = 2, ["obras"] = 2,
        ["festival"] = 3, ["festivales"] = 3,
        ["feria"] = 4, ["ferias"] = 4,
        ["exposicion"] = 5, ["exposiciones"] = 5, ["expo"] = 5, ["museo"] = 5, ["arte"] = 5,
        ["deporte"] = 6, ["deportes"] = 6, ["partido"] = 6, ["maraton"] = 6,
        ["gastronomia"] = 7, ["comida"] = 7, ["gastronomico"] = 7,
        ["familia"] = 8, ["familias"] = 8, ["familiar"] = 8, ["ninos"] = 8,
        ["cultura"] = 9, ["cultural"] = 9, ["culturales"] = 9
    };

    private readonly ApplicationDbContext _db;
    private readonly MachineLearningService _ml;
    private readonly ICatalogoService _catalogo;
    private readonly IRecomendacionService _recomendaciones;
    private readonly IRedactorLlm _redactor;
    private readonly IUbicacionService _ubicacion;
    private readonly ILogger<AsistenteService> _logger;
    private readonly IAccionesAsistente? _acciones;
    private readonly IAsistenteAnaliticoService? _analitico;

    public AsistenteService(ApplicationDbContext db, MachineLearningService ml, ICatalogoService catalogo,
        IRecomendacionService recomendaciones, IRedactorLlm redactor, IUbicacionService ubicacion, ILogger<AsistenteService> logger,
        IAccionesAsistente? acciones = null, IAsistenteAnaliticoService? analitico = null)
    {
        _db = db;
        _ml = ml;
        _catalogo = catalogo;
        _recomendaciones = recomendaciones;
        _redactor = redactor;
        _ubicacion = ubicacion;
        _logger = logger;
        _acciones = acciones;
        _analitico = analitico;
    }

    // =============================
    // 1️⃣ ENTENDER LA PREGUNTA
    // =============================
    public string DetectarIntencion(string mensaje)
    {
        var texto = BaseConocimiento.Normalizar(mensaje);
        if (texto.Length == 0)
            return Intenciones.Desconocida;

        // "Quiero 2 entradas para…" es un pedido, no una pregunta sobre cómo se compra
        if (PedidoDeEntradas.IsMatch(texto) && !NoEsPedido.IsMatch(texto))
            return Intenciones.Reservar;

        // Palabras clave: una frase más larga pesa más ("cuanto cuesta" > "concierto")
        var puntajes = BaseConocimiento.Entradas
            .Select(e => (e.Intencion, Puntaje: e.PalabrasClave
                .Where(k => BaseConocimiento.Contiene(texto, k))
                .Sum(k => BaseConocimiento.Normalizar(k).Split(' ').Length)))
            // Una orden ("resérvame", "recuérdame") pesa más que las palabras del título ("Festival de Rock")
            .Select(p => (p.Intencion, Puntaje: p.Puntaje > 0 && p.Intencion is Intenciones.Reservar or Intenciones.Recordatorio ? p.Puntaje + 2 : p.Puntaje))
            .Where(p => p.Puntaje > 0)
            .OrderByDescending(p => p.Puntaje)
            .ToList();

        var ml = _ml.Predecir(texto);

        if (puntajes.Count > 0)
        {
            var mejor = puntajes[0].Puntaje;
            var empatadas = puntajes.Where(p => p.Puntaje == mejor).Select(p => p.Intencion).ToList();

            // En caso de empate decide el modelo ML.NET; si no opina sobre ninguna, gana la primera
            return empatadas.Count == 1 || !empatadas.Contains(ml.Intencion) ? empatadas[0] : ml.Intencion;
        }

        // Sin palabras clave: se confía en el modelo si está suficientemente seguro
        return ml.Confianza >= ConfianzaMinimaMl && BaseConocimiento.TieneVocabularioConocido(texto) ? ml.Intencion : Intenciones.Desconocida;
    }

    // =============================
    // 2️⃣ RESPONDER
    // =============================
    // Responde y registra la consulta (diagrama de secuencia de HU14, paso 11: métrica del OE3)
    public async Task<RespuestaChat> ResponderAsync(SolicitudChat solicitud, string? usuarioId, CancellationToken ct = default)
    {
        var respuesta = await ResolverAsync(solicitud, usuarioId, ct);

        var pregunta = (solicitud.Mensaje ?? "").Trim();
        _db.ConsultasAsistente.Add(new ConsultaAsistente
        {
            Fecha = DateTime.UtcNow,
            Pregunta = pregunta.Length > 200 ? pregunta[..200] : pregunta,
            Intencion = respuesta.Intencion,
            Resuelta = respuesta.Resuelta,
            EventoId = respuesta.EventoId,
            ConSesion = usuarioId != null,
            RedactadaPorLlm = respuesta.RedactadaPorLlm
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // La métrica nunca debe impedir que el usuario reciba su respuesta
            _logger.LogWarning(ex, "No se pudo registrar la consulta al asistente");
        }
        return respuesta;
    }

    private static void Redactar(RespuestaChat respuesta, string? redactado)
    {
        if (string.IsNullOrWhiteSpace(redactado))
            return;
        respuesta.Texto = redactado;
        respuesta.RedactadaPorLlm = true;
    }

    public async Task<MetricasAsistente> MetricasAsync(int dias = 30, CancellationToken ct = default)
    {
        var desde = DateTime.UtcNow.AddDays(-Math.Clamp(dias, 1, 365));
        var consultas = _db.ConsultasAsistente.AsNoTracking().Where(c => c.Fecha >= desde);

        var porIntencion = await consultas
            .GroupBy(c => c.Intencion)
            .Select(g => new IntencionMetrica(g.Key, g.Count(), g.Count(c => c.Resuelta)))
            .ToListAsync(ct);

        return new MetricasAsistente
        {
            Dias = dias,
            Total = porIntencion.Sum(i => i.Total),
            Resueltas = porIntencion.Sum(i => i.Resueltas),
            ConLlm = await consultas.CountAsync(c => c.RedactadaPorLlm, ct),
            PorIntencion = porIntencion.OrderByDescending(i => i.Total).ToList(),
            NoResueltas = await consultas.Where(c => !c.Resuelta).OrderByDescending(c => c.Fecha).Take(20)
                .Select(c => new ConsultaPendiente(c.Fecha, c.Pregunta, c.Intencion)).ToListAsync(ct)
        };
    }

    private async Task<RespuestaChat> ResolverAsync(SolicitudChat solicitud, string? usuarioId, CancellationToken ct = default)
    {
        var mensaje = (solicitud.Mensaje ?? "").Trim();
        var intencion = DetectarIntencion(mensaje);
        var texto = BaseConocimiento.Normalizar(mensaje);

        // Reserva en curso: lo que escribe ahora responde a lo último que se le preguntó
        if (solicitud.Pendiente != null && usuarioId != null && _acciones != null)
        {
            var continuacion = await ContinuarCompraAsync(solicitud.Pendiente, texto, intencion, usuarioId, ct);
            if (continuacion != null)
            {
                continuacion.Intencion = Intenciones.Reservar;
                return continuacion;
            }
        }

        // Primero el rol: cada uno solo llega a lo que le corresponde
        var rol = AgenteIA.RolPrincipal(usuarioId == null ? Array.Empty<string>() : await RolesAsync(usuarioId, ct), usuarioId != null);
        var alcance = AlcanceChat.Clasificar(texto, intencion, rol, DateTime.Today);
        if (alcance is AlcanceConsulta.SoloAdministrador or AlcanceConsulta.SoloOrganizadores)
            return AlcanceChat.Denegada(alcance, rol);
        if (alcance is AlcanceConsulta.Analitica or AlcanceConsulta.AnaliticaPropia && _analitico != null)
            return await ConsultaAnaliticaAsync(mensaje, usuarioId!, alcance == AlcanceConsulta.AnaliticaPropia, ct);
        if (rol == Roles.Organizador && AsistenteAnaliticoService.PideGrafico(texto))
            intencion = Intenciones.VentasOrganizador;

        // "¿Cuánto he vendido?" de un administrador sin eventos propios se refiere a toda la plataforma
        if (rol == Roles.Administrador && intencion == Intenciones.VentasOrganizador && _analitico != null
            && !await _db.Eventos.AnyAsync(e => e.OrganizadorId == usuarioId, ct))
            return await ConsultaAnaliticaAsync(mensaje, usuarioId!, false, ct);

        // HU28: "¿dónde está mi asiento?" se responde con las entradas del propio usuario
        if (intencion == Intenciones.MiUbicacion)
        {
            var ubicacion = await MiUbicacionAsync(texto, solicitud.EventoId, usuarioId, ct);
            ubicacion.Intencion = intencion;
            if (ubicacion.Resuelta)
                Redactar(ubicacion, await _redactor.MejorarAsync(mensaje, ubicacion.Texto, ct));
            return ubicacion;
        }

        // Reservas y avisos (usuario), publicaciones y ventas (organizador): datos exactos, sin redacción del LLM
        if (Intenciones.AccionesPorRol.Contains(intencion) && _acciones != null)
        {
            var accion = await AccionPorRolAsync(intencion, texto, solicitud.EventoId, usuarioId, ct);
            accion.Intencion = intencion;
            return accion;
        }

        // Si nombra un evento ("¿cuánto cuesta Rock Fest?"), la pregunta es sobre ese evento
        var evento = await IdentificarEventoAsync(texto, solicitud.EventoId, ct);
        if (evento != null && intencion is Intenciones.BuscarEventos or Intenciones.Desconocida)
            intencion = Intenciones.Instalaciones;

        RespuestaChat respuesta = intencion switch
        {
            Intenciones.BuscarEventos => await BuscarEventosAsync(texto, ct),
            Intenciones.Recomendacion => await RecomendarAsync(usuarioId, solicitud, ct),
            _ when Intenciones.SobreUnEvento.Contains(intencion) => await SobreEventoAsync(intencion, evento, ct),
            Intenciones.Desconocida => NoEntendida(rol),
            _ when Intenciones.AccionesPorRol.Contains(intencion) => NoEntendida(rol),
            _ => DesdeBaseConocimiento(intencion)
        };

        respuesta.Intencion = intencion;
        respuesta.EventoId ??= evento?.Id;

        // Redacción más natural con el LLM si está disponible; los datos no cambian
        if (respuesta.Resuelta && intencion is not (Intenciones.Saludo or Intenciones.Agradecimiento))
            Redactar(respuesta, await _redactor.MejorarAsync(mensaje, respuesta.Texto, ct));

        return respuesta;
    }

    private static RespuestaChat DesdeBaseConocimiento(string intencion)
    {
        var entrada = BaseConocimiento.Buscar(intencion)!;
        return new RespuestaChat
        {
            Texto = entrada.Respuesta ?? "",
            Enlaces = (entrada.Enlaces ?? Array.Empty<string>())
                .Select(e => e.Split('|'))
                .Select(p => new EnlaceChat(p[0], p[1]))
                .ToList(),
            Sugerencias = (entrada.Sugerencias ?? Array.Empty<string>()).ToList(),
            Resuelta = true
        };
    }

    private static RespuestaChat NoEntendida(string rol) => new()
    {
        Texto = "No estoy seguro de haber entendido 🤔. " + rol switch
        {
            Roles.Administrador => "Puedo consultar eventos, ventas, comisiones y compradores de toda la plataforma, generar gráficos y darte recomendaciones.",
            Roles.Organizador => "Puedo mostrarte tu dashboard de ventas y el estado de tus eventos, y resolver dudas sobre compras y entradas.",
            Roles.Asistente or Roles.ControlAcceso => "Puedo reservarte entradas, mostrarte tu carrito y tus compras, y ayudarte a buscar eventos.",
            _ => "Puedo ayudarte a buscar eventos, decirte dónde quedan, a qué hora empiezan o cuánto cuestan, y resolver dudas sobre compras y entradas (prueba con las preguntas frecuentes de abajo)."
        } + " Si tu duda es sobre un evento en particular, ábrelo y pregúntame desde su ficha, o escribe a soporte@activafest.com con el nombre del evento y te ponemos en contacto con su organizador.",
        Sugerencias = rol == "Visitante" ? SugerenciasGenerales.ToList() : AgenteIA.SugerenciasPara(rol),
        Resuelta = false
    };

    // El chat responde igual que el panel de analítica (mismas cifras, tabla, gráfico y auditoría).
    // soloPropios: organizador, con los datos de sus eventos; si no, administrador, con toda la plataforma.
    private async Task<RespuestaChat> ConsultaAnaliticaAsync(string mensaje, string usuarioId, bool soloPropios, CancellationToken ct)
    {
        var r = soloPropios
            ? await _analitico!.ConsultarMisEventosAsync(mensaje, usuarioId, ct)
            : await _analitico!.ConsultarAsync(mensaje, usuarioId, ct);
        return new RespuestaChat
        {
            Texto = r.Texto,
            Tabla = r.Tabla,
            Grafico = r.Grafico is { Valores.Count: > 0 } ? r.Grafico : null,
            Recomendaciones = r.Recomendaciones,
            Sugerencias = r.Sugerencias,
            EventoId = r.EventoId,
            Enlaces = new() { soloPropios ? new EnlaceChat("📊 Abrir mi panel de ventas", "/Panel") : new EnlaceChat("📊 Abrir el dashboard", "/Analitica") },
            Intencion = soloPropios ? Intenciones.ConsultaMisEventos : Intenciones.ConsultaPlataforma,
            Resuelta = !r.FueraDeAlcance
        };
    }

    // ---------- Eventos ----------
    private async Task<Evento?> IdentificarEventoAsync(string texto, int? eventoContexto, CancellationToken ct)
    {
        var ahora = DateTime.Now.AddHours(-Evento.HorasValidezEntradas);
        var eventos = await _db.Eventos.AsNoTracking()
            .Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha >= ahora)
            .Select(e => new EventoCandidato(e.Id, e.Titulo, e.Fecha))
            .ToListAsync(ct);

        var reconocido = ReconocedorEventos.Reconocer(texto, eventos);
        var id = reconocido.Encontrado?.Id ?? reconocido.Opciones.FirstOrDefault()?.Id ?? eventoContexto;
        if (id == null)
            return null;

        return await _db.Eventos.AsNoTracking()
            .Include(e => e.TiposEntrada)
            .Include(e => e.Categoria)
            .Include(e => e.Imagenes)
            .Include(e => e.Organizador)
            .AsSplitQuery()
            .FirstOrDefaultAsync(e => e.Id == id && e.Estado == EstadoEvento.Aprobado, ct);
    }

    private async Task<RespuestaChat> SobreEventoAsync(string intencion, Evento? evento, CancellationToken ct)
    {
        if (evento == null)
        {
            // Pide aclarar con los próximos eventos como sugerencias
            var proximos = await _catalogo.BuscarAsync(new CatalogoFiltro(), ct);
            return new RespuestaChat
            {
                Texto = "¿Sobre qué evento me preguntas? Escribe su nombre o ábrelo en el catálogo y pregúntame desde su ficha.",
                Sugerencias = proximos.Eventos.Take(3).Select(e => $"{PreguntaPara(intencion)} {e.Titulo}?").ToList(),
                Enlaces = new() { new EnlaceChat("Ver eventos", "/Eventos") },
                Resuelta = false
            };
        }

        var culture = CultureInfo.GetCultureInfo("es-PE");
        var inv = CultureInfo.InvariantCulture;
        var ficha = new EnlaceChat($"Ver {evento.Titulo}", $"/Eventos/Detalle/{evento.Id}");
        var pasado = evento.YaOcurrio(DateTime.Now);

        var respuesta = new RespuestaChat { EventoId = evento.Id, Resuelta = true };

        switch (intencion)
        {
            case Intenciones.Ubicacion:
                respuesta.Texto = $"📍 «{evento.Titulo}» será en {evento.Ubicacion}. En la ficha del evento tienes el mapa y el botón para trazar la ruta desde tu ubicación.";
                respuesta.Enlaces.Add(new EnlaceChat("Ver mapa y ruta", $"/Eventos/Detalle/{evento.Id}"));
                if (evento.TieneUbicacion)
                    respuesta.Enlaces.Add(new EnlaceChat("Abrir en Google Maps",
                        $"https://www.google.com/maps/dir/?api=1&destination={evento.Latitud!.Value.ToString(inv)},{evento.Longitud!.Value.ToString(inv)}"));
                respuesta.Sugerencias.AddRange(new[] { "¿A qué hora empieza?", "¿Cuánto cuesta?" });
                break;

            case Intenciones.Horario:
                respuesta.Texto = pasado
                    ? $"«{evento.Titulo}» fue el {Formato.Fecha(evento.Fecha, "dddd dd 'de' MMMM")} a las {evento.Fecha:HH:mm}; ese evento ya se realizó."
                    : $"📅 «{evento.Titulo}» es el {Formato.Fecha(evento.Fecha, "dddd dd 'de' MMMM 'de' yyyy").ToLower(culture)} a las {evento.Fecha:HH:mm}. " +
                      $"El ingreso con QR se habilita desde las {evento.InicioIngreso:HH:mm}" +
                      (evento.InicioIngreso.Date != evento.Fecha.Date ? $" del {evento.InicioIngreso:dd/MM}" : "") + ".";
                respuesta.Enlaces.Add(ficha);
                respuesta.Sugerencias.AddRange(new[] { "¿Dónde queda?", "¿Cuánto cuesta?" });
                break;

            case Intenciones.Precio:
                var zonas = evento.TiposEntrada.OrderBy(t => t.Precio).ToList();
                if (zonas.Count == 0)
                {
                    respuesta.Texto = $"El organizador de «{evento.Titulo}» aún no publicó los precios.";
                }
                else
                {
                    var lineas = zonas.Select(z => $"• {z.Nombre}: {Formato.Precio(z.Precio)}" +
                        (z.Disponibles == 0 ? " (agotado)" : z.Disponibles <= 10 ? $" (¡quedan {z.Disponibles}!)" : ""));
                    var agotado = zonas.All(z => z.Disponibles == 0);
                    respuesta.Texto = $"🎟️ Entradas para «{evento.Titulo}»:\n{string.Join("\n", lineas)}" +
                                      (agotado ? "\nLas entradas están agotadas." : pasado ? "" : "\nPuedes comprarlas en la ficha del evento.");
                }
                respuesta.Enlaces.Add(new EnlaceChat(pasado ? $"Ver {evento.Titulo}" : "Comprar entradas", $"/Eventos/Detalle/{evento.Id}#comprar"));
                respuesta.Sugerencias.AddRange(new[] { "¿Cómo compro entradas?", "¿A qué hora empieza?" });
                break;

            default: // Instalaciones / información general del evento
                var descripcion = evento.Descripcion.Length > 220 ? evento.Descripcion[..220].TrimEnd() + "…" : evento.Descripcion;
                respuesta.Texto = $"«{evento.Titulo}» ({evento.Categoria?.Nombre}) · 📍 {evento.Ubicacion} · 📅 {evento.Fecha:dd/MM} a las {evento.Fecha:HH:mm}" +
                                  (evento.Aforo > 0 ? $" · 👥 aforo de {evento.Aforo:N0} personas" : "") + "." +
                                  (descripcion.Length > 0 ? $"\n{descripcion}" : "") +
                                  (evento.Imagenes.Count > 0 ? $"\nEn la ficha hay {evento.Imagenes.Count} fotos del recinto." : "") +
                                  $"\nPara detalles como estacionamiento o accesibilidad, revisa la descripción o consulta a su organizador{(evento.Organizador != null ? $", {evento.Organizador.NombreCompleto}," : "")} escribiendo a soporte@activafest.com.";
                respuesta.Enlaces.Add(ficha);
                respuesta.Sugerencias.AddRange(new[] { "¿Dónde queda?", "¿Cuánto cuesta?", "¿A qué hora empieza?" });
                break;
        }

        return respuesta;
    }

    private static string PreguntaPara(string intencion) => intencion switch
    {
        Intenciones.Ubicacion => "¿Dónde queda",
        Intenciones.Horario => "¿A qué hora empieza",
        Intenciones.Precio => "¿Cuánto cuesta",
        _ => "¿Qué sabes de"
    };

    // ---------- Búsqueda ----------
    private async Task<RespuestaChat> BuscarEventosAsync(string texto, CancellationToken ct)
    {
        var filtro = InterpretarBusqueda(texto, DateTime.Today);
        var resultado = await _catalogo.BuscarAsync(filtro, ct);

        // Si el texto libre no dio resultados, se reintenta solo con los filtros reconocidos
        if (resultado.Total == 0 && filtro.Buscar != null)
        {
            filtro.Buscar = null;
            resultado = await _catalogo.BuscarAsync(filtro, ct);
        }

        if (resultado.Total == 0)
        {
            return new RespuestaChat
            {
                Texto = "No encontré eventos próximos con esas características 😕. Prueba con otra fecha o categoría, o mira todo el catálogo.",
                Enlaces = new() { new EnlaceChat("Ver todos los eventos", "/Eventos") },
                Sugerencias = new() { "Recomiéndame algo", "¿Hay eventos gratis?" },
                Resuelta = true
            };
        }

        var top = resultado.Eventos.Take(3).ToList();
        return new RespuestaChat
        {
            Texto = $"Encontré {resultado.Total} {(resultado.Total == 1 ? "evento" : "eventos")}." + (resultado.Total > 3 ? " Estos son los más próximos:" : "") + "\n" +
                    string.Join("\n", top.Select(e => $"• {e.Titulo} — {Formato.Fecha(e.Fecha, "ddd dd MMM, HH:mm")} · {e.Ubicacion} · {Formato.PrecioDesde(e.PrecioDesde)}")),
            Enlaces = top.Select(e => new EnlaceChat(e.Titulo, $"/Eventos/Detalle/{e.Id}"))
                .Append(new EnlaceChat("Ver todos con estos filtros", UrlCatalogo(filtro)))
                .ToList(),
            EventoId = top.Count == 1 ? top[0].Id : null,
            Resuelta = true
        };
    }

    // Convierte lenguaje natural en filtros del catálogo: categoría, fechas y precio
    public static CatalogoFiltro InterpretarBusqueda(string textoNormalizado, DateTime hoy)
    {
        var filtro = new CatalogoFiltro();
        var palabras = textoNormalizado.Split(' ');

        var categoria = palabras.Select(p => SinonimosCategoria.TryGetValue(p, out var id) ? id : (int?)null).FirstOrDefault(c => c.HasValue);
        filtro.CategoriaId = categoria;

        if (BaseConocimiento.Contiene(textoNormalizado, "hoy"))
        {
            filtro.Desde = hoy; filtro.Hasta = hoy;
        }
        else if (BaseConocimiento.Contiene(textoNormalizado, "manana"))
        {
            filtro.Desde = hoy.AddDays(1); filtro.Hasta = hoy.AddDays(1);
        }
        else if (BaseConocimiento.Contiene(textoNormalizado, "fin de semana") || BaseConocimiento.Contiene(textoNormalizado, "finde"))
        {
            // Sábado y domingo próximos (si hoy es domingo, solo hoy)
            var sabado = hoy.DayOfWeek == DayOfWeek.Sunday ? hoy : hoy.AddDays(((int)DayOfWeek.Saturday - (int)hoy.DayOfWeek + 7) % 7);
            filtro.Desde = sabado;
            filtro.Hasta = hoy.DayOfWeek == DayOfWeek.Sunday ? hoy : sabado.AddDays(1);
        }
        else if (BaseConocimiento.Contiene(textoNormalizado, "esta semana"))
        {
            // Hasta el domingo de esta semana
            filtro.Desde = hoy; filtro.Hasta = hoy.AddDays((7 - (int)hoy.DayOfWeek) % 7);
        }
        else if (BaseConocimiento.Contiene(textoNormalizado, "este mes"))
        {
            filtro.Desde = hoy; filtro.Hasta = new DateTime(hoy.Year, hoy.Month, DateTime.DaysInMonth(hoy.Year, hoy.Month));
        }

        if (BaseConocimiento.Contiene(textoNormalizado, "gratis") || BaseConocimiento.Contiene(textoNormalizado, "gratuito") || BaseConocimiento.Contiene(textoNormalizado, "gratuitos"))
            filtro.SoloGratis = true;
        else if (BaseConocimiento.Contiene(textoNormalizado, "barato") || BaseConocimiento.Contiene(textoNormalizado, "baratos") || BaseConocimiento.Contiene(textoNormalizado, "economico"))
            filtro.Orden = OrdenCatalogo.PrecioAsc;

        // "en arequipa" → texto libre sobre la ubicación
        var indiceEn = Array.LastIndexOf(palabras, "en");
        if (indiceEn >= 0 && indiceEn < palabras.Length - 1)
        {
            var lugar = palabras[indiceEn + 1];
            if (lugar.Length > 2 && !SinonimosCategoria.ContainsKey(lugar) && lugar is not ("la" or "el" or "los" or "las"))
                filtro.Buscar = lugar;
        }

        return filtro;
    }

    private static string UrlCatalogo(CatalogoFiltro f)
    {
        var partes = f.RutaConPagina(1).Where(p => p.Key != "Pagina").Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}");
        var query = string.Join("&", partes);
        return query.Length == 0 ? "/Eventos" : $"/Eventos?{query}";
    }

    // =============================
    // 🧭 ¿DÓNDE ESTÁ MI ASIENTO? (HU28)
    // =============================
    // La ubicación sale de la función de Semantic Kernel ObtenerUbicacionDeMiEntrada (solo entradas del usuario
    // autenticado). Tres caminos, como en el diagrama: una entrada, varias para esa fecha o ninguna.
    private async Task<RespuestaChat> MiUbicacionAsync(string texto, int? eventoContexto, string? usuarioId, CancellationToken ct)
    {
        if (usuarioId == null)
        {
            return new RespuestaChat
            {
                Texto = "Para decirte dónde está tu asiento necesito ver tus entradas: inicia sesión con la cuenta con la que compraste y vuelve a preguntarme.",
                Enlaces = new() { new EnlaceChat("Iniciar sesión", "/Auth/Login") },
                Resuelta = true
            };
        }

        var fecha = FechaMencionada(texto, DateTime.Today);
        var kernel = PluginUbicacion.KernelPara(_ubicacion, usuarioId);
        var argumentos = new Microsoft.SemanticKernel.KernelArguments();
        if (fecha != null)
            argumentos["fecha"] = fecha.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var resultado = await kernel.InvokeAsync(PluginUbicacion.NombrePlugin, PluginUbicacion.NombreFuncion, argumentos, ct);
        var entradas = resultado.GetValue<List<UbicacionMiEntrada>>() ?? new();

        // Si nombra un evento, solo ese; si pregunta desde la ficha de un evento para el que tiene entradas, ese
        var nombrado = await IdentificarEventoAsync(texto, null, ct);
        if (nombrado != null)
            entradas = entradas.Where(e => e.EventoId == nombrado.Id).ToList();
        else if (eventoContexto != null && entradas.Any(e => e.EventoId == eventoContexto))
            entradas = entradas.Where(e => e.EventoId == eventoContexto).ToList();

        if (entradas.Count == 0)
        {
            var cuando = fecha == null ? "" : fecha == DateTime.Today ? " para hoy" : $" para el {Formato.Fecha(fecha.Value, "dddd dd 'de' MMMM")}";
            return new RespuestaChat
            {
                Texto = $"No encontré entradas activas{cuando}{(nombrado != null ? $" para «{nombrado.Titulo}»" : "")} en tu cuenta. " +
                        "Si ya compraste, revisa que hayas iniciado sesión con la misma cuenta; si no, puedes buscar un evento en el catálogo.",
                Enlaces = new() { new EnlaceChat("Mis entradas", "/Entradas"), new EnlaceChat("Ver eventos", "/Eventos") },
                Resuelta = true
            };
        }

        var eventos = entradas.GroupBy(e => e.EventoId).ToList();
        if (eventos.Count > 1)
        {
            // Varias entradas para esa fecha (o próximas): pregunta a cuál se refiere
            return new RespuestaChat
            {
                Texto = $"Tienes entradas para {eventos.Count} eventos{(fecha != null ? " ese día" : "")}. ¿A cuál te refieres?\n" +
                        string.Join("\n", eventos.Select(g => $"• {g.First().Evento} — {Formato.Fecha(g.First().Fecha, "ddd dd MMM, HH:mm")}")),
                Sugerencias = eventos.Take(4).Select(g => $"¿Dónde está mi asiento para {g.First().Evento}?").ToList(),
                Resuelta = false
            };
        }

        var mias = eventos[0].ToList();
        var primera = mias[0];
        var lineas = new List<string>
        {
            $"🎟️ {(mias.Count == 1 ? "Tu entrada" : $"Tus {mias.Count} entradas")} para «{primera.Evento}» ({Formato.Fecha(primera.Fecha, "ddd dd/MM, HH:mm")}, {primera.Recinto}):"
        };
        lineas.AddRange(mias.Take(10).Select(e => $"• {e.Resumen}"));
        lineas.Add(mias.Count == 1 ? "Cómo llegar:" : $"Cómo llegar a {(primera.Asiento != null ? $"la fila {primera.Fila}, asiento {primera.Asiento}" : $"la zona {primera.Zona}")}:");
        lineas.AddRange(primera.Indicaciones.Select((p, i) => $"{i + 1}. {p}"));

        return new RespuestaChat
        {
            Texto = string.Join("\n", lineas),
            EventoId = primera.EventoId,
            Plano = $"/Entradas/Plano/{primera.EntradaId}",
            Enlaces = new() { new EnlaceChat("Ver mi entrada", "/Entradas"), new EnlaceChat("Ver mapa y ruta al recinto", $"/Eventos/Detalle/{primera.EventoId}") },
            Sugerencias = new() { "¿A qué hora empieza?", "¿Cómo ingreso con el QR?" },
            Resuelta = true
        };
    }

    // "hoy", "mañana", "pasado mañana", "el sábado" o "28/11" → fecha; null si no menciona ninguna
    public static DateTime? FechaMencionada(string textoNormalizado, DateTime hoy)
    {
        if (BaseConocimiento.Contiene(textoNormalizado, "pasado manana"))
            return hoy.AddDays(2);
        if (BaseConocimiento.Contiene(textoNormalizado, "manana"))
            return hoy.AddDays(1);
        if (BaseConocimiento.Contiene(textoNormalizado, "hoy") || BaseConocimiento.Contiene(textoNormalizado, "esta noche"))
            return hoy;

        var dias = new[] { "domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado" };
        for (var d = 0; d < dias.Length; d++)
            if (BaseConocimiento.Contiene(textoNormalizado, dias[d]))
                return hoy.AddDays(((d - (int)hoy.DayOfWeek) + 7) % 7);

        // "28 11" (la normalización convierte "28/11" en "28 11")
        var m = Regex.Match(textoNormalizado, @"\b(\d{1,2}) (\d{1,2})\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var dia) && int.TryParse(m.Groups[2].Value, out var mes) && mes is >= 1 and <= 12 && dia >= 1 && dia <= DateTime.DaysInMonth(hoy.Year, mes))
        {
            var f = new DateTime(hoy.Year, mes, dia);
            return f < hoy ? f.AddYears(1) : f;
        }
        return null;
    }

    // ---------- Recomendación ----------
    private async Task<RespuestaChat> RecomendarAsync(string? usuarioId, SolicitudChat solicitud, CancellationToken ct)
    {
        var ubicacion = solicitud.Latitud.HasValue && solicitud.Longitud.HasValue ? new Ubicacion(solicitud.Latitud.Value, solicitud.Longitud.Value) : null;
        var recomendados = await _recomendaciones.RecomendarAsync(usuarioId, ubicacion, 3, ct);

        if (recomendados.Count == 0)
        {
            return new RespuestaChat
            {
                Texto = "Por ahora no hay eventos próximos para recomendarte. ¡Vuelve pronto!",
                Enlaces = new() { new EnlaceChat("Ver catálogo", "/Eventos") },
                Resuelta = true
            };
        }

        var intro = usuarioId == null
            ? "Te recomiendo estos eventos (inicia sesión para recomendaciones según tus gustos):"
            : "Según lo que te gusta, te recomiendo:";

        return new RespuestaChat
        {
            Texto = intro + "\n" + string.Join("\n", recomendados.Select(e =>
                $"• {e.Titulo} — {Formato.Fecha(e.Fecha, "ddd dd MMM")} · {Formato.PrecioDesde(e.PrecioDesde)} ({e.Motivo})")),
            Enlaces = recomendados.Select(e => new EnlaceChat(e.Titulo, $"/Eventos/Detalle/{e.Id}")).ToList(),
            Resuelta = true
        };
    }

    // =============================
    // ⚡ ACCIONES POR ROL
    // =============================
    private static readonly string[] VerbosQuitar = { "quitar", "quita", "quitame", "cancelar", "cancela", "desactivar", "desactiva", "eliminar", "elimina", "borra", "ya no" };

    private static readonly Dictionary<string, int> NumerosEnTexto = new()
    {
        ["un"] = 1, ["una"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5,
        ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8, ["nueve"] = 9, ["diez"] = 10
    };

    private async Task<RespuestaChat> AccionPorRolAsync(string intencion, string texto, int? eventoContexto, string? usuarioId, CancellationToken ct)
    {
        if (usuarioId == null)
        {
            var paraQue = intencion switch
            {
                Intenciones.Reservar => "reservar entradas",
                Intenciones.MiCarrito => "mostrarte tu carrito",
                Intenciones.Recordatorio or Intenciones.MisAvisos => "avisarte de tus eventos",
                _ => "mostrarte tus eventos y ventas"
            };
            return new RespuestaChat
            {
                Texto = $"Para {paraQue} necesito saber quién eres: inicia sesión y vuelve a pedírmelo.",
                Enlaces = new() { new EnlaceChat("Iniciar sesión", "/Auth/Login"), new EnlaceChat("Crear cuenta", "/Auth/Registro") },
                Resuelta = true
            };
        }

        if (intencion is Intenciones.EstadoMisEventos or Intenciones.VentasOrganizador)
        {
            var roles = await RolesAsync(usuarioId, ct);
            if (!roles.Contains(Roles.Organizador) && !roles.Contains(Roles.Administrador))
            {
                return new RespuestaChat
                {
                    Texto = "Esa información es para organizadores: ahí ves el estado de tus eventos publicados y tus ventas. Si quieres vender entradas de tu propio evento, solicita el perfil de organizador.",
                    Enlaces = new() { new EnlaceChat("Organiza tu evento", "/Organizador/Solicitar") },
                    Sugerencias = new() { "Mis avisos", "¿Dónde veo mis entradas?" },
                    Resuelta = true
                };
            }
            // El administrador (sin eventos propios) pregunta por la moderación de toda la plataforma
            if (intencion == Intenciones.EstadoMisEventos && roles.Contains(Roles.Administrador) && !roles.Contains(Roles.Organizador)
                && !await _db.Eventos.AnyAsync(e => e.OrganizadorId == usuarioId, ct))
            {
                var pendientes = await _db.Eventos.CountAsync(e => e.Estado == EstadoEvento.Pendiente && !e.PendientePagoPublicacion, ct);
                return new RespuestaChat
                {
                    Texto = pendientes == 0
                        ? "✅ No hay eventos esperando aprobación en la plataforma."
                        : $"⏳ Hay {pendientes} {(pendientes == 1 ? "evento esperando" : "eventos esperando")} tu aprobación. Puedes revisarlos en Administración.",
                    Enlaces = new() { new EnlaceChat("Moderar eventos", "/Admin") },
                    Resuelta = true
                };
            }

            return intencion == Intenciones.EstadoMisEventos
                ? await EstadoMisEventosAsync(usuarioId, ct)
                : await VentasOrganizadorAsync(texto, eventoContexto, usuarioId, ct);
        }

        return intencion switch
        {
            Intenciones.Reservar => await IniciarCompraAsync(texto, eventoContexto, usuarioId, ct),
            Intenciones.MiCarrito => await MiCarritoAsync(usuarioId, ct),
            Intenciones.Recordatorio => await RecordatorioAsync(texto, eventoContexto, usuarioId, ct),
            _ => await MisAvisosAsync(usuarioId, ct)
        };
    }

    private Task<List<string>> RolesAsync(string usuarioId, CancellationToken ct) =>
        (from ur in _db.UserRoles
         join r in _db.Roles on ur.RoleId equals r.Id
         where ur.UserId == usuarioId
         select r.Name!).ToListAsync(ct);

    // "2 entradas", "tres boletos", "para 4 personas" → cantidad; null si no menciona ninguna
    public static int? CantidadMencionada(string textoNormalizado)
    {
        var m = Regex.Match(textoNormalizado,
            @"\b(\d{1,2}|un|una|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez)( [a-z]+)? (entradas?|boletos?|tickets?|personas?|pases?|lugares?)\b");
        if (!m.Success)
            return null;
        var valor = m.Groups[1].Value;
        return int.TryParse(valor, out var n) ? n : NumerosEnTexto[valor];
    }

    private async Task<RespuestaChat> PedirEventoAsync(string pregunta, Func<string, string> sugerencia, CancellationToken ct)
    {
        var proximos = await _catalogo.BuscarAsync(new CatalogoFiltro(), ct);
        return new RespuestaChat
        {
            Texto = pregunta,
            Sugerencias = proximos.Eventos.Take(3).Select(e => sugerencia(e.Titulo)).ToList(),
            Enlaces = new() { new EnlaceChat("Ver eventos", "/Eventos") },
            Resuelta = false
        };
    }

    // =============================
    // 🎟️ RESERVAR O COMPRAR CONVERSANDO
    // =============================
    // El asistente pregunta solo lo que falta (evento y tipo de entrada), muestra un resumen con el total y no hace
    // nada hasta que el usuario lo confirma. Pagar siempre ocurre en la pasarela, nunca dentro del chat.
    private static readonly Regex PedidoDeEntradas = new(@"^(?:hola )?(?:yo )?(?:quiero|quisiera|deseo|necesito|dame|me das|consigueme|comprame|vendeme)\b.*\b(?:entradas?|boletos?|tickets?|pases?)\b", RegexOptions.Compiled);
    private static readonly Regex NoEsPedido = new(@"\b(?:mis|cuanto|cuantas|precio|precios|como|donde|cuando|saber|ver|devolver|cancelar|transferir|regalar|vender|imprimir|descargar|reenviar)\b", RegexOptions.Compiled);
    private static readonly Regex NumeroSuelto = new(@"^(?:(?:son|serian|mejor|solo|quiero) )?(\d{1,2}|un|una|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez)(?: por favor)?$", RegexOptions.Compiled);

    private static readonly string[] Afirmaciones =
        { "si", "claro", "dale", "ok", "okay", "confirmo", "confirmar", "confirma", "adelante", "hazlo", "listo", "perfecto", "de acuerdo", "va", "vale", "correcto", "procede", "por supuesto" };

    private static readonly string[] Negaciones =
        { "cancelar", "cancela", "cancelalo", "cancelala", "olvidalo", "olvidala", "dejalo", "dejala", "mejor no", "ya no", "no gracias", "no quiero", "detente", "para" };

    private static readonly string[] VerbosComprar = { "comprar", "compra", "comprame", "comprarme", "comprarlas", "comprarla", "pagar", "pagarlas", "pagarla", "pago" };
    private static readonly string[] VerbosReservar = { "reservar", "reserva", "reservame", "reservarme", "reservalas", "reservala", "reservalo", "apartar", "aparta", "apartame", "separar", "separa", "separame" };

    private static bool EsNegacion(string t) =>
        t == "no" || (t.StartsWith("no ") && t.Split(' ').Length <= 4) || Negaciones.Any(n => BaseConocimiento.Contiene(t, n));

    private static bool EsAfirmacion(string t) => Afirmaciones.Any(a => BaseConocimiento.Contiene(t, a));

    private static string? ModoPedido(string t) =>
        VerbosComprar.Any(v => BaseConocimiento.Contiene(t, v)) ? BorradorCompra.Comprar
        : VerbosReservar.Any(v => BaseConocimiento.Contiene(t, v)) ? BorradorCompra.Reservar
        : null;

    private static int? CantidadSuelta(string t)
    {
        var m = NumeroSuelto.Match(t);
        if (!m.Success)
            return null;
        return int.TryParse(m.Groups[1].Value, out var n) ? n : NumerosEnTexto[m.Groups[1].Value];
    }

    private async Task<List<EventoCandidato>> EventosAVentaAsync(CancellationToken ct)
    {
        var ahora = DateTime.Now.AddHours(-Evento.HorasValidezEntradas);
        return await _db.Eventos.AsNoTracking()
            .Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha >= ahora)
            .Select(e => new EventoCandidato(e.Id, e.Titulo, e.Fecha))
            .ToListAsync(ct);
    }

    private async Task<RespuestaChat> IniciarCompraAsync(string texto, int? eventoContexto, string usuarioId, CancellationToken ct)
    {
        var b = new BorradorCompra { Modo = ModoPedido(texto) ?? BorradorCompra.Reservar, Cantidad = CantidadMencionada(texto) };

        var reconocido = ReconocedorEventos.Reconocer(texto, await EventosAVentaAsync(ct));
        if (reconocido.Ambiguo)
            return ElegirEvento(b, reconocido.Opciones);

        var nombrado = reconocido.NoEncontrado ?? (eventoContexto == null ? reconocido.Mencion : null);
        if (reconocido.Encontrado == null && nombrado != null)
            return await PedirEventoCompraAsync(b, $"No encontré «{nombrado}» entre los próximos eventos. ", ct);

        b.EventoId = reconocido.Encontrado?.Id ?? eventoContexto;
        return b.EventoId == null ? await PedirEventoCompraAsync(b, "", ct) : await AvanzarCompraAsync(b, texto, usuarioId, ct);
    }

    private async Task<RespuestaChat?> ContinuarCompraAsync(BorradorCompra b, string texto, string intencion, string usuarioId, CancellationToken ct)
    {
        if (b.Nuevo)
        {
            b.Nuevo = false;
            return b.EventoId == null ? await PedirEventoCompraAsync(b, "", ct) : await AvanzarCompraAsync(b, texto, usuarioId, ct);
        }

        if (EsNegacion(texto))
            return new RespuestaChat
            {
                Texto = "Listo, no reservé nada. Cuando quieras retomamos.",
                Sugerencias = new() { "Quiero reservar una entrada", "¿Qué eventos hay este fin de semana?", "Ver mi carrito" },
                Resuelta = true
            };

        var sigueLaCompra = intencion is Intenciones.Desconocida or Intenciones.Reservar;
        var eventos = await EventosAVentaAsync(ct);

        // Le acabamos de preguntar el evento: todo lo que escribió es su nombre
        if (b.EventoId == null)
        {
            var respuesta = ReconocedorEventos.Reconocer(texto, eventos, comoNombre: true);
            if (respuesta.Ambiguo)
                return ElegirEvento(b, respuesta.Opciones);
            if (respuesta.Encontrado == null)
                return sigueLaCompra ? await PedirEventoCompraAsync(b, $"No encontré «{respuesta.NoEncontrado ?? texto}» entre los próximos eventos. ", ct) : null;

            b.EventoId = respuesta.Encontrado.Id;
            b.Cantidad ??= CantidadMencionada(texto);
            return await AvanzarCompraAsync(b, texto, usuarioId, ct);
        }

        // Cambió de opinión: otro evento, otra zona u otra cantidad. Se vuelve a mostrar el resumen
        var otroEvento = ReconocedorEventos.Reconocer(texto, eventos).Encontrado;
        if (otroEvento != null && otroEvento.Id != b.EventoId)
        {
            (b.EventoId, b.Zona, b.PorConfirmar) = (otroEvento.Id, null, false);
            return await AvanzarCompraAsync(b, texto, usuarioId, ct);
        }

        var zonas = await _db.TiposEntrada.AsNoTracking().Where(t => t.EventoId == b.EventoId).Select(t => t.Nombre).ToListAsync(ct);
        var zona = zonas.Where(n => BaseConocimiento.Contiene(texto, n)).OrderByDescending(n => n.Length).FirstOrDefault();
        var cantidad = CantidadMencionada(texto) ?? CantidadSuelta(texto);
        if ((zona != null && zona != b.Zona) || (cantidad != null && cantidad != b.Cantidad))
        {
            b.Zona = zona ?? b.Zona;
            b.Cantidad = cantidad ?? b.Cantidad;
            b.PorConfirmar = false;
            return await AvanzarCompraAsync(b, texto, usuarioId, ct);
        }

        if (b.PorConfirmar && (EsAfirmacion(texto) || ModoPedido(texto) != null))
        {
            b.Modo = ModoPedido(texto) ?? b.Modo;
            return await EjecutarCompraAsync(b, usuarioId, ct);
        }

        // Hablar de otra cosa deja la reserva de lado; si no se entendió, se vuelve a preguntar lo que falta
        return sigueLaCompra ? await AvanzarCompraAsync(b, "", usuarioId, ct) : null;
    }

    private async Task<RespuestaChat> PedirEventoCompraAsync(BorradorCompra b, string aviso, CancellationToken ct)
    {
        var proximos = await _catalogo.BuscarAsync(new CatalogoFiltro(), ct);
        var verbo = b.Modo == BorradorCompra.Comprar ? "comprar" : "reservar";
        return new RespuestaChat
        {
            Texto = $"{aviso}¿Para qué evento quieres {verbo}? Elige uno o escribe su nombre.",
            Sugerencias = proximos.Eventos.Where(e => e.PrecioDesde > 0).Take(4).Select(e => e.Titulo).ToList(),
            Enlaces = new() { new EnlaceChat("Ver eventos", "/Eventos") },
            Pendiente = b,
            Resuelta = false
        };
    }

    private static RespuestaChat ElegirEvento(BorradorCompra b, List<EventoCandidato> opciones) => new()
    {
        Texto = "Encontré varios eventos con ese nombre. ¿Cuál quieres?\n" +
                string.Join("\n", opciones.Select(e => $"• {e.Titulo} — {Formato.Fecha(e.Fecha, "ddd dd MMM, HH:mm")}")),
        Sugerencias = opciones.Select(e => e.Titulo).ToList(),
        Pendiente = b,
        Resuelta = false
    };

    // Pregunta lo que falte; con todo definido muestra el resumen y espera la confirmación
    private async Task<RespuestaChat> AvanzarCompraAsync(BorradorCompra b, string texto, string usuarioId, CancellationToken ct)
    {
        var evento = await _db.Eventos.AsNoTracking().Include(e => e.TiposEntrada)
            .FirstOrDefaultAsync(e => e.Id == b.EventoId && e.Estado == EstadoEvento.Aprobado, ct);
        if (evento == null)
        {
            b.EventoId = null;
            return await PedirEventoCompraAsync(b, "Ese evento ya no está publicado. ", ct);
        }

        var zonas = evento.TiposEntrada.OrderBy(t => t.Precio).ToList();
        var conStock = zonas.Where(z => z.Disponibles > 0).ToList();

        // Gratis, terminado o agotado: ReservarAsync lo rechaza con su motivo, sin crear nada
        if (evento.EsGratuito || evento.YaOcurrio(DateTime.Now) || conStock.Count == 0)
            return ReservaRechazada(await _acciones!.ReservarAsync(usuarioId, evento.Id, null, 1, ct), evento.Id, evento.Titulo, b);

        var nombreZona = b.Zona ?? zonas.Select(z => z.Nombre).Where(n => BaseConocimiento.Contiene(texto, n)).OrderByDescending(n => n.Length).FirstOrDefault();
        var zona = nombreZona == null
            ? (conStock.Count == 1 ? conStock[0] : null)
            : conStock.FirstOrDefault(z => BaseConocimiento.Normalizar(z.Nombre) == BaseConocimiento.Normalizar(nombreZona));

        if (zona == null)
        {
            (b.Zona, b.PorConfirmar) = (null, false);
            var lineas = conStock.Select(z => $"• {z.Nombre}: {Formato.Precio(z.Precio)}" + (z.Disponibles <= 10 ? $" (¡quedan {z.Disponibles}!)" : ""));
            return new RespuestaChat
            {
                Texto = (nombreZona == null ? "" : $"«{nombreZona}» no está disponible. ") + $"🎟️ ¿Qué tipo de entrada quieres para «{evento.Titulo}»?\n{string.Join("\n", lineas)}",
                Sugerencias = conStock.Take(4).Select(z => $"{z.Nombre} · {Formato.Precio(z.Precio)}").ToList(),
                EventoId = evento.Id,
                Pendiente = b,
                Resuelta = false
            };
        }

        // Asientos numerados: el asiento se elige en el plano de la ficha
        if (zona.AsientosNumerados)
            return ReservaRechazada(await _acciones!.ReservarAsync(usuarioId, evento.Id, zona.Nombre, 1, ct), evento.Id, evento.Titulo, b);

        var maximo = Math.Min(_acciones!.MaximoPorOrden, zona.Disponibles);
        var pedida = b.Cantidad ?? 1;
        var cantidad = Math.Clamp(pedida, 1, maximo);
        var aviso = pedida > maximo ? $"Puedes llevar hasta {maximo} {(maximo == 1 ? "entrada" : "entradas")} {zona.Nombre} en una orden. " : "";
        (b.Zona, b.Cantidad, b.PorConfirmar) = (zona.Nombre, cantidad, true);

        var comprar = b.Modo == BorradorCompra.Comprar;
        var sugerencias = new List<string> { comprar ? "💳 Sí, ir a pagar" : "✅ Sí, reservar", comprar ? "🛒 Solo reservar" : "💳 Comprar ahora" };
        if (cantidad == 1 && maximo > 1)
            sugerencias.Add("Quiero 2 entradas");
        sugerencias.Add("Cancelar");

        return new RespuestaChat
        {
            Texto = $"{aviso}🧾 Confirma tu {(comprar ? "compra" : "reserva")}:\n" +
                    $"• Evento: «{evento.Titulo}» ({Formato.Fecha(evento.Fecha, "ddd dd MMM, HH:mm")})\n" +
                    $"• Entradas: {cantidad} × {zona.Nombre} ({Formato.Precio(zona.Precio)} cada una)\n" +
                    $"• Total: {Formato.Monto(zona.Precio * cantidad)}\n" +
                    (comprar ? "Al confirmar te llevo a la pasarela para pagar." : $"Quedan apartadas {_acciones.MinutosReserva} minutos en tu carrito para que las pagues.") +
                    " Si quieres otra zona o cantidad, solo dímelo.",
            Sugerencias = sugerencias,
            EventoId = evento.Id,
            Pendiente = b,
            Resuelta = true
        };
    }

    private async Task<RespuestaChat> EjecutarCompraAsync(BorradorCompra b, string usuarioId, CancellationToken ct)
    {
        var id = b.EventoId!.Value;
        var titulo = await _db.Eventos.AsNoTracking().Where(e => e.Id == id).Select(e => e.Titulo).FirstAsync(ct);
        var r = await _acciones!.ReservarAsync(usuarioId, id, b.Zona, b.Cantidad ?? 1, ct);
        if (!r.Reservada)
            return ReservaRechazada(r, id, titulo, b);

        var comprar = b.Modo == BorradorCompra.Comprar;
        return new RespuestaChat
        {
            Texto = comprar ? r.Mensaje + " Te llevo a la pasarela de pago." : r.Mensaje,
            Redirigir = comprar ? r.UrlSiguiente : null,
            EventoId = id,
            Enlaces = new() { new EnlaceChat("💳 Pagar mi reserva", r.UrlSiguiente!), new EnlaceChat("🛒 Ver mi carrito", "/Carrito") },
            Sugerencias = new() { "Ver mi carrito", $"Recuérdame {titulo}" },
            Resuelta = true
        };
    }

    // Si aún quedan otras zonas, la conversación sigue con ellas
    private static RespuestaChat ReservaRechazada(ResultadoReserva r, int eventoId, string titulo, BorradorCompra b)
    {
        var respuesta = new RespuestaChat { Texto = r.Mensaje, EventoId = eventoId, Resuelta = true };
        if (r.UrlSiguiente != null)
            respuesta.Enlaces.Add(new EnlaceChat($"Ver {titulo}", r.UrlSiguiente));
        if (r.ZonasDisponibles is { Count: > 0 })
        {
            (b.Zona, b.PorConfirmar) = (null, false);
            respuesta.Pendiente = b;
            respuesta.Sugerencias.AddRange(r.ZonasDisponibles.Take(4));
        }
        return respuesta;
    }

    private async Task<RespuestaChat> MiCarritoAsync(string usuarioId, CancellationToken ct)
    {
        var carrito = await _acciones!.CarritoAsync(usuarioId, ct);
        if (carrito.PorPagar.Count == 0)
            return new RespuestaChat
            {
                Texto = "🛒 Tu carrito está vacío: no tienes entradas reservadas por pagar.",
                Enlaces = new() { new EnlaceChat("Ver eventos", "/Eventos") },
                Sugerencias = new() { "Quiero reservar una entrada", "¿Qué eventos hay este fin de semana?" },
                Resuelta = true
            };

        var ahora = DateTime.UtcNow;
        var lineas = carrito.PorPagar.Select(i =>
            $"• {i.Evento}: {string.Join(", ", i.Lineas.Select(l => $"{l.Cantidad} {l.Zona}"))} · {Formato.Monto(i.Total)} · vence en {Math.Max(1, (int)Math.Ceiling((i.Vence - ahora).TotalMinutes))} min");
        return new RespuestaChat
        {
            Texto = $"🛒 Tienes {carrito.Entradas} {(carrito.Entradas == 1 ? "entrada" : "entradas")} por pagar ({Formato.Monto(carrito.Total)} en total):\n" + string.Join("\n", lineas),
            Enlaces = new() { new EnlaceChat("🛒 Ir a mi carrito", "/Carrito") },
            Resuelta = true
        };
    }

    private static readonly string[] PalabrasAprobacion =
        { "aprueben", "aprueba", "aprobado", "aprobada", "aprobacion", "aprobar", "aprobaron", "revisen", "revisan", "moderar", "moderacion", "pendientes", "por aprobar" };

    private static readonly string[] PalabrasTurno = { "turno", "turnos", "asignado", "asignados", "asignada", "control de acceso", "puerta", "escanear" };

    private static RespuestaChat RespuestaAviso(ResultadoRecordatorio r, EnlaceChat? enlace, params string[] sugerencias)
    {
        var respuesta = new RespuestaChat { Texto = r.Mensaje, EventoId = r.EventoId, Sugerencias = sugerencias.ToList(), Resuelta = true };
        if (enlace != null)
            respuesta.Enlaces.Add(enlace);
        respuesta.Enlaces.Add(new EnlaceChat("🔔 Mis notificaciones", "/Notificaciones"));
        return respuesta;
    }

    // Recordatorios según el rol: el organizador pide que le avisen cuando aprueben su evento, el administrador
    // cuando lleguen eventos por aprobar y el personal de control de acceso de sus turnos; para un evento concreto,
    // cualquiera recibe un recordatorio adaptado a lo que es en ese evento (asistente, organizador o personal)
    private async Task<RespuestaChat> RecordatorioAsync(string texto, int? eventoContexto, string usuarioId, CancellationToken ct)
    {
        var quitar = VerbosQuitar.Any(v => BaseConocimiento.Contiene(texto, v));
        var roles = await RolesAsync(usuarioId, ct);
        var esOrganizador = roles.Contains(Roles.Organizador) || roles.Contains(Roles.Administrador);
        var hablaDeAprobacion = PalabrasAprobacion.Any(p => BaseConocimiento.Contiene(texto, p));

        if (hablaDeAprobacion && esOrganizador)
        {
            var propio = await IdentificarEventoPropioAsync(texto, eventoContexto, usuarioId, ct);
            var pendientes = await _db.Eventos.AsNoTracking()
                .Where(e => e.OrganizadorId == usuarioId && e.Estado == EstadoEvento.Pendiente && !e.PendientePagoPublicacion)
                .Select(e => new { e.Id, e.Titulo })
                .ToListAsync(ct);

            // Sin nombrar un evento propio: el administrador se refiere a la moderación de la plataforma
            if (propio == null && roles.Contains(Roles.Administrador) && (pendientes.Count == 0 || !BaseConocimiento.Contiene(texto, "mi evento")))
            {
                var admin = await _acciones!.AvisarEventosPorAprobarAsync(usuarioId, !quitar, ct);
                return RespuestaAviso(admin, new EnlaceChat("Moderar eventos", "/Admin"), "¿Qué eventos faltan aprobar?");
            }

            propio ??= pendientes.Count == 1 ? pendientes[0].Id : null;
            if (propio == null)
            {
                return new RespuestaChat
                {
                    Texto = pendientes.Count == 0
                        ? "No tienes eventos esperando aprobación en este momento."
                        : "¿De cuál de tus eventos pendientes quieres que te avise cuando lo revisen?",
                    Sugerencias = pendientes.Take(4).Select(e => $"Avísame cuando aprueben {e.Titulo}").ToList(),
                    Enlaces = new() { new EnlaceChat("Mis eventos", "/Eventos/MisEventos") },
                    Resuelta = pendientes.Count == 0
                };
            }

            var aprobacion = await _acciones!.AvisarAprobacionAsync(usuarioId, propio.Value, ct);
            return RespuestaAviso(aprobacion, new EnlaceChat("Mis eventos", "/Eventos/MisEventos"), "¿Qué eventos tengo pendientes de aprobación?");
        }

        if (roles.Contains(Roles.ControlAcceso) && !quitar && PalabrasTurno.Any(p => BaseConocimiento.Contiene(texto, p)))
        {
            var turnos = await _acciones!.RecordarMisTurnosAsync(usuarioId, ct);
            return RespuestaAviso(turnos, new EnlaceChat("Ir a Puerta", "/Acceso"), "Mis avisos");
        }

        var evento = await IdentificarEventoAsync(texto, eventoContexto, ct);

        if (evento == null)
        {
            if (quitar)
                return await MisAvisosAsync(usuarioId, ct);
            return await PedirEventoAsync("¿De qué evento quieres que te avise? Escribe su nombre o pídemelo desde la ficha del evento.",
                t => $"Recuérdame {t}", ct);
        }

        var r = quitar
            ? await _acciones!.QuitarRecordatorioAsync(usuarioId, evento.Id, ct)
            : await _acciones!.ActivarRecordatorioAsync(usuarioId, evento.Id, ct);

        return new RespuestaChat
        {
            Texto = r.Mensaje,
            EventoId = evento.Id,
            Enlaces = new() { new EnlaceChat($"Ver {evento.Titulo}", $"/Eventos/Detalle/{evento.Id}"), new EnlaceChat("🔔 Mis notificaciones", "/Notificaciones") },
            Sugerencias = r.Activo
                ? new() { "Mis avisos", $"Resérvame 1 entrada para {evento.Titulo}" }
                : new() { "Mis avisos" },
            Resuelta = true
        };
    }

    private async Task<RespuestaChat> MisAvisosAsync(string usuarioId, CancellationToken ct)
    {
        var avisos = await _acciones!.AvisosAsync(usuarioId, ct);
        var noLeidas = await _db.Notificaciones.CountAsync(n => n.UsuarioId == usuarioId && !n.Leida, ct);
        var pieNotificaciones = noLeidas == 0 ? "" : $"\nTienes {noLeidas} {(noLeidas == 1 ? "notificación sin leer" : "notificaciones sin leer")} en la campana 🔔.";
        if (avisos.Count == 0)
        {
            return new RespuestaChat
            {
                Texto = "No tienes avisos ni entradas para eventos próximos. Pídeme \"recuérdame\" y el nombre de un evento, y te aviso en tus notificaciones y por correo antes de que empiece. 🔔" + pieNotificaciones,
                Enlaces = new() { new EnlaceChat("Ver eventos", "/Eventos"), new EnlaceChat("🔔 Mis notificaciones", "/Notificaciones") },
                Sugerencias = new() { "¿Qué eventos hay este fin de semana?", "Recomiéndame algo" },
                Resuelta = true
            };
        }

        var ahora = DateTime.Now;
        var lineas = avisos.Take(8).Select(a =>
            $"• {a.Evento} — {Formato.Fecha(a.Fecha, "ddd dd MMM, HH:mm")} ({a.Falta(ahora)})" +
            (a.TieneEntradas ? " · 🎟️ tienes entradas" : "") + (a.Recordatorio ? " · 🔔 aviso activo" : ""));

        var sugerencias = avisos.Where(a => !a.TieneEntradas).Select(a => $"Resérvame 1 entrada para {a.Evento}")
            .Concat(avisos.Where(a => !a.Recordatorio).Select(a => $"Recuérdame {a.Evento}"))
            .Take(3).ToList();

        return new RespuestaChat
        {
            Texto = $"🔔 Tus próximos eventos:\n{string.Join("\n", lineas)}" + pieNotificaciones,
            Enlaces = avisos.Take(3).Select(a => new EnlaceChat(a.Evento, $"/Eventos/Detalle/{a.EventoId}"))
                .Append(new EnlaceChat("Mis entradas", "/Entradas"))
                .Append(new EnlaceChat("🔔 Mis notificaciones", "/Notificaciones")).ToList(),
            Sugerencias = sugerencias,
            EventoId = avisos.Count == 1 ? avisos[0].EventoId : null,
            Resuelta = true
        };
    }

    private async Task<RespuestaChat> EstadoMisEventosAsync(string usuarioId, CancellationToken ct)
    {
        var estado = await _acciones!.EstadoPublicacionesAsync(usuarioId, ct);
        if (estado.Total == 0)
        {
            return new RespuestaChat
            {
                Texto = "Todavía no has creado eventos. Crea el primero y lo enviamos a revisión del administrador.",
                Enlaces = new() { new EnlaceChat("Crear evento", "/Eventos/Crear") },
                Resuelta = true
            };
        }

        static string Lista(IEnumerable<EventoPublicacion> eventos) =>
            string.Join("\n", eventos.Take(5).Select(e => $"   • {e.Titulo} ({e.Fecha:dd/MM})" + (string.IsNullOrWhiteSpace(e.Detalle) ? "" : $" — {e.Detalle}")));

        var partes = new List<string> { $"📋 Tienes {estado.Total} {(estado.Total == 1 ? "evento" : "eventos")}:" };
        void Grupo(string titulo, List<EventoPublicacion> eventos)
        {
            if (eventos.Count > 0)
                partes.Add($"{titulo} ({eventos.Count}):\n{Lista(eventos)}");
        }
        Grupo("✅ Publicados", estado.Publicados);
        Grupo("⏳ Pendientes de aprobación", estado.Pendientes);
        Grupo("💳 Falta pagar la publicación", estado.PorPagar);
        Grupo("❌ Rechazados", estado.Rechazados);
        if (estado.Finalizados.Count > 0 || estado.Cancelados.Count > 0)
            partes.Add($"🏁 Finalizados: {estado.Finalizados.Count} · 🚫 Cancelados: {estado.Cancelados.Count}");

        var sugerencias = new List<string> { "¿Cómo van mis ventas?" };
        sugerencias.AddRange(estado.Publicados.Take(2).Select(e => $"¿Cómo van las ventas de {e.Titulo}?"));

        return new RespuestaChat
        {
            Texto = string.Join("\n", partes),
            Dashboard = new DashboardChat
            {
                Titulo = "📋 Estado de tus eventos",
                Indicadores = new()
                {
                    new("Publicados", estado.Publicados.Count.ToString()),
                    new("Pendientes", estado.Pendientes.Count.ToString()),
                    new("Por pagar", estado.PorPagar.Count.ToString()),
                    new("Rechazados", estado.Rechazados.Count.ToString())
                }
            },
            Enlaces = new() { new EnlaceChat("Mis eventos", "/Eventos/MisEventos"), new EnlaceChat("Panel de ventas", "/Panel") },
            Sugerencias = sugerencias,
            Resuelta = true
        };
    }

    // Evento propio del organizador nombrado en el mensaje (cualquier estado); también "este evento" desde su ficha
    private async Task<int?> IdentificarEventoPropioAsync(string texto, int? eventoContexto, string usuarioId, CancellationToken ct)
    {
        var propios = await EventosPropiosAsync(usuarioId, ct);
        var reconocido = ReconocedorEventos.Reconocer(texto, propios);
        if (reconocido.Encontrado != null)
            return reconocido.Encontrado.Id;
        if (eventoContexto != null && BaseConocimiento.Contiene(texto, "este evento") && propios.Any(e => e.Id == eventoContexto))
            return eventoContexto;
        return null;
    }

    private Task<List<EventoCandidato>> EventosPropiosAsync(string usuarioId, CancellationToken ct) =>
        _db.Eventos.AsNoTracking()
            .Where(e => e.OrganizadorId == usuarioId)
            .Select(e => new EventoCandidato(e.Id, e.Titulo, e.Fecha))
            .ToListAsync(ct);

    private async Task<RespuestaChat> VentasOrganizadorAsync(string texto, int? eventoContexto, string usuarioId, CancellationToken ct)
    {
        var reconocido = ReconocedorEventos.Reconocer(texto, await EventosPropiosAsync(usuarioId, ct));
        if (reconocido.Ambiguo)
            return new RespuestaChat
            {
                Texto = "Tienes varios eventos con ese nombre. ¿De cuál quieres ver las ventas?",
                Sugerencias = reconocido.Opciones.Select(e => $"¿Cómo van las ventas de {e.Titulo}?").ToList(),
                Resuelta = false
            };

        // Nombró algo que no es un evento suyo: la analítica distingue una categoría de un evento que no existe
        if (reconocido.Encontrado == null && (reconocido.NoEncontrado ?? reconocido.Mencion) != null && _analitico != null)
            return await ConsultaAnaliticaAsync(texto, usuarioId, true, ct);

        var eventoId = await IdentificarEventoPropioAsync(texto, eventoContexto, usuarioId, ct);
        var v = await _acciones!.VentasAsync(usuarioId, eventoId, ct);

        if (eventoId != null && v.Encontrado)
        {
            var masVendida = v.Zonas.OrderByDescending(z => z.Vendidas).FirstOrDefault(z => z.Vendidas > 0);
            return new RespuestaChat
            {
                Texto = $"📊 «{v.Evento}»: vendiste {v.Vendidas:N0} de {v.Aforo:N0} entradas ({Math.Round(v.Ocupacion * 100):0} %) en {v.OrdenesPagadas:N0} {(v.OrdenesPagadas == 1 ? "orden" : "órdenes")}. " +
                        $"Ingresos {Formato.Monto(v.Ingresos)}, comisión {Formato.Monto(v.Comision)} y neto {Formato.Monto(v.Neto)}" +
                        (v.PorCobrar is > 0 ? $" ({Formato.Monto(v.PorCobrar.Value)} por cobrar)" : "") + ". " +
                        $"En los últimos 7 días: {v.VendidasUltimos7Dias:N0} entradas ({Formato.Monto(v.IngresosUltimos7Dias)})." +
                        (masVendida != null ? $" La zona más vendida es {masVendida.Nombre}." : "") +
                        (v.Ingresados > 0 ? $" Ya ingresaron {v.Ingresados:N0} asistentes." : ""),
                Dashboard = v.Dashboard,
                EventoId = v.EventoId,
                Enlaces = new() { new EnlaceChat("Abrir panel del evento", $"/Panel/Evento/{v.EventoId}") },
                Sugerencias = new() { "¿Cómo van mis ventas?", "¿Qué eventos tengo pendientes de aprobación?" },
                Resuelta = true
            };
        }

        if (v.Eventos.Count == 0)
        {
            return new RespuestaChat
            {
                Texto = "Aún no tienes eventos publicados con ventas. Cuando el administrador apruebe tu evento, aquí verás cuánto llevas vendido.",
                Enlaces = new() { new EnlaceChat("Mis eventos", "/Eventos/MisEventos") },
                Sugerencias = new() { "¿Qué eventos tengo pendientes de aprobación?" },
                Resuelta = true
            };
        }

        var top = v.Eventos.OrderByDescending(e => e.Ingresos).ThenByDescending(e => e.Vendidas).First();
        return new RespuestaChat
        {
            Texto = $"📊 Llevas {v.Vendidas:N0} entradas vendidas en {v.Eventos.Count} {(v.Eventos.Count == 1 ? "evento" : "eventos")}, " +
                    $"con ingresos de {Formato.Monto(v.Ingresos)} (neto {Formato.Monto(v.Neto)} tras {Formato.Monto(v.Comision)} de comisión). " +
                    $"En los últimos 7 días vendiste {v.VendidasUltimos7Dias:N0} entradas ({Formato.Monto(v.IngresosUltimos7Dias)}). " +
                    $"Tu evento con más ventas es «{top.Titulo}» ({top.Vendidas:N0} entradas, {Formato.Monto(top.Ingresos)}).",
            Dashboard = v.Dashboard,
            Enlaces = new() { new EnlaceChat("Panel de ventas", "/Panel") },
            Sugerencias = v.Eventos.OrderByDescending(e => e.Ingresos).Take(3).Select(e => $"¿Cómo van las ventas de {e.Titulo}?").ToList(),
            Resuelta = true
        };
    }
}
