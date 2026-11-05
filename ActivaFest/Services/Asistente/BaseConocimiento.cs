using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ActivaFest.Services.Asistente;

// Intenciones que entiende el asistente (HU14)
public static class Intenciones
{
    // Requieren datos de la BD
    public const string BuscarEventos = "BuscarEventos";
    public const string Recomendacion = "Recomendacion";
    public const string Ubicacion = "Ubicacion";
    public const string Horario = "Horario";
    public const string Precio = "Precio";
    public const string Instalaciones = "Instalaciones";

    // HU28: ubicación de la entrada del propio usuario (zona, fila, asiento, puerta y cómo llegar)
    public const string MiUbicacion = "MiUbicacion";

    // Se responden con la base de conocimiento
    public const string Saludo = "Saludo";
    public const string Agradecimiento = "Agradecimiento";
    public const string ComoComprar = "ComoComprar";
    public const string MediosPago = "MediosPago";
    public const string MisEntradas = "MisEntradas";
    public const string ReembolsoCancelacion = "ReembolsoCancelacion";
    public const string TransferirEntrada = "TransferirEntrada";
    public const string IngresoQr = "IngresoQr";
    public const string ProblemaCompra = "ProblemaCompra";
    public const string PublicarEvento = "PublicarEvento";
    public const string Destacar = "Destacar";
    public const string Comisiones = "Comisiones";
    public const string Cuenta = "Cuenta";
    public const string Edad = "Edad";
    public const string Contacto = "Contacto";

    // Acciones por rol (AccionesAsistente): reservar y avisos (usuario), publicaciones y ventas (organizador)
    public const string Reservar = "Reservar";
    public const string Recordatorio = "Recordatorio";
    public const string MisAvisos = "MisAvisos";
    public const string EstadoMisEventos = "EstadoMisEventos";
    public const string VentasOrganizador = "VentasOrganizador";
    public const string MiCarrito = "MiCarrito";

    // El administrador pregunta por toda la plataforma: la decide AlcanceChat según el rol, no las palabras clave
    public const string ConsultaPlataforma = "ConsultaPlataforma";

    // El organizador pide un gráfico o análisis: se responde solo con sus eventos
    public const string ConsultaMisEventos = "ConsultaMisEventos";

    public const string Desconocida = "Desconocida";

    // Preguntas sobre un evento concreto
    public static readonly string[] SobreUnEvento = { Ubicacion, Horario, Precio, Instalaciones };

    public static readonly string[] AccionesPorRol = { Reservar, Recordatorio, MisAvisos, EstadoMisEventos, VentasOrganizador, MiCarrito };
}

public record EntradaConocimiento(
    string Intencion,
    string[] PalabrasClave,
    string[] Ejemplos,
    string? Respuesta = null,
    string[]? Enlaces = null,
    string[]? Sugerencias = null);

// =============================
// 📚 BASE DE CONOCIMIENTO DE PREGUNTAS FRECUENTES (HU14 / OE3)
// =============================
// Las palabras clave guían la detección; los ejemplos entrenan el modelo ML.NET.
// Enlaces: "Texto|/ruta".
public static class BaseConocimiento
{
    public static readonly EntradaConocimiento[] Entradas =
    {
        new(Intenciones.Saludo,
            new[] { "hola", "buenas", "buenos dias", "buenas tardes", "buenas noches", "hey", "que tal" },
            new[] { "hola", "buenas tardes", "hola que tal", "buenos dias asistente", "hey hola" },
            "¡Hola! 👋 Soy el asistente de ActivaFest. Puedo ayudarte a encontrar eventos, decirte dónde quedan, a qué hora empiezan y cuánto cuestan, o resolver dudas sobre tus entradas.",
            Sugerencias: new[] { "¿Qué eventos hay este fin de semana?", "¿Cómo compro entradas?", "Recomiéndame algo" }),

        new(Intenciones.Agradecimiento,
            new[] { "gracias", "muchas gracias", "genial", "perfecto", "excelente", "chevere" },
            new[] { "gracias", "muchas gracias por la ayuda", "perfecto gracias", "genial muchas gracias" },
            "¡Con gusto! Si necesitas algo más, aquí estoy. 🎉"),

        new(Intenciones.ComoComprar,
            new[] { "como compro", "comprar entrada", "comprar entradas", "como comprar", "adquirir", "como hago para comprar", "pasos para comprar", "conseguir entradas", "para comprar", "comprar boletos", "comprar tickets" },
            new[] { "como compro entradas", "como funciona la compra de entradas", "como hago para comprar boletos", "cuales son los pasos para comprar", "donde compro mis tickets", "que necesito para comprar" },
            "Comprar es muy fácil: 1) abre la ficha del evento, 2) elige la cantidad por zona y pulsa \"Comprar entradas\", 3) confirma el resumen (tus entradas quedan reservadas 10 minutos), 4) paga en la pasarela y 5) recibe tus entradas con código QR en tu correo y en \"Mis entradas\". Necesitas una cuenta para comprar.",
            new[] { "Ver eventos|/Eventos", "Crear cuenta|/Auth/Registro" }),

        new(Intenciones.MediosPago,
            new[] { "medios de pago", "metodos de pago", "formas de pago", "tarjeta", "yape", "plin", "mercado pago", "efectivo", "pagar con", "como pago", "pago seguro", "es seguro", "debito", "credito" },
            new[] { "que medios de pago aceptan", "puedo pagar con tarjeta", "aceptan yape", "como pago mis entradas", "se puede pagar en efectivo" },
            "El pago se hace en línea a través de Mercado Pago, que acepta tarjetas de crédito y débito y otros medios disponibles en tu cuenta de Mercado Pago. ActivaFest nunca ve ni guarda los datos de tu tarjeta. No se aceptan pagos en efectivo en la plataforma."),

        new(Intenciones.MisEntradas,
            new[] { "mis entradas", "donde estan mis entradas", "no me llego", "no recibi", "reenviar", "perdi mi entrada", "ver mi qr", "mi codigo qr", "descargar entrada", "correo con las entradas" },
            new[] { "donde veo mis entradas", "no me llego el correo con mi entrada", "perdi el qr de mi entrada", "como descargo mi entrada", "no recibi mis boletos" },
            "Tus entradas siempre están en \"Mis entradas\" (inicia sesión con la misma cuenta con la que compraste). Desde ahí puedes ver y descargar cada código QR. También te las enviamos al correo justo después del pago; revisa la carpeta de spam si no lo encuentras.",
            new[] { "Mis entradas|/Entradas" }),

        new(Intenciones.ReembolsoCancelacion,
            new[] { "reembolso", "devolucion", "devolver", "cancelar mi compra", "cancelar entrada", "me devuelven", "anular compra", "cancelaron el evento", "evento cancelado", "postergado", "devuelvan", "mi plata", "mi dinero" },
            new[] { "como pido un reembolso", "quiero cancelar mi compra", "me devuelven el dinero", "si cancelan el evento que pasa", "puedo devolver mi entrada" },
            "Las devoluciones se gestionan directamente con el organizador del evento: ActivaFest es el intermediario tecnológico y no realiza reembolsos automáticos. Si el evento cambia de fecha o lugar te avisaremos por correo. Para un reembolso, escribe al organizador indicando tu número de orden."),

        new(Intenciones.TransferirEntrada,
            new[] { "transferir", "regalar", "ceder", "pasar mi entrada", "otra persona", "revender", "reventa", "vender mi entrada" },
            new[] { "puedo transferir mi entrada a otra persona", "puedo regalar mi entrada", "se puede revender una entrada", "quiero vender mi entrada" },
            "La plataforma no tiene reventa entre usuarios. Cada entrada tiene un QR único que solo funciona una vez: si alguien más usa tu QR antes que tú, el tuyo será rechazado en la puerta. Comparte tu QR solo con la persona que asistirá en tu lugar."),

        new(Intenciones.IngresoQr,
            new[] { "qr", "puerta", "ingreso", "ingresar", "entrar al evento", "escanear", "imprimir", "celular", "captura de pantalla", "a que hora abren", "abren las puertas", "mi qr", "con el qr", "puedo ingresar" },
            new[] { "como ingreso al evento", "tengo que imprimir la entrada", "puedo mostrar el qr en el celular", "sirve una captura del qr", "a que hora abren las puertas" },
            "En la puerta muestra el código QR desde tu celular o impreso; el personal lo escanea. Cada QR sirve una sola vez. El ingreso se habilita desde 6 horas antes del inicio del evento y la entrada vale hasta 12 horas después."),

        new(Intenciones.ProblemaCompra,
            new[] { "problema", "error", "no funciona", "no pude pagar", "pago rechazado", "me cobraron", "cobro doble", "no se proceso", "fallo el pago", "ayuda con mi pago" },
            new[] { "tengo un problema con mi pago", "me cobraron pero no tengo entradas", "mi pago fue rechazado", "error al comprar", "ayuda mi pago no procesa" },
            "Lamento el inconveniente. Si el pago fue rechazado, puedes reintentarlo mientras dure tu reserva de 10 minutos. Si te cobraron y no ves tus entradas en \"Mis entradas\", escríbenos a soporte@activafest.com con tu número de orden y lo revisamos.",
            new[] { "Mis entradas|/Entradas" }),

        new(Intenciones.PublicarEvento,
            new[] { "publicar", "organizar", "organizador", "crear un evento", "subir mi evento", "vender mis entradas", "tengo un evento", "promocionar mi evento", "promotor", "productora", "como vendo", "vendo entradas", "mi concierto" },
            new[] { "como publico mi evento", "quiero organizar un evento", "como vendo entradas de mi evento", "soy organizador como empiezo", "quiero subir mi evento a la plataforma" },
            "Para publicar: crea una cuenta de tipo Organizador, entra a \"Mis eventos\" → \"Crear evento\" y completa los datos, la ubicación en el mapa, las zonas con sus precios y al menos 3 fotos del recinto. Nuestro equipo lo revisa y, al aprobarlo, aparece en el catálogo.",
            new[] { "Crear cuenta|/Auth/Registro" }),

        new(Intenciones.Destacar,
            new[] { "destacar", "destacado", "publicidad", "anunciar", "aparecer primero", "portada", "paquete publicitario", "destacar mi evento", "publicitar" },
            new[] { "como destaco mi evento", "cuanto cuesta la publicidad", "quiero que mi evento salga en la portada", "tienen paquetes publicitarios" },
            "Los organizadores pueden destacar sus eventos en la portada con paquetes de 3, 7 o 15 días desde \"Mis eventos\" → \"Destacar\". El cobro es inmediato y el evento aparece en la sección Destacados durante el periodo contratado."),

        new(Intenciones.Comisiones,
            new[] { "comision", "cuanto cobran", "cuanto me cobran", "liquidacion", "cuando me pagan", "porcentaje" },
            new[] { "cuanto cobra la plataforma de comision", "cuando me pagan mis ventas", "que comision tienen", "como funciona la liquidacion" },
            "ActivaFest cobra una comisión por cada entrada vendida, que se descuenta del monto de la venta. Como organizador ves en tu panel las ventas, la comisión, el neto y el saldo pendiente; el equipo te liquida el saldo y cada pago queda registrado."),

        new(Intenciones.Cuenta,
            new[] { "contrasena", "clave", "olvide", "iniciar sesion", "registrarme", "crear cuenta", "mi cuenta", "no puedo entrar", "bloqueada" },
            new[] { "olvide mi contrasena", "como me registro", "no puedo iniciar sesion", "como creo una cuenta", "mi cuenta esta bloqueada" },
            "Puedes crear tu cuenta en \"Crear cuenta\". Si olvidaste tu contraseña, en \"Iniciar sesión\" pulsa \"¿Olvidaste tu contraseña?\" y te enviaremos un enlace. Tras 5 intentos fallidos la cuenta se bloquea 10 minutos por seguridad.",
            new[] { "Iniciar sesión|/Auth/Login", "Crear cuenta|/Auth/Registro" }),

        new(Intenciones.Edad,
            new[] { "edad", "menores", "ninos", "niños", "mayor de edad", "familia" },
            new[] { "pueden ir menores de edad", "hay eventos para ninos", "hay restriccion de edad", "puedo llevar a mis hijos" },
            "ActivaFest es para todo público: no hay restricción de edad en la plataforma. Algunos eventos pueden tener condiciones propias; revisa la descripción de cada evento. Para planes con niños, mira la categoría Familiar.",
            new[] { "Eventos para la familia|/Categorias?CategoriaId=8" }),

        new(Intenciones.Contacto,
            new[] { "contacto", "hablar con alguien", "humano", "soporte", "atencion al cliente", "telefono", "correo de soporte", "agente" },
            new[] { "quiero hablar con una persona", "como contacto a soporte", "tienen un telefono de atencion", "necesito hablar con un agente" },
            "Puedes escribirnos a soporte@activafest.com y te responderemos lo antes posible. Si tu consulta es sobre un evento en particular (horario, ingreso, devoluciones), también puedes contactar a su organizador."),

        // ----- Intenciones con datos de la BD (la respuesta se arma en AsistenteService) -----
        new(Intenciones.MiUbicacion,
            new[] { "mi asiento", "mis asientos", "donde esta mi asiento", "donde queda mi asiento", "donde me siento", "donde me toca", "mi butaca", "mi fila", "mi zona", "mi ubicacion", "donde estoy ubicado", "por que puerta", "que puerta", "mi puerta", "por donde entro", "como llego a mi asiento", "como llego a mi zona", "donde esta mi zona" },
            new[] { "donde esta mi asiento", "donde me toca sentarme en el concierto de manana", "por que puerta entro", "como llego a mi zona", "cual es mi asiento para el evento de hoy", "en que fila estoy" }),

        new(Intenciones.BuscarEventos,
            new[] { "que eventos", "eventos hay", "hay algun", "hay algo", "busco", "quiero ir", "planes", "que hacer", "concierto", "conciertos", "obra", "teatro", "feria", "festival", "exposicion", "partido", "fin de semana", "esta semana", "hoy", "manana", "gratis", "gratuito", "barato", "eventos para", "actividades", "familias" },
            new[] { "que eventos hay este fin de semana", "busco un concierto", "hay obras de teatro", "quiero ir a una feria", "que hay para hacer hoy", "hay eventos gratis", "eventos en arequipa", "algun festival este mes" }),

        new(Intenciones.Recomendacion,
            new[] { "recomienda", "recomiendame", "recomendacion", "sugiere", "sugerencia", "que me sugieres", "no se a donde ir", "no se que hacer", "para mi" },
            new[] { "recomiendame algo", "que me sugieres hacer", "no se a donde ir", "dame una recomendacion", "que evento me recomiendas" }),

        new(Intenciones.Ubicacion,
            new[] { "donde queda", "donde es", "donde sera", "ubicacion", "direccion", "como llego", "como llegar", "lugar", "mapa", "ruta", "en que lugar" },
            new[] { "donde queda el evento", "cual es la direccion", "como llego al lugar", "en que lugar es", "donde sera el concierto" }),

        new(Intenciones.Horario,
            new[] { "a que hora", "que hora", "horario", "cuando es", "cuando sera", "que dia", "fecha", "empieza", "inicia", "comienza", "termina" },
            new[] { "a que hora empieza", "cuando es el evento", "que dia es", "cual es el horario", "a que hora comienza el show" }),

        new(Intenciones.Precio,
            new[] { "cuanto cuesta", "cuanto vale", "precio", "precios", "tarifa", "tarifas", "costo", "valor de la entrada", "es gratis", "zonas", "vip", "quedan entradas", "disponibles", "agotado" },
            new[] { "cuanto cuesta la entrada", "cuales son los precios", "cuanto vale la zona vip", "todavia quedan entradas", "es gratis el evento" }),

        // ----- Acciones por rol (la respuesta se arma con AccionesAsistente) -----
        new(Intenciones.Reservar,
            new[] { "reservar", "reservame", "reservarme", "quiero reservar", "hazme una reserva", "haz una reserva", "hacer una reserva", "una reserva", "apartar", "apartame", "separar", "separame", "aparta", "separa", "quiero comprar", "comprame", "comprarme", "consigueme", "quiero asistir", "quiero entradas" },
            new[] { "reservame 2 entradas para el concierto", "quiero reservar una entrada", "apartame tres entradas vip", "separa dos entradas generales", "puedes reservar entradas por mi",
                "quiero comprar dos entradas", "comprame una entrada para el festival", "hazme una reserva para el sabado", "consigueme entradas para la obra", "necesito tres boletos para la feria", "quiero hacer una reserva", "me reservas una entrada general", "quiero comprar una entrada vip" }),

        new(Intenciones.MiCarrito,
            new[] { "carrito", "mi carrito", "mis reservas", "mi reserva", "reservas pendientes", "tengo reservado", "tengo reservadas", "por pagar", "entradas reservadas" },
            new[] { "que tengo en mi carrito", "muestrame mi carrito", "ver mis reservas", "que entradas tengo por pagar", "como pago mi reserva", "cuanto debo de mis reservas", "abre mi carrito", "que reservas tengo activas" }),

        new(Intenciones.Recordatorio,
            new[] { "recuerdame", "recordarme", "recordatorio", "avisame", "avisame cuando", "avisarme", "notificame", "notificarme", "me avisas", "me recuerdas", "activar aviso", "activa un aviso", "quiero que me avises", "alerta", "no me quiero olvidar", "ya no me avises", "quitar aviso", "quita el aviso", "cancelar aviso", "desactivar aviso", "quitar recordatorio" },
            new[] { "recuerdame el concierto del sabado", "avisame cuando se acerque el festival", "notificame de este evento", "quiero un recordatorio de la feria", "ya no me avises de ese evento" }),

        new(Intenciones.MisAvisos,
            new[] { "mis recordatorios", "mis avisos", "mis notificaciones", "notificaciones", "tengo avisos", "que eventos tengo", "mis proximos eventos", "proximos eventos que tengo", "que tengo pendiente", "mis eventos proximos" },
            new[] { "cuales son mis recordatorios", "tengo notificaciones", "que eventos tengo proximamente", "muestrame mis avisos", "mis proximos eventos" }),

        new(Intenciones.EstadoMisEventos,
            new[] { "mis eventos publicados", "eventos publicados", "mis publicaciones", "tengo pendientes", "tengo publicados", "pendientes de aprobacion", "pendiente de aprobacion", "por aprobar", "falta aprobar", "faltan aprobar", "estado de mis eventos", "mis eventos pendientes", "eventos pendientes", "aprobaron mi evento", "ya aprobaron", "fue aprobado", "eventos rechazados", "rechazaron mi evento", "mis eventos", "en revision" },
            new[] { "que eventos tengo publicados", "cuales de mis eventos estan pendientes de aprobacion", "ya aprobaron mi evento", "por que rechazaron mi evento", "cual es el estado de mis eventos",
                "dime los eventos que ya he aprobado", "que eventos faltan aprobar", "eventos que estan en revision", "muestrame los eventos rechazados", "lista de eventos aprobados" }),

        new(Intenciones.VentasOrganizador,
            new[] { "mis ventas", "cuanto he vendido", "he vendido", "cuanto vendi", "cuanto se ha vendido", "cuanto llevo vendido", "dashboard", "mi dashboard", "resumen de ventas", "reporte de ventas", "entradas vendidas", "cuantas entradas vendi", "cuantas entradas he vendido", "ventas de mi evento", "como van las ventas", "como van mis ventas", "mis ingresos", "cuanto he ganado", "cuanto gane", "estadisticas", "ventas", "vendidas", "recaudado", "cuanto recaude" },
            new[] { "como van las ventas de mis eventos", "muestrame mi dashboard", "cuantas entradas he vendido del festival", "dame el resumen de ventas", "cuanto he ganado este mes",
                "dime cuanto he vendido en el evento de cumbia peruana", "cuanto vendi en la feria", "cuanto se recaudo en el concierto", "ventas del evento", "ingresos de mi festival", "hazme un grafico de mis ventas", "grafico de barras de mis eventos", "diagrama de ventas por categoria" }),

        new(Intenciones.Instalaciones,
            new[] { "instalaciones", "recinto", "estacionamiento", "parqueo", "techado", "aforo", "capacidad", "fotos", "como es el lugar", "accesible", "silla de ruedas", "banos" },
            new[] { "como son las instalaciones", "hay estacionamiento", "cual es el aforo", "es techado el lugar", "tienen fotos del recinto" }),
    };

    public static EntradaConocimiento? Buscar(string intencion) => Entradas.FirstOrDefault(e => e.Intencion == intencion);

    // Palabras significativas que el asistente conoce (de las palabras clave y los ejemplos)
    private static readonly HashSet<string> Vocabulario = Entradas
        .SelectMany(e => e.PalabrasClave.Concat(e.Ejemplos))
        .SelectMany(f => Normalizar(f).Split(' '))
        .Where(p => p.Length > 3)
        .ToHashSet();

    // Evita que el modelo ML "adivine" una intención para un texto que no tiene nada que ver con la plataforma
    public static bool TieneVocabularioConocido(string textoNormalizado) =>
        textoNormalizado.Split(' ').Any(Vocabulario.Contains);

    // "Música en VIVO!!" -> "musica en vivo"
    public static string Normalizar(string texto)
    {
        var descompuesto = (texto ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
        {
            var categoria = CharUnicodeInfo.GetUnicodeCategory(c);
            if (categoria == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    // La frase aparece como palabras completas dentro del texto normalizado
    public static bool Contiene(string textoNormalizado, string frase)
    {
        var f = Normalizar(frase);
        return f.Length > 0 && $" {textoNormalizado} ".Contains($" {f} ", StringComparison.Ordinal);
    }
}
