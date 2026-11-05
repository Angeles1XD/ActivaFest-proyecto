using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Email;
using ActivaFest.Services.Notificaciones;
using ActivaFest.Services.Panel;

namespace ActivaFest.Services.Asistente;

// ---------- Dashboard que el chat dibuja como tarjeta (indicadores, barras y ventas por día) ----------
public record IndicadorChat(string Etiqueta, string Valor);
public record BarraChat(string Etiqueta, double Porcentaje, string Detalle);
public record PuntoChat(string Etiqueta, int Valor);

public class DashboardChat
{
    public string Titulo { get; set; } = "";
    public List<IndicadorChat> Indicadores { get; set; } = new();
    public string? TituloBarras { get; set; }
    public List<BarraChat> Barras { get; set; } = new();
    public string? TituloSerie { get; set; }
    public List<PuntoChat> Serie { get; set; } = new();
}

public record ResultadoReserva(bool Reservada, string Mensaje, string? Codigo = null, string? Zona = null, int Cantidad = 0,
    decimal Total = 0, DateTime? Vence = null, List<string>? ZonasDisponibles = null, string? UrlSiguiente = null);

public record ResultadoRecordatorio(bool Activo, string Mensaje, int? EventoId = null);

public record AvisoEvento(int EventoId, string Evento, DateTime Fecha, string Ubicacion, bool TieneEntradas, bool Recordatorio)
{
    public string Falta(DateTime ahora)
    {
        var t = Fecha - ahora;
        return t.TotalHours < 1 ? "empieza en menos de una hora"
            : t.TotalHours < 24 ? $"empieza en {(int)t.TotalHours} h"
            : t.TotalDays < 2 ? "es mañana"
            : $"faltan {(int)t.TotalDays} días";
    }
}

public record EventoPublicacion(int Id, string Titulo, DateTime Fecha, string Estado, string? Detalle);

public class EstadoPublicaciones
{
    public List<EventoPublicacion> Publicados { get; set; } = new();
    public List<EventoPublicacion> Pendientes { get; set; } = new();
    public List<EventoPublicacion> PorPagar { get; set; } = new();
    public List<EventoPublicacion> Rechazados { get; set; } = new();
    public List<EventoPublicacion> Finalizados { get; set; } = new();
    public List<EventoPublicacion> Cancelados { get; set; } = new();
    public int Total => Publicados.Count + Pendientes.Count + PorPagar.Count + Rechazados.Count + Finalizados.Count + Cancelados.Count;
}

public record VentaEventoOrganizador(int Id, string Titulo, DateTime Fecha, string Estado, int Vendidas, int Aforo, decimal Ingresos);

public class VentasOrganizador
{
    public bool Encontrado { get; set; } = true;
    public string? Evento { get; set; }
    public int? EventoId { get; set; }
    public int Vendidas { get; set; }
    public int Aforo { get; set; }
    public decimal Ingresos { get; set; }
    public decimal Comision { get; set; }
    public decimal Neto => Ingresos - Comision;
    public decimal? PorCobrar { get; set; }
    public int OrdenesPagadas { get; set; }
    public int Ingresados { get; set; }
    public int Reservadas { get; set; }
    public int VendidasUltimos7Dias { get; set; }
    public decimal IngresosUltimos7Dias { get; set; }
    public int? Visitas { get; set; }
    public List<ZonaPanel> Zonas { get; set; } = new();
    public List<VentaEventoOrganizador> Eventos { get; set; } = new();
    public DashboardChat Dashboard { get; set; } = new();

    public double Ocupacion => Aforo == 0 ? 0 : (double)Vendidas / Aforo;
    public decimal TicketPromedio => OrdenesPagadas == 0 ? 0 : Ingresos / OrdenesPagadas;
}

public interface IAccionesAsistente
{
    // Límites que el asistente le anticipa al usuario antes de pedirle que confirme
    int MaximoPorOrden { get; }
    int MinutosReserva { get; }

    // Usuario: aparta entradas (la reserva dura unos minutos y se paga en el checkout)
    Task<ResultadoReserva> ReservarAsync(string usuarioId, int eventoId, string? zona, int cantidad, CancellationToken ct = default);

    // Usuario: las reservas que faltan pagar (las mismas de la página Carrito)
    Task<Carrito> CarritoAsync(string usuarioId, CancellationToken ct = default);

    // Usuario: avisos por correo antes de un evento que aún no ocurre
    Task<ResultadoRecordatorio> ActivarRecordatorioAsync(string usuarioId, int eventoId, CancellationToken ct = default);
    Task<ResultadoRecordatorio> QuitarRecordatorioAsync(string usuarioId, int eventoId, CancellationToken ct = default);
    Task<List<AvisoEvento>> AvisosAsync(string usuarioId, CancellationToken ct = default);

    // Organizador: qué está publicado y qué espera aprobación
    Task<EstadoPublicaciones> EstadoPublicacionesAsync(string organizadorId, CancellationToken ct = default);

    // Organizador: dashboard de ventas de todos sus eventos o de uno (eventoId)
    Task<VentasOrganizador> VentasAsync(string organizadorId, int? eventoId, CancellationToken ct = default);

    // Tarea en segundo plano: envía los avisos de los eventos que empiezan pronto
    Task<int> EnviarRecordatoriosAsync(CancellationToken ct = default);

    // Organizador: notificación cuando el administrador apruebe o rechace su evento
    Task<ResultadoRecordatorio> AvisarAprobacionAsync(string organizadorId, int eventoId, CancellationToken ct = default);

    // Administrador: notificación cada vez que lleguen eventos nuevos por aprobar (activar = false la quita)
    Task<ResultadoRecordatorio> AvisarEventosPorAprobarAsync(string administradorId, bool activar, CancellationToken ct = default);

    // Control de acceso: recordatorio de cada evento que tiene asignado
    Task<ResultadoRecordatorio> RecordarMisTurnosAsync(string usuarioId, CancellationToken ct = default);

    // Tarea en segundo plano: revisa las alertas de organizadores y administradores
    Task<int> ProcesarAlertasAsync(CancellationToken ct = default);
}

// Qué es la persona respecto del evento: así cada rol recibe el recordatorio que le sirve
public enum RelacionEvento { Usuario, Organizador, Personal }

// =============================
// ⚡ ACCIONES DEL ASISTENTE VIRTUAL (por rol)
// =============================
// Las usan el agente de IA (FuncionesAgente) y el asistente base (AsistenteService), así ambos hacen lo mismo
// aunque el modelo no esté disponible. El usuario siempre llega fijado desde la sesión del servidor.
public class AccionesAsistente : IAccionesAsistente
{
    private readonly ApplicationDbContext _db;
    private readonly ICompraService _compras;
    private readonly IPanelService _panel;
    private readonly IEmailService _email;
    private readonly ComprasOptions _opcionesCompra;
    private readonly IConfiguration _config;
    private readonly ILogger<AccionesAsistente> _logger;

    public AccionesAsistente(ApplicationDbContext db, ICompraService compras, IPanelService panel, IEmailService email,
        IOptions<ComprasOptions> opcionesCompra, IConfiguration config, ILogger<AccionesAsistente> logger)
    {
        _db = db;
        _compras = compras;
        _panel = panel;
        _email = email;
        _opcionesCompra = opcionesCompra.Value;
        _config = config;
        _logger = logger;
    }

    public int MaximoPorOrden => _opcionesCompra.MaxEntradasPorOrden;
    public int MinutosReserva => _opcionesCompra.MinutosReserva;

    // =============================
    // 🎟️ RESERVAR ENTRADAS
    // =============================
    public Task<Carrito> CarritoAsync(string usuarioId, CancellationToken ct = default) =>
        new CarritoService(_db).ObtenerAsync(usuarioId, ct);

    public async Task<ResultadoReserva> ReservarAsync(string usuarioId, int eventoId, string? zona, int cantidad, CancellationToken ct = default)
    {
        var evento = await _db.Eventos.AsNoTracking().Include(e => e.TiposEntrada)
            .FirstOrDefaultAsync(e => e.Id == eventoId && e.Estado == EstadoEvento.Aprobado, ct);
        if (evento == null)
            return new ResultadoReserva(false, "No encontré ese evento publicado.");

        var ficha = $"/Eventos/Detalle/{evento.Id}#comprar";
        if (evento.EsGratuito)
            return new ResultadoReserva(false, $"«{evento.Titulo}» es de entrada libre: no necesitas reservar entrada.", UrlSiguiente: ficha);
        if (evento.YaOcurrio(DateTime.Now))
            return new ResultadoReserva(false, $"La venta de «{evento.Titulo}» ya terminó.", UrlSiguiente: ficha);

        var zonas = evento.TiposEntrada.OrderBy(t => t.Precio).ToList();
        var conStock = zonas.Where(t => t.Disponibles > 0).ToList();
        var nombres = conStock.Select(t => $"{t.Nombre} ({Formato.Precio(t.Precio)})").ToList();
        if (conStock.Count == 0)
            return new ResultadoReserva(false, $"Las entradas de «{evento.Titulo}» están agotadas.", UrlSiguiente: ficha);

        TipoEntrada? elegida;
        if (!string.IsNullOrWhiteSpace(zona))
        {
            var buscada = BaseConocimiento.Normalizar(zona);
            elegida = zonas.FirstOrDefault(t => BaseConocimiento.Normalizar(t.Nombre) == buscada)
                      ?? zonas.FirstOrDefault(t => BaseConocimiento.Normalizar(t.Nombre).Contains(buscada) || buscada.Contains(BaseConocimiento.Normalizar(t.Nombre)));
            if (elegida == null)
                return new ResultadoReserva(false, $"«{evento.Titulo}» no tiene la zona «{zona}». Zonas disponibles: {string.Join(", ", nombres)}.",
                    ZonasDisponibles: conStock.Select(t => t.Nombre).ToList());
        }
        else if (conStock.Count == 1)
        {
            elegida = conStock[0];
        }
        else
        {
            return new ResultadoReserva(false, $"¿En qué zona quieres tus entradas para «{evento.Titulo}»? Disponibles: {string.Join(", ", nombres)}.",
                ZonasDisponibles: conStock.Select(t => t.Nombre).ToList());
        }

        // Asientos numerados: el asiento exacto se elige en el plano de la ficha
        if (elegida.AsientosNumerados)
            return new ResultadoReserva(false, $"En la zona {elegida.Nombre} de «{evento.Titulo}» eliges tu asiento exacto en el plano. Te dejo el enlace para elegirlo.",
                Zona: elegida.Nombre, UrlSiguiente: ficha);

        cantidad = Math.Clamp(cantidad, 1, _opcionesCompra.MaxEntradasPorOrden);
        if (elegida.Disponibles < cantidad)
            return new ResultadoReserva(false, $"En {elegida.Nombre} solo quedan {elegida.Disponibles} entradas para «{evento.Titulo}».",
                Zona: elegida.Nombre, ZonasDisponibles: conStock.Select(t => t.Nombre).ToList());

        var resultado = await _compras.CrearOrdenAsync(usuarioId, evento.Id, new Dictionary<int, int> { [elegida.Id] = cantidad }, null, ct);
        if (resultado.Orden == null)
            return new ResultadoReserva(false, resultado.Error ?? "No pude hacer la reserva. Inténtalo desde la ficha del evento.", UrlSiguiente: ficha);

        var orden = resultado.Orden;
        var minutos = Math.Max(1, (int)Math.Round((orden.FechaExpiracion - DateTime.UtcNow).TotalMinutes));
        return new ResultadoReserva(true,
            $"✅ Reservé {cantidad} {(cantidad == 1 ? "entrada" : "entradas")} {elegida.Nombre} para «{evento.Titulo}» por {Formato.Monto(orden.Total)}. " +
            $"Quedan apartadas {minutos} minutos: paga antes de que venza la reserva para recibir tus QR (orden {orden.Codigo}).",
            orden.Codigo, elegida.Nombre, cantidad, orden.Total, orden.FechaExpiracion, UrlSiguiente: $"/Compras/Checkout?codigo={Uri.EscapeDataString(orden.Codigo)}");
    }

    // =============================
    // 🔔 RECORDATORIOS DE EVENTOS
    // =============================
    public async Task<ResultadoRecordatorio> ActivarRecordatorioAsync(string usuarioId, int eventoId, CancellationToken ct = default)
    {
        var evento = await _db.Eventos.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventoId && e.Estado == EstadoEvento.Aprobado, ct);
        if (evento == null)
            return new ResultadoRecordatorio(false, "No encontré ese evento publicado.");
        if (evento.Fecha <= DateTime.Now)
            return new ResultadoRecordatorio(false, $"«{evento.Titulo}» ya empezó o ya se realizó: no puedo programar un aviso.", evento.Id);

        var cuando = $"{Formato.Fecha(evento.Fecha, "dddd dd 'de' MMMM")} a las {evento.Fecha:HH:mm}";
        if (await _db.RecordatoriosEvento.AnyAsync(r => r.UsuarioId == usuarioId && r.EventoId == eventoId, ct))
            return new ResultadoRecordatorio(true, $"🔔 Ya tenías activado el aviso de «{evento.Titulo}» ({cuando}).", evento.Id);

        _db.RecordatoriosEvento.Add(new RecordatorioEvento { UsuarioId = usuarioId, EventoId = eventoId });

        // Confirmación en el panel de notificaciones, con el texto según su relación con el evento
        var relacion = await RelacionAsync(usuarioId, evento, ct);
        var cuandoAviso = evento.Fecha - DateTime.Now <= TimeSpan.FromHours(RecordatorioEvento.HorasAntes)
            ? "en unos minutos" : $"el {evento.Fecha.AddHours(-RecordatorioEvento.HorasAntes):dd/MM 'a las' HH:mm}";
        _db.Notificaciones.Add(NotificacionService.Nueva(usuarioId, "⏰",
            relacion switch
            {
                RelacionEvento.Organizador => $"Recordatorio de tu evento «{evento.Titulo}» programado",
                RelacionEvento.Personal => $"Recordatorio de tu turno en «{evento.Titulo}» programado",
                _ => $"Recordatorio de «{evento.Titulo}» programado"
            },
            $"Te avisaré {cuandoAviso}. El evento es el {cuando}.",
            UrlPara(relacion, evento.Id, false), evento.Id));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Dos pedidos simultáneos: el índice único deja solo uno
            return new ResultadoRecordatorio(true, $"🔔 Ya tenías activado el aviso de «{evento.Titulo}» ({cuando}).", evento.Id);
        }

        var horas = evento.Fecha - DateTime.Now <= TimeSpan.FromHours(RecordatorioEvento.HorasAntes)
            ? "en unos minutos, porque ya falta poco"
            : $"{RecordatorioEvento.HorasAntes} horas antes";
        return new ResultadoRecordatorio(true,
            $"🔔 Listo, te avisaré de «{evento.Titulo}» ({cuando}). El recordatorio te llegará a tus notificaciones 🔔 (y a tu correo) {horas}, y puedes preguntarme por tus avisos cuando quieras.", evento.Id);
    }

    public async Task<ResultadoRecordatorio> QuitarRecordatorioAsync(string usuarioId, int eventoId, CancellationToken ct = default)
    {
        var recordatorio = await _db.RecordatoriosEvento.Include(r => r.Evento)
            .FirstOrDefaultAsync(r => r.UsuarioId == usuarioId && r.EventoId == eventoId, ct);
        if (recordatorio == null)
            return new ResultadoRecordatorio(false, "No tenías un aviso activo para ese evento.");

        _db.RecordatoriosEvento.Remove(recordatorio);
        await _db.SaveChangesAsync(ct);
        return new ResultadoRecordatorio(false, $"🔕 Quité el aviso de «{recordatorio.Evento?.Titulo}».", eventoId);
    }

    // Próximos eventos del usuario: los que tienen aviso y aquellos para los que tiene entradas
    public async Task<List<AvisoEvento>> AvisosAsync(string usuarioId, CancellationToken ct = default)
    {
        var ahora = DateTime.Now;
        var conAviso = await _db.RecordatoriosEvento.AsNoTracking()
            .Where(r => r.UsuarioId == usuarioId && r.Evento!.Estado == EstadoEvento.Aprobado && r.Evento.Fecha > ahora)
            .Select(r => r.EventoId)
            .ToListAsync(ct);
        var conEntradas = await _db.Entradas.AsNoTracking()
            .Where(e => e.UsuarioId == usuarioId && e.Estado == EstadoEntrada.Activa && e.Evento!.Fecha > ahora)
            .Select(e => e.EventoId)
            .Distinct()
            .ToListAsync(ct);

        var ids = conAviso.Union(conEntradas).ToList();
        var eventos = await _db.Eventos.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Titulo, e.Fecha, e.Ubicacion })
            .ToListAsync(ct);

        return eventos
            .OrderBy(e => e.Fecha)
            .Select(e => new AvisoEvento(e.Id, e.Titulo, e.Fecha, e.Ubicacion, conEntradas.Contains(e.Id), conAviso.Contains(e.Id)))
            .ToList();
    }

    public async Task<int> EnviarRecordatoriosAsync(CancellationToken ct = default)
    {
        var ahora = DateTime.Now;
        var limite = ahora.AddHours(RecordatorioEvento.HorasAntes);
        var pendientes = await _db.RecordatoriosEvento
            .Include(r => r.Evento)
            .Where(r => r.EnviadoEn == null && r.Evento!.Estado == EstadoEvento.Aprobado && r.Evento.Fecha > ahora && r.Evento.Fecha <= limite)
            .Take(100)
            .ToListAsync(ct);
        if (pendientes.Count == 0)
            return 0;

        var usuarios = pendientes.Select(r => r.UsuarioId).Distinct().ToList();
        var datos = await _db.Users.AsNoTracking()
            .Where(u => usuarios.Contains(u.Id) && u.Activo)
            .Select(u => new { u.Id, u.Email, u.NombreCompleto })
            .ToDictionaryAsync(u => u.Id, ct);
        var baseUrl = (_config["App:UrlPublica"] ?? "http://localhost:5054").TrimEnd('/');

        foreach (var r in pendientes)
        {
            var tieneEntradas = await _db.Entradas.AnyAsync(e => e.UsuarioId == r.UsuarioId && e.EventoId == r.EventoId && e.Estado == EstadoEntrada.Activa, ct);
            _db.Notificaciones.Add(await NotificacionRecordatorioAsync(r.UsuarioId, r.Evento!, tieneEntradas, ct));

            if (datos.TryGetValue(r.UsuarioId, out var u) && !string.IsNullOrWhiteSpace(u.Email))
                await _email.EnviarAsync(u.Email, $"🔔 Recordatorio: {r.Evento!.Titulo}",
                    EmailTemplates.RecordatorioEvento(u.NombreCompleto, r.Evento, tieneEntradas, $"{baseUrl}/Eventos/Detalle/{r.EventoId}"));
            r.EnviadoEn = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("🔔 Se enviaron {Total} recordatorios de eventos", pendientes.Count);
        return pendientes.Count;
    }

    private async Task<RelacionEvento> RelacionAsync(string usuarioId, Evento evento, CancellationToken ct)
    {
        if (evento.OrganizadorId == usuarioId)
            return RelacionEvento.Organizador;
        return await _db.AsignacionesAcceso.AnyAsync(a => a.UsuarioId == usuarioId && a.EventoId == evento.Id && a.FinalizadaEn == null, ct)
            ? RelacionEvento.Personal : RelacionEvento.Usuario;
    }

    private static string UrlPara(RelacionEvento relacion, int eventoId, bool tieneEntradas) => relacion switch
    {
        RelacionEvento.Organizador => $"/Panel/Evento/{eventoId}",
        RelacionEvento.Personal => "/Acceso",
        _ => tieneEntradas ? "/Entradas" : $"/Eventos/Detalle/{eventoId}"
    };

    // El recordatorio que llega al panel: el organizador ve sus ventas, el personal su turno y el usuario sus entradas
    private async Task<Notificacion> NotificacionRecordatorioAsync(string usuarioId, Evento evento, bool tieneEntradas, CancellationToken ct)
    {
        var relacion = await RelacionAsync(usuarioId, evento, ct);
        var cuando = $"{Formato.Fecha(evento.Fecha, "dddd dd/MM")} a las {evento.Fecha:HH:mm}";
        var url = UrlPara(relacion, evento.Id, tieneEntradas);

        switch (relacion)
        {
            case RelacionEvento.Organizador:
                var zonas = await _db.TiposEntrada.AsNoTracking().Where(t => t.EventoId == evento.Id)
                    .Select(t => new { t.Vendidas, t.Capacidad }).ToListAsync(ct);
                return NotificacionService.Nueva(usuarioId, "🎤", $"Tu evento «{evento.Titulo}» ya se acerca",
                    $"Empieza el {cuando} en {evento.Ubicacion}. Llevas {zonas.Sum(z => z.Vendidas):N0} de {zonas.Sum(z => z.Capacidad):N0} entradas vendidas.",
                    url, evento.Id);
            case RelacionEvento.Personal:
                return NotificacionService.Nueva(usuarioId, "🛂", $"Tu turno de control en «{evento.Titulo}»",
                    $"El ingreso se habilita el {evento.InicioIngreso:dd/MM 'a las' HH:mm} en {evento.Ubicacion}. Abre la sección Puerta para escanear las entradas.",
                    url, evento.Id);
            default:
                return NotificacionService.Nueva(usuarioId, "🔔", $"«{evento.Titulo}» ya se acerca",
                    $"Es el {cuando} en {evento.Ubicacion}. " +
                    (tieneEntradas ? "Ya tienes tus entradas con QR en \"Mis entradas\"." : "Aún no tienes entradas: ¡consíguelas antes de que se agoten!"),
                    url, evento.Id);
        }
    }

    // =============================
    // 🚨 ALERTAS POR ROL (organizador, administrador, control de acceso)
    // =============================
    public async Task<ResultadoRecordatorio> AvisarAprobacionAsync(string organizadorId, int eventoId, CancellationToken ct = default)
    {
        var evento = await _db.Eventos.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventoId && e.OrganizadorId == organizadorId, ct);
        if (evento == null)
            return new ResultadoRecordatorio(false, "No encontré ese evento entre los tuyos.");
        if (evento.Estado != EstadoEvento.Pendiente)
            return new ResultadoRecordatorio(false, evento.Estado == EstadoEvento.Aprobado
                ? $"«{evento.Titulo}» ya está aprobado y publicado ✅."
                : $"«{evento.Titulo}» ya fue revisado: está {evento.Estado.ToString().ToLowerInvariant()}.", evento.Id);
        if (evento.PendientePagoPublicacion)
            return new ResultadoRecordatorio(false, $"«{evento.Titulo}» aún no pasa a revisión: primero falta pagar su publicación.", evento.Id);

        if (!await _db.AlertasAsistente.AnyAsync(a => a.UsuarioId == organizadorId && a.Tipo == TipoAlertaAsistente.AprobacionEvento && a.EventoId == eventoId && a.Activa, ct))
        {
            _db.AlertasAsistente.Add(new AlertaAsistente { UsuarioId = organizadorId, Tipo = TipoAlertaAsistente.AprobacionEvento, EventoId = eventoId });
            _db.Notificaciones.Add(NotificacionService.Nueva(organizadorId, "⏳", $"Te avisaré cuando revisen «{evento.Titulo}»",
                "Apenas el administrador lo apruebe o lo rechace, te llegará una notificación aquí.", "/Eventos/MisEventos", eventoId));
            await _db.SaveChangesAsync(ct);
        }
        return new ResultadoRecordatorio(true, $"⏳ Listo, te aviso en tus notificaciones 🔔 apenas el administrador revise «{evento.Titulo}».", evento.Id);
    }

    public async Task<ResultadoRecordatorio> AvisarEventosPorAprobarAsync(string administradorId, bool activar, CancellationToken ct = default)
    {
        var alertas = await _db.AlertasAsistente
            .Where(a => a.UsuarioId == administradorId && a.Tipo == TipoAlertaAsistente.EventosPorAprobar && a.Activa)
            .ToListAsync(ct);

        if (!activar)
        {
            alertas.ForEach(a => a.Activa = false);
            await _db.SaveChangesAsync(ct);
            return new ResultadoRecordatorio(false, alertas.Count == 0
                ? "No tenías activado el aviso de eventos por aprobar."
                : "🔕 Ya no te avisaré de los eventos por aprobar.");
        }

        var pendientes = await _db.Eventos.CountAsync(e => e.Estado == EstadoEvento.Pendiente && !e.PendientePagoPublicacion, ct);
        var ahora = pendientes == 0 ? "Ahora no hay ninguno pendiente." : $"Ahora hay {pendientes} {(pendientes == 1 ? "evento esperando" : "eventos esperando")} tu revisión.";
        if (alertas.Count == 0)
        {
            _db.AlertasAsistente.Add(new AlertaAsistente { UsuarioId = administradorId, Tipo = TipoAlertaAsistente.EventosPorAprobar });
            _db.Notificaciones.Add(NotificacionService.Nueva(administradorId, "🛡️", "Aviso de eventos por aprobar activado",
                $"Te avisaré aquí cada vez que lleguen eventos nuevos para moderar. {ahora}", "/Admin"));
            await _db.SaveChangesAsync(ct);
        }
        return new ResultadoRecordatorio(true, $"🛡️ Listo, te aviso en tus notificaciones 🔔 cada vez que lleguen eventos por aprobar. {ahora}");
    }

    public async Task<ResultadoRecordatorio> RecordarMisTurnosAsync(string usuarioId, CancellationToken ct = default)
    {
        var ahora = DateTime.Now;
        var turnos = await _db.AsignacionesAcceso.AsNoTracking()
            .Where(a => a.UsuarioId == usuarioId && a.FinalizadaEn == null && a.Evento!.Estado == EstadoEvento.Aprobado && a.Evento.Fecha > ahora)
            .Select(a => a.EventoId)
            .Distinct()
            .ToListAsync(ct);
        if (turnos.Count == 0)
            return new ResultadoRecordatorio(false, "No tienes eventos asignados próximamente para controlar el ingreso.");

        foreach (var id in turnos)
            await ActivarRecordatorioAsync(usuarioId, id, ct);
        return new ResultadoRecordatorio(true,
            $"🛂 Listo, programé el recordatorio de tus {turnos.Count} {(turnos.Count == 1 ? "turno" : "turnos")} de control de acceso. Te llegarán a tus notificaciones 🔔 y a tu correo antes de cada evento.");
    }

    public async Task<int> ProcesarAlertasAsync(CancellationToken ct = default)
    {
        var alertas = await _db.AlertasAsistente.Include(a => a.Evento)
            .Where(a => a.Activa)
            .Take(200)
            .ToListAsync(ct);
        var enviadas = 0;

        foreach (var a in alertas)
        {
            if (a.Tipo == TipoAlertaAsistente.AprobacionEvento)
            {
                var ev = a.Evento;
                if (ev == null || ev.Estado == EstadoEvento.Pendiente)
                    continue;

                _db.Notificaciones.Add(ev.Estado switch
                {
                    EstadoEvento.Aprobado => NotificacionService.Nueva(a.UsuarioId, "✅", $"¡Aprobaron tu evento «{ev.Titulo}»!",
                        "Ya está publicado en el catálogo y puede empezar a vender entradas.", $"/Eventos/Detalle/{ev.Id}", ev.Id),
                    EstadoEvento.Rechazado => NotificacionService.Nueva(a.UsuarioId, "❌", $"«{ev.Titulo}» no fue aprobado",
                        string.IsNullOrWhiteSpace(ev.MotivoRechazo) ? "Revisa la información y vuelve a enviarlo." : $"Motivo: {ev.MotivoRechazo}", "/Eventos/MisEventos", ev.Id),
                    _ => NotificacionService.Nueva(a.UsuarioId, "🚫", $"«{ev.Titulo}» fue cancelado", ev.MotivoCancelacion ?? "", "/Eventos/MisEventos", ev.Id)
                });
                a.Activa = false;
                enviadas++;
            }
            else
            {
                var nuevos = await _db.Eventos.CountAsync(e => e.Estado == EstadoEvento.Pendiente && !e.PendientePagoPublicacion && e.FechaCreacion > a.UltimaRevision, ct);
                if (nuevos > 0)
                {
                    var total = await _db.Eventos.CountAsync(e => e.Estado == EstadoEvento.Pendiente && !e.PendientePagoPublicacion, ct);
                    _db.Notificaciones.Add(NotificacionService.Nueva(a.UsuarioId, "🛡️",
                        nuevos == 1 ? "Llegó un evento nuevo por aprobar" : $"Llegaron {nuevos} eventos nuevos por aprobar",
                        $"En total hay {total} {(total == 1 ? "evento esperando" : "eventos esperando")} tu revisión.", "/Admin"));
                    enviadas++;
                }
                a.UltimaRevision = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
        return enviadas;
    }

    // =============================
    // 📋 ESTADO DE PUBLICACIÓN (organizador)
    // =============================
    public async Task<EstadoPublicaciones> EstadoPublicacionesAsync(string organizadorId, CancellationToken ct = default)
    {
        var eventos = await _db.Eventos.AsNoTracking()
            .Where(e => e.OrganizadorId == organizadorId)
            .Select(e => new { e.Id, e.Titulo, e.Fecha, e.Estado, e.PendientePagoPublicacion, e.MotivoRechazo, e.MotivoCancelacion, e.FechaCreacion, e.FechaModeracion })
            .ToListAsync(ct);

        var ahora = DateTime.Now;
        var estado = new EstadoPublicaciones();
        foreach (var e in eventos.OrderBy(e => e.Fecha))
        {
            switch (e.Estado)
            {
                case EstadoEvento.Aprobado when e.Fecha >= ahora.AddHours(-Evento.HorasValidezEntradas):
                    estado.Publicados.Add(new(e.Id, e.Titulo, e.Fecha, "Publicado", e.FechaModeracion != null ? $"aprobado el {e.FechaModeracion.Value.ToLocalTime():dd/MM}" : null));
                    break;
                case EstadoEvento.Aprobado:
                    estado.Finalizados.Add(new(e.Id, e.Titulo, e.Fecha, "Finalizado", null));
                    break;
                case EstadoEvento.Pendiente when e.PendientePagoPublicacion:
                    estado.PorPagar.Add(new(e.Id, e.Titulo, e.Fecha, "Falta pagar la publicación", null));
                    break;
                case EstadoEvento.Pendiente:
                    var dias = (int)(DateTime.UtcNow - e.FechaCreacion).TotalDays;
                    estado.Pendientes.Add(new(e.Id, e.Titulo, e.Fecha, "Pendiente de aprobación",
                        dias == 0 ? "enviado hoy" : $"enviado hace {dias} {(dias == 1 ? "día" : "días")}"));
                    break;
                case EstadoEvento.Rechazado:
                    estado.Rechazados.Add(new(e.Id, e.Titulo, e.Fecha, "Rechazado", e.MotivoRechazo));
                    break;
                case EstadoEvento.Cancelado:
                    estado.Cancelados.Add(new(e.Id, e.Titulo, e.Fecha, "Cancelado", e.MotivoCancelacion));
                    break;
            }
        }
        estado.Finalizados.Reverse(); // el más reciente primero
        return estado;
    }

    // =============================
    // 📊 DASHBOARD DE VENTAS (organizador)
    // =============================
    // Las cifras salen del mismo PanelService que la página del panel, así el chat y el panel siempre coinciden
    public async Task<VentasOrganizador> VentasAsync(string organizadorId, int? eventoId, CancellationToken ct = default)
    {
        var desde7 = DateTime.UtcNow.AddDays(-7);

        if (eventoId != null)
        {
            var detalle = await _panel.DetalleAsync(eventoId.Value, organizadorId, false, ct);
            if (detalle == null)
                return new VentasOrganizador { Encontrado = false };

            var ventas7 = await _db.Ordenes.AsNoTracking()
                .Where(o => o.EventoId == eventoId && o.Estado == EstadoOrden.Pagada && o.FechaPago >= desde7)
                .Select(o => new { o.Total, Entradas = o.Items.Sum(i => i.Cantidad) })
                .ToListAsync(ct);

            var v = new VentasOrganizador
            {
                Evento = detalle.Evento.Titulo,
                EventoId = detalle.Evento.Id,
                Vendidas = detalle.Vendidas,
                Aforo = detalle.Aforo,
                Ingresos = detalle.Ingresos,
                Comision = detalle.Comision,
                PorCobrar = detalle.SaldoPorCobrar,
                OrdenesPagadas = detalle.OrdenesPagadas,
                Ingresados = detalle.Ingresados,
                Reservadas = detalle.Reservadas,
                Visitas = detalle.Visitas,
                VendidasUltimos7Dias = ventas7.Sum(o => o.Entradas),
                IngresosUltimos7Dias = ventas7.Sum(o => o.Total),
                Zonas = detalle.Zonas
            };

            v.Dashboard = new DashboardChat
            {
                Titulo = $"📊 Ventas de {detalle.Evento.Titulo}",
                Indicadores = new()
                {
                    new("Entradas vendidas", $"{v.Vendidas:N0} / {v.Aforo:N0}"),
                    new("Ocupación", Porcentaje(v.Ocupacion)),
                    new("Ingresos", Formato.Monto(v.Ingresos)),
                    new("Neto para ti", Formato.Monto(v.Neto)),
                    new("Por cobrar", Formato.Monto(detalle.SaldoPorCobrar)),
                    new("Órdenes pagadas", v.OrdenesPagadas.ToString("N0")),
                    new("Ya ingresaron", v.Ingresados.ToString("N0")),
                    new("Conversión", detalle.Visitas == 0 ? "—" : Porcentaje(detalle.Conversion))
                },
                TituloBarras = "Ocupación por zona",
                Barras = detalle.Zonas.Select(z => new BarraChat(z.Nombre, z.Ocupacion,
                    $"{z.Vendidas:N0}/{z.Capacidad:N0} · {Formato.Monto(z.Ingresos)}")).ToList(),
                TituloSerie = "Entradas vendidas (últimos 7 días)",
                Serie = detalle.VentasPorDia.TakeLast(7).Select(d => new PuntoChat(d.Dia.ToString("dd/MM", CultureInfo.InvariantCulture), d.Entradas)).ToList()
            };
            return v;
        }

        var eventos = await _panel.ResumenAsync(organizadorId, false, ct);
        var ids = eventos.Select(e => e.Id).ToList();
        var ordenes = await _db.Ordenes.AsNoTracking()
            .Where(o => ids.Contains(o.EventoId) && o.Estado == EstadoOrden.Pagada)
            .Select(o => new { o.Total, o.Comision, o.FechaPago, Entradas = o.Items.Sum(i => i.Cantidad) })
            .ToListAsync(ct);
        var ingresados = await _db.Entradas.CountAsync(e => ids.Contains(e.EventoId) && e.Estado == EstadoEntrada.Usada, ct);

        var conVentas = eventos.Where(e => e.Estado is EstadoEvento.Aprobado or EstadoEvento.Cancelado || e.Vendidas > 0).ToList();
        var total = new VentasOrganizador
        {
            Vendidas = conVentas.Sum(e => e.Vendidas),
            Aforo = eventos.Where(e => e.Estado == EstadoEvento.Aprobado).Sum(e => e.Aforo),
            Ingresos = ordenes.Sum(o => o.Total),
            Comision = ordenes.Sum(o => o.Comision),
            OrdenesPagadas = ordenes.Count,
            Ingresados = ingresados,
            Reservadas = eventos.Sum(e => e.Reservadas),
            VendidasUltimos7Dias = ordenes.Where(o => o.FechaPago >= desde7).Sum(o => o.Entradas),
            IngresosUltimos7Dias = ordenes.Where(o => o.FechaPago >= desde7).Sum(o => o.Total),
            Eventos = conVentas.Select(e => new VentaEventoOrganizador(e.Id, e.Titulo, e.Fecha, e.Estado.ToString(), e.Vendidas, e.Aforo, e.Ingresos)).ToList()
        };

        var ahora = DateTime.Now;
        var porDia = ordenes.Where(o => o.FechaPago >= desde7)
            .GroupBy(o => o.FechaPago!.Value.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.Entradas));

        total.Dashboard = new DashboardChat
        {
            Titulo = "📊 Resumen de ventas de tus eventos",
            Indicadores = new()
            {
                new("Ingresos totales", Formato.Monto(total.Ingresos)),
                new("Entradas vendidas", total.Vendidas.ToString("N0")),
                new("Neto para ti", Formato.Monto(total.Neto)),
                new("Comisión", Formato.Monto(total.Comision)),
                new("Ocupación", Porcentaje(total.Ocupacion)),
                new("Ticket promedio", Formato.Monto(total.TicketPromedio)),
                new("Últimos 7 días", $"{total.VendidasUltimos7Dias:N0} entradas"),
                new("Próximos eventos", eventos.Count(e => e.Estado == EstadoEvento.Aprobado && e.Fecha >= ahora).ToString())
            },
            TituloBarras = "Eventos con más ventas",
            Barras = conVentas.OrderByDescending(e => e.Ingresos).ThenByDescending(e => e.Vendidas).Take(5)
                .Select(e => new BarraChat(e.Titulo, e.Ocupacion, $"{e.Vendidas:N0}/{e.Aforo:N0} · {Formato.Monto(e.Ingresos)}")).ToList(),
            TituloSerie = "Entradas vendidas (últimos 7 días)",
            Serie = Enumerable.Range(0, 7).Select(i => ahora.Date.AddDays(i - 6))
                .Select(d => new PuntoChat(d.ToString("dd/MM", CultureInfo.InvariantCulture), porDia.GetValueOrDefault(d))).ToList()
        };
        return total;
    }

    private static string Porcentaje(double valor) => $"{Math.Round(valor * 100):0} %";
}

// =============================
// 🔔 ENVÍA LOS RECORDATORIOS DE EVENTOS
// =============================
// Cada minuto avisa (panel de notificaciones y correo) a quienes pidieron al asistente que les recordara un evento
// próximo, y revisa las alertas de organizadores (aprobación) y administradores (eventos por aprobar).
public class RecordatoriosEventosService : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RecordatoriosEventosService> _logger;

    public RecordatoriosEventosService(IServiceScopeFactory scopes, ILogger<RecordatoriosEventosService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(Intervalo);

        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var acciones = scope.ServiceProvider.GetRequiredService<IAccionesAsistente>();
                await acciones.EnviarRecordatoriosAsync(stoppingToken);
                await acciones.ProcesarAlertasAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error al enviar recordatorios de eventos");
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken));
    }
}
