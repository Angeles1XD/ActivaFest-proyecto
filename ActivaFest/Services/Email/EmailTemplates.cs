using System.Net;
using ActivaFest.Models;

namespace ActivaFest.Services.Email;

// =============================
// 🎨 PLANTILLAS HTML DE CORREO (HU15)
// =============================
public static class EmailTemplates
{
    private static string Layout(string titulo, string contenido, string? botonTexto = null, string? botonUrl = null)
    {
        var boton = botonUrl == null ? "" : $@"
            <p style=""text-align:center;margin:30px 0;"">
                <a href=""{botonUrl}"" style=""background:linear-gradient(90deg,#ff4d6d,#ff006e);background-color:#ff006e;color:#ffffff;padding:12px 24px;border-radius:10px;text-decoration:none;font-weight:bold;"">{botonTexto}</a>
            </p>";

        return $@"<!DOCTYPE html>
<html lang=""es"">
<body style=""margin:0;padding:0;background:#f4f4f7;font-family:'Segoe UI',Arial,sans-serif;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f4f4f7;padding:30px 0;"">
    <tr><td align=""center"">
      <table width=""560"" cellpadding=""0"" cellspacing=""0"" style=""background:#ffffff;border-radius:14px;overflow:hidden;"">
        <tr><td style=""background:#0a0a1a;padding:20px 30px;color:#ff4d6d;font-size:22px;font-weight:bold;"">ActivaFest</td></tr>
        <tr><td style=""padding:30px;color:#333;font-size:15px;line-height:1.6;"">
          <h2 style=""margin-top:0;color:#111;"">{titulo}</h2>
          {contenido}
          {boton}
        </td></tr>
        <tr><td style=""background:#fafafa;padding:16px 30px;color:#999;font-size:12px;text-align:center;"">
          Este es un correo automático de ActivaFest. Por favor no respondas a este mensaje.
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
    }

    private static string E(string? texto) => WebUtility.HtmlEncode(texto ?? "");

    public static string Bienvenida(string nombre, string rol, string urlLogin) =>
        Layout($"¡Bienvenido/a, {E(nombre)}!",
            $"<p>Tu cuenta en ActivaFest fue creada correctamente con el perfil <b>{E(rol)}</b>.</p>" +
            "<p>Ya puedes explorar eventos, y si eres organizador, publicar los tuyos.</p>",
            "Iniciar sesión", urlLogin);

    public static string EventoRecibido(Evento ev) =>
        Layout("Recibimos tu evento",
            $"<p>Tu evento <b>{E(ev.Titulo)}</b> fue registrado y está <b>pendiente de revisión</b> por nuestro equipo.</p>" +
            "<p>Te avisaremos por correo cuando sea aprobado o si necesita cambios.</p>");

    // HU34: evento gratuito que debe pagar la tarifa de publicación antes de pasar a revisión
    public static string PublicacionPorPagar(Evento ev, decimal tarifa, string urlPago) =>
        Layout("Falta un paso para publicar tu evento",
            $"<p>Registramos <b>{E(ev.Titulo)}</b> como evento de <b>entrada libre</b>. Para publicarlo se cobra una tarifa única de " +
            $"<b>S/ {tarifa:0.00}</b> según su aforo ({ev.Aforo:N0} personas).</p>" +
            "<p>Cuando la pagues, el evento pasará a revisión del administrador.</p>",
            "Pagar publicación", urlPago);

    public static string PublicacionPagada(PagoPublicacion pago, string titulo) =>
        Layout("Pagaste la publicación de tu evento ✅",
            $"<p>Recibimos el pago de <b>S/ {pago.Monto:0.00}</b> por publicar <b>{E(titulo)}</b> (referencia {E(pago.Referencia)}).</p>" +
            "<p>Tu evento ya está en revisión; te avisaremos cuando sea aprobado.</p>");

    // Confirmación de la compra de un paquete publicitario (HU12)
    public static string DestacadoActivado(ContratoDestacado c) =>
        Layout("¡Tu evento ya está destacado! ⭐",
            $"<p>Contrataste el paquete <b>{E(c.Paquete?.Nombre)}</b> para <b>{E(c.Evento?.Titulo)}</b>.</p>" +
            $"<p>Aparecerá en la sección <b>Destacados</b> de la portada del " +
            $"{c.FechaInicio:dd/MM/yyyy HH:mm} al {c.FechaFin:dd/MM/yyyy HH:mm}.</p>" +
            $"<p>Monto pagado: <b>{Formato.Monto(c.Precio)}</b> · N.° de operación: {E(c.Codigo)}</p>");

    // Aviso a quienes ya compraron cuando cambia la fecha o el lugar (RF13)
    public static string EventoActualizado(Evento ev, DateTime fechaAnterior, string ubicacionAnterior, string urlEvento)
    {
        var cambios = new List<string>();
        if (fechaAnterior != ev.Fecha)
            cambios.Add($"<li>📅 <b>Nueva fecha:</b> {Formato.Fecha(ev.Fecha, "dddd dd 'de' MMMM 'de' yyyy, HH:mm")} " +
                        $"<span style=\"color:#999;\">(antes: {Formato.Fecha(fechaAnterior, "dd/MM/yyyy HH:mm")})</span></li>");
        if (ubicacionAnterior != ev.Ubicacion)
            cambios.Add($"<li>📍 <b>Nuevo lugar:</b> {E(ev.Ubicacion)} <span style=\"color:#999;\">(antes: {E(ubicacionAnterior)})</span></li>");

        return Layout("Hubo un cambio en tu evento",
            $"<p>El organizador de <b>{E(ev.Titulo)}</b> actualizó información importante:</p>" +
            $"<ul>{string.Join("", cambios)}</ul>" +
            "<p>Tus entradas siguen siendo válidas con el mismo código QR. Si no puedes asistir, comunícate con el organizador.</p>",
            "Ver el evento", urlEvento);
    }

    // HU15: aviso de cancelación a cada comprador
    public static string EventoCancelado(Evento ev, string nombre, IReadOnlyList<string> ordenes, string urlMisEntradas) =>
        Layout("Tu evento fue cancelado",
            $"<p>Hola {E(nombre)}, lamentamos informarte que el evento <b>{E(ev.Titulo)}</b> " +
            $"programado para el {Formato.Fecha(ev.Fecha, "dddd dd 'de' MMMM 'de' yyyy, HH:mm")} fue <b>cancelado</b>.</p>" +
            $"<p><b>Motivo:</b> {E(ev.MotivoCancelacion)}</p>" +
            "<p>Tus entradas quedaron anuladas. El organizador gestionará la devolución de tu dinero; " +
            $"tus números de orden son: <b>{E(string.Join(", ", ordenes))}</b>. Si tienes dudas, escríbenos a soporte@activafest.com.</p>",
            "Ver mis entradas", urlMisEntradas);

    public static string EventoCanceladoOrganizador(Evento ev, int ordenesPagadas) =>
        Layout("Cancelaste tu evento",
            $"<p>El evento <b>{E(ev.Titulo)}</b> quedó cancelado y fue retirado del catálogo.</p>" +
            $"<p><b>Motivo informado a los compradores:</b> {E(ev.MotivoCancelacion)}</p>" +
            (ordenesPagadas > 0
                ? $"<p>Hay <b>{ordenesPagadas}</b> {(ordenesPagadas == 1 ? "compra pagada" : "compras pagadas")} pendientes de reembolso. " +
                  "Recuerda devolver el dinero a cada comprador; estas ventas no generan comisión para ActivaFest.</p>"
                : "<p>No había compras pagadas, así que no tienes reembolsos pendientes.</p>"));

    public static string EventoAprobado(Evento ev, string urlEvento) =>
        Layout("¡Tu evento fue aprobado! 🎉",
            $"<p>El evento <b>{E(ev.Titulo)}</b> ya está publicado en el catálogo de ActivaFest.</p>" +
            $"<p>📍 {E(ev.Ubicacion)}<br/>📅 {ev.Fecha:dd/MM/yyyy HH:mm}</p>",
            "Ver evento", urlEvento);

    // Aviso que el usuario pidió al asistente virtual antes de que empiece el evento
    public static string RecordatorioEvento(string nombre, Evento ev, bool tieneEntradas, string urlEvento) =>
        Layout($"¡{E(ev.Titulo)} ya se acerca! 🔔",
            $"<p>Hola {E(nombre)}, te recordamos el evento que nos pediste avisarte:</p>" +
            $"<p>📍 {E(ev.Ubicacion)}<br/>📅 {ev.Fecha:dd/MM/yyyy HH:mm}</p>" +
            (tieneEntradas
                ? "<p>Ya tienes tus entradas: lleva tu código QR desde \"Mis entradas\".</p>"
                : "<p>Aún no tienes entradas para este evento. ¡Consíguelas antes de que se agoten!</p>"),
            tieneEntradas ? "Ver evento" : "Comprar entradas", urlEvento);

    public static string EventoRechazado(Evento ev, string urlMisEventos) =>
        Layout("Tu evento no fue aprobado",
            $"<p>Revisamos el evento <b>{E(ev.Titulo)}</b> y por ahora no puede publicarse.</p>" +
            $"<p><b>Motivo:</b> {E(ev.MotivoRechazo)}</p>" +
            "<p>Puedes corregir la información y volver a enviarlo.</p>",
            "Ir a mis eventos", urlMisEventos);

    // HU31 / HU32: solicitudes al administrador
    public static string SolicitudParaAdmin(string tipo, string quien, string urlAdmin) =>
        Layout("Nueva solicitud por revisar",
            $"<p><b>{E(quien)}</b> envió una solicitud para <b>{E(tipo)}</b>.</p><p>Revísala y apruébala o recházala desde el panel de administración.</p>",
            "Ver solicitudes", urlAdmin);

    public static string OrganizadorAprobado(string nombre, DateTime? hasta, string urlCrearEvento) =>
        Layout("¡Tu cuenta ya puede crear eventos! 🎉",
            $"<p>Hola {E(nombre)}, aprobamos tu solicitud: ya puedes publicar tus eventos en ActivaFest" +
            (hasta == null ? " sin fecha de vencimiento." : $" hasta el <b>{hasta:dd/MM/yyyy}</b>.") + "</p>" +
            "<p>Cada evento que crees pasa por la revisión del equipo antes de aparecer en el catálogo. " +
            "Si necesitas personal para validar las entradas en la puerta, solicítalo desde «Mis eventos».</p>",
            "Crear mi primer evento", urlCrearEvento);

    public static string SolicitudRechazada(string tipo, string nombre, string? motivo, string urlSolicitud) =>
        Layout("Tu solicitud no fue aprobada",
            $"<p>Hola {E(nombre)}, revisamos tu solicitud para <b>{E(tipo)}</b> y por ahora no pudimos aprobarla.</p>" +
            $"<p><b>Motivo:</b> {E(motivo)}</p><p>Puedes corregir los datos y volver a enviarla.</p>",
            "Ver mi solicitud", urlSolicitud);

    public static string OrganizadorVencido(string nombre) =>
        Layout("Tu periodo como organizador terminó",
            $"<p>Hola {E(nombre)}, terminó el plazo en el que tu cuenta podía crear eventos. Tus eventos ya publicados siguen vigentes.</p>" +
            "<p>Si quieres seguir organizando, envía una nueva solicitud desde «Organiza tu evento».</p>");

    public static string PersonalAsignado(string nombre, Evento ev, string urlPuerta) =>
        Layout("Validarás las entradas en la puerta 🚪",
            $"<p>Hola {E(nombre)}, te asignaron como personal de control de <b>{E(ev.Titulo)}</b> ({ev.Fecha:dd/MM/yyyy HH:mm}, {E(ev.Ubicacion)}).</p>" +
            "<p>El día del evento inicia sesión con esta cuenta y entra a «Puerta» para escanear los códigos QR de las entradas.</p>",
            "Ir a la puerta", urlPuerta);

    public static string PersonalAprobado(string nombre, string evento, IEnumerable<string> correos) =>
        Layout("Tu personal de control fue aprobado",
            $"<p>Hola {E(nombre)}, ya están asignadas para validar las entradas de <b>{E(evento)}</b> estas cuentas:</p>" +
            $"<ul>{string.Join("", correos.Select(c => $"<li>{E(c)}</li>"))}</ul>");

    // HU30: cupón personal de una promoción
    public static string CuponPromocion(string nombre, Promocion p, string codigo, string urlCatalogo) =>
        Layout("¡Tienes un cupón de descuento! 🎁",
            $"<p>Hola {E(nombre)}, gracias por ser parte de ActivaFest. Te regalamos <b>{E(p.Etiqueta)}</b> en tu próxima compra de entradas.</p>" +
            $"<p style=\"text-align:center;margin:24px 0;\"><span style=\"display:inline-block;padding:12px 22px;border:2px dashed #ff006e;border-radius:10px;font-size:22px;font-weight:bold;letter-spacing:2px;color:#111;\">{E(codigo)}</span></p>" +
            $"<p>Válido del <b>{p.FechaInicio:dd/MM/yyyy}</b> al <b>{p.FechaFin:dd/MM/yyyy}</b>. Escríbelo en el campo «Cupón de descuento» al comprar.</p>" +
            "<p style=\"color:#888;font-size:13px;\">El cupón es personal: solo funciona con tu cuenta y se puede usar una sola vez." +
            (p.LimiteUsos != null ? " La promoción tiene cupos limitados." : "") + "</p>" +
            "<p style=\"color:#888;font-size:12px;\">Recibes este correo porque aceptaste recibir promociones. Puedes desactivarlas en tu perfil.</p>",
            "Ver eventos", urlCatalogo);

    public static string EntradasCompradas(string nombre, Orden orden, Evento evento, string urlMisEntradas)
    {
        var filas = string.Join("", orden.Items
            .GroupBy(i => (Zona: i.TipoEntrada?.Nombre, i.PrecioUnitario))
            .Select(g => (g.Key.Zona, Cantidad: g.Sum(i => i.Cantidad), Subtotal: g.Sum(i => i.Subtotal)))
            .Select(i =>
            $"<tr><td style=\"padding:6px 0;\">{i.Cantidad} × {E(i.Zona)}</td>" +
            $"<td style=\"padding:6px 0;text-align:right;\">{Formato.Precio(i.Subtotal)}</td></tr>"));

        return Layout("¡Tus entradas están listas! 🎟️",
            $"<p>Hola {E(nombre)}, tu compra para <b>{E(evento.Titulo)}</b> fue confirmada.</p>" +
            $"<p>📅 {Formato.Fecha(evento.Fecha, "dddd dd 'de' MMMM 'de' yyyy, HH:mm")}<br/>📍 {E(evento.Ubicacion)}</p>" +
            $"<table width=\"100%\" style=\"border-top:1px solid #eee;border-bottom:1px solid #eee;margin:15px 0;\">{filas}" +
            (orden.Descuento > 0
                ? $"<tr><td style=\"padding:6px 0;color:#16a34a;\">Descuento (cupón)</td><td style=\"padding:6px 0;text-align:right;color:#16a34a;\">− {Formato.Precio(orden.Descuento)}</td></tr>"
                : "") +
            $"<tr><td style=\"padding:8px 0;\"><b>Total</b></td><td style=\"padding:8px 0;text-align:right;\"><b>{Formato.Precio(orden.Total)}</b></td></tr></table>" +
            UbicacionesHtml(orden) +
            $"<p>Adjuntamos <b>{orden.Entradas.Count}</b> {(orden.Entradas.Count == 1 ? "código QR" : "códigos QR")}, uno por persona, " +
            "y el <b>plano del recinto con tu ubicación resaltada</b> (plano-tu-ubicacion.png). " +
            "Presenta el QR en la puerta desde tu celular o impreso. Cada QR solo se puede usar una vez.</p>" +
            $"<p style=\"color:#888;font-size:13px;\">N.° de orden: {E(orden.Codigo)}</p>",
            "Ver mis entradas", urlMisEntradas);
    }

    // HU27: zona, fila, asiento y puerta de cada entrada
    private static string UbicacionesHtml(Orden orden)
    {
        if (orden.Entradas.Count == 0)
            return "";

        var filas = string.Join("", orden.Entradas.Select((e, i) =>
        {
            var u = UbicacionEntrada.De(e);
            return "<tr>" +
                   $"<td style=\"padding:6px 8px;\">#{i + 1}</td>" +
                   $"<td style=\"padding:6px 8px;\"><b>{E(u.Zona)}</b></td>" +
                   $"<td style=\"padding:6px 8px;\">{(u.TieneAsiento ? $"Fila {E(u.Fila)}, asiento {u.Numero}" : "Ubicación libre en la zona")}</td>" +
                   $"<td style=\"padding:6px 8px;\">{(string.IsNullOrEmpty(u.Puerta) ? "—" : "🚪 " + E(u.Puerta))}</td>" +
                   "</tr>";
        }));

        return "<p style=\"margin-bottom:6px;\"><b>Tu ubicación</b></p>" +
               "<table width=\"100%\" style=\"background:#f6f4ff;border-radius:10px;margin-bottom:15px;font-size:14px;\">" +
               "<tr style=\"color:#666;font-size:12px;\"><td style=\"padding:6px 8px;\">Entrada</td><td style=\"padding:6px 8px;\">Zona</td>" +
               "<td style=\"padding:6px 8px;\">Asiento</td><td style=\"padding:6px 8px;\">Puerta</td></tr>" + filas + "</table>";
    }

    public static string RecuperarContrasena(string nombre, string urlReset) =>
        Layout("Restablecer contraseña",
            $"<p>Hola {E(nombre)}, recibimos una solicitud para restablecer tu contraseña.</p>" +
            "<p>El enlace vence en <b>1 hora</b> y solo se puede usar una vez. Si no fuiste tú, ignora este mensaje: tu contraseña no cambiará.</p>",
            "Restablecer contraseña", urlReset);
}
