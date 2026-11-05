using ActivaFest.Models;
using ActivaFest.Services.Analitica;

namespace ActivaFest.Services.Asistente;

public enum AlcanceConsulta
{
    Normal,

    // Administrador: se responde con los datos de toda la plataforma (AsistenteAnaliticoService)
    Analitica,

    // Organizador: el mismo análisis, solo con los eventos que él publicó
    AnaliticaPropia,
    SoloAdministrador,
    SoloOrganizadores
}

// =============================
// 🔐 QUÉ PUEDE CONSULTAR CADA ROL EN EL CHAT
// =============================
// Se decide en el servidor y antes de responder, con la IA activa o sin ella: el usuario común consulta compras y
// reservas, el organizador sus propios eventos y ventas, y solo el administrador los datos de toda la plataforma.
public static class AlcanceChat
{
    public const string IntencionDenegada = "AccesoPorRol";

    private static readonly string[] Plataforma =
    {
        "toda la plataforma", "toda la pagina", "toda la web", "todo el sitio", "resumen de la plataforma",
        "ventas de la plataforma", "ingresos de la plataforma", "comisiones de la plataforma", "usuarios de la plataforma",
        "eventos de la plataforma", "eventos en la plataforma", "cuantos usuarios", "usuarios registrados", "todos los usuarios",
        "eventos hay en total", "eventos en total", "total de eventos", "cuantos eventos hay", "cuantos eventos se han publicado",
        "cuantos eventos han sido publicados", "cuantos eventos publicados", "otros organizadores", "todos los organizadores"
    };

    private static readonly string[] LoPropio = { "mi evento", "mis eventos", "mis ventas", "mis compras", "mis entradas" };

    // Estas preguntas del organizador ya las responde su tarjeta de ventas
    private static readonly string[] ResumenOrganizador = { "ResumenVentas", "TotalesPlataforma", "Recomendaciones" };

    public static bool PreguntaDePlataforma(string t) => Plataforma.Any(p => BaseConocimiento.Contiene(t, p));

    public static AlcanceConsulta Clasificar(string texto, string intencion, string rol, DateTime hoy)
    {
        var grafico = AsistenteAnaliticoService.PideGrafico(texto);

        // Lo personal (reservar, carrito, avisos, mi asiento) vale para cualquier rol, salvo que pida un gráfico
        var personal = intencion is Intenciones.Reservar or Intenciones.Recordatorio or Intenciones.MisAvisos or Intenciones.MiUbicacion or Intenciones.MiCarrito;
        if (personal && !(grafico && rol is Roles.Administrador or Roles.Organizador))
            return AlcanceConsulta.Normal;

        if (rol == Roles.Administrador)
        {
            // Un gráfico siempre es analítica: la tabla y el dibujo no los puede dar el modelo de lenguaje
            if (grafico || PreguntaDePlataforma(texto))
                return AlcanceConsulta.Analitica;
            if (LoPropio.Any(p => BaseConocimiento.Contiene(texto, p)))
                return AlcanceConsulta.Normal;
            return intencion == Intenciones.VentasOrganizador || AsistenteAnaliticoService.Interpretar(texto, hoy) != null
                ? AlcanceConsulta.Analitica : AlcanceConsulta.Normal;
        }

        if (PreguntaDePlataforma(texto))
            return AlcanceConsulta.SoloAdministrador;

        if (rol == Roles.Organizador)
        {
            // "Mi dashboard" es su tarjeta de indicadores; cualquier otro gráfico se dibuja con sus propios eventos
            if (BaseConocimiento.Contiene(texto, "dashboard"))
                return AlcanceConsulta.Normal;
            if (grafico)
                return AlcanceConsulta.AnaliticaPropia;
            if (intencion is not (Intenciones.VentasOrganizador or Intenciones.Desconocida or Intenciones.Comisiones or Intenciones.Destacar))
                return AlcanceConsulta.Normal;

            var funcion = AsistenteAnaliticoService.Interpretar(texto, hoy)?.Funcion;
            return funcion == null || ResumenOrganizador.Contains(funcion) ? AlcanceConsulta.Normal : AlcanceConsulta.AnaliticaPropia;
        }

        return intencion is Intenciones.Comisiones or Intenciones.Destacar || grafico
            ? AlcanceConsulta.SoloOrganizadores
            : AlcanceConsulta.Normal;
    }

    // Estas consultas las resuelve siempre el asistente base: el modelo de IA no decide sobre permisos
    public static bool ResuelveElAsistenteBase(string texto, string intencion, string rol, DateTime hoy) =>
        Clasificar(texto, intencion, rol, hoy) != AlcanceConsulta.Normal;

    public static RespuestaChat Denegada(AlcanceConsulta alcance, string rol)
    {
        var respuesta = new RespuestaChat
        {
            Sugerencias = AgenteIA.SugerenciasPara(rol),
            Intencion = IntencionDenegada,
            Resuelta = true
        };

        if (alcance == AlcanceConsulta.SoloOrganizadores)
        {
            respuesta.Texto = "Esa información es para organizadores. Yo te ayudo con tus compras, reservas y entradas, y a encontrar eventos. " +
                              "Si quieres vender entradas de tu propio evento, solicita el perfil de organizador.";
            respuesta.Enlaces.Add(new EnlaceChat("Organiza tu evento", "/Organizador/Solicitar"));
            return respuesta;
        }

        respuesta.Texto = rol == Roles.Organizador
            ? "Esa información es de toda la plataforma y solo la ve el administrador. Puedo mostrarte tu dashboard de ventas y el estado de tus eventos."
            : "Esa información solo la ve el administrador. Yo te ayudo con tus compras, reservas y entradas, y a encontrar eventos.";
        return respuesta;
    }
}
