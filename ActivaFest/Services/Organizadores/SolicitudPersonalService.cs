using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Acceso;
using ActivaFest.Services.Email;

namespace ActivaFest.Services.Organizadores;

// Cuenta pedida como personal de control, tal como la ve el administrador (con su reputación, HU33)
public record CuentaPersonal(string Correo, string? Nombre, bool Existe, bool Activa, bool EsControl, string? UsuarioId,
    Reputacion? Reputacion = null)
{
    public bool MalaFama => Reputacion?.MalaFama == true;

    // Se puede habilitar automáticamente: existe, está activa y no tiene mala reputación
    public bool Apta => Existe && Activa && !MalaFama;
}

public interface ISolicitudPersonalService
{
    // Busca una cuenta por su correo exacto (solo para el administrador)
    Task<CuentaPersonal> BuscarCuentaAsync(string correo, CancellationToken ct = default);

    // Datos de las cuentas de una o varias solicitudes, por correo (solo para el administrador)
    Task<Dictionary<string, CuentaPersonal>> CuentasAsync(IEnumerable<string> correos, CancellationToken ct = default);

    // Para el organizador: qué correos de sus solicitudes quedaron habilitados (true) o ya terminaron su turno (false)
    Task<Dictionary<(int SolicitudId, string Correo), bool>> HabilitadosAsync(IEnumerable<SolicitudPersonal> solicitudes, CancellationToken ct = default);

    // Eventos aprobados y próximos del organizador (el administrador ve todos)
    Task<List<Evento>> EventosAsync(string organizadorId, bool esAdmin, CancellationToken ct = default);
    Task<List<SolicitudPersonal>> MiasAsync(string organizadorId, CancellationToken ct = default);
    Task<ResultadoSolicitud> CrearAsync(string organizadorId, bool esAdmin, int eventoId, string correos, string? mensaje, string urlAdmin, CancellationToken ct = default);
    Task<List<SolicitudPersonal>> ListarAsync(EstadoSolicitud estado, CancellationToken ct = default);
    Task<int> PendientesAsync(CancellationToken ct = default);

    // elegidos: correos que marcó el administrador (null = todas las cuentas registradas y activas)
    Task<ResultadoSolicitud> AprobarAsync(int id, string administradorId, string urlPuerta, IReadOnlyCollection<string>? elegidos = null, CancellationToken ct = default);
    Task<ResultadoSolicitud> RechazarAsync(int id, string administradorId, string motivo, string urlSolicitud, CancellationToken ct = default);

    // HU33: resuelve sola cada solicitud que siga pendiente 1 hora antes del evento
    Task<int> ResolverAutomaticasAsync(DateTime ahora, string urlPuerta, string urlSolicitud, CancellationToken ct = default);

    // HU33: desactiva las asignaciones de los eventos que ya terminaron y quita el rol a quien se queda sin turnos
    Task<int> FinalizarAsignacionesAsync(DateTime ahora, CancellationToken ct = default);
}

// =============================
// 🚪 SOLICITUDES DE PERSONAL DE CONTROL (HU32, HU33)
// =============================
public class SolicitudPersonalService : ISolicitudPersonalService
{
    public const int MaxCuentas = 10;
    public const int HorasAntesAprobacionAutomatica = 1;

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _usuarios;
    private readonly IEmailService _email;
    private readonly IReputacionPersonalService _reputacion;
    private readonly ILogger<SolicitudPersonalService> _logger;

    public SolicitudPersonalService(ApplicationDbContext db, UserManager<ApplicationUser> usuarios, IEmailService email,
        IReputacionPersonalService reputacion, ILogger<SolicitudPersonalService> logger)
    {
        _db = db;
        _usuarios = usuarios;
        _email = email;
        _reputacion = reputacion;
        _logger = logger;
    }

    // Momento en que el sistema resuelve la solicitud si el administrador no lo hizo antes
    public static DateTime LimiteRespuesta(Evento evento) => evento.Fecha.AddHours(-HorasAntesAprobacionAutomatica);

    private IQueryable<Evento> Vigentes(string organizadorId, bool esAdmin)
    {
        var desde = DateTime.Now.AddHours(-Evento.HorasValidezEntradas);
        // HU34: los eventos gratuitos no tienen entradas que controlar
        var q = _db.Eventos.AsNoTracking().Where(e => e.Estado == EstadoEvento.Aprobado && !e.EsGratuito && e.Fecha >= desde);
        return esAdmin ? q : q.Where(e => e.OrganizadorId == organizadorId);
    }

    public Task<List<Evento>> EventosAsync(string organizadorId, bool esAdmin, CancellationToken ct = default) =>
        Vigentes(organizadorId, esAdmin).OrderBy(e => e.Fecha).Take(500).ToListAsync(ct);

    public Task<List<SolicitudPersonal>> MiasAsync(string organizadorId, CancellationToken ct = default) =>
        _db.SolicitudesPersonal.AsNoTracking().Include(s => s.Evento)
            .Where(s => s.OrganizadorId == organizadorId)
            .OrderByDescending(s => s.FechaSolicitud).Take(50)
            .ToListAsync(ct);

    public async Task<CuentaPersonal> BuscarCuentaAsync(string correo, CancellationToken ct = default) =>
        (await CuentasAsync(new[] { correo }, ct)).Values.First();

    public async Task<Dictionary<string, CuentaPersonal>> CuentasAsync(IEnumerable<string> correos, CancellationToken ct = default)
    {
        var resultado = new Dictionary<string, CuentaPersonal>(StringComparer.OrdinalIgnoreCase);
        var encontrados = new List<(string Correo, ApplicationUser Usuario)>();
        foreach (var correo in correos.Select(c => (c ?? "").Trim().ToLowerInvariant()).Distinct())
        {
            var usuario = correo.Length == 0 ? null : await _usuarios.FindByEmailAsync(correo);
            if (usuario == null)
                resultado[correo] = new CuentaPersonal(correo, null, false, false, false, null);
            else
                encontrados.Add((correo, usuario));
        }

        var reputaciones = await _reputacion.ReputacionesAsync(encontrados.Select(e => e.Usuario.Id), ct);
        foreach (var (correo, usuario) in encontrados)
            resultado[correo] = new CuentaPersonal(correo, usuario.NombreCompleto, true, usuario.Activo,
                await _usuarios.IsInRoleAsync(usuario, Roles.ControlAcceso), usuario.Id,
                reputaciones.GetValueOrDefault(usuario.Id, Reputacion.Vacia));
        return resultado;
    }

    public async Task<Dictionary<(int SolicitudId, string Correo), bool>> HabilitadosAsync(IEnumerable<SolicitudPersonal> solicitudes, CancellationToken ct = default)
    {
        var habilitados = new Dictionary<(int, string), bool>();
        foreach (var s in solicitudes.Where(s => s.Estado == EstadoSolicitud.Aprobada))
            foreach (var correo in s.ListaCorreos)
            {
                var cuenta = await _usuarios.FindByEmailAsync(correo);
                if (cuenta == null)
                    continue;
                var asignacion = await _db.AsignacionesAcceso.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.UsuarioId == cuenta.Id && a.EventoId == s.EventoId, ct);
                if (asignacion != null)
                    habilitados[(s.Id, correo)] = asignacion.Activa;
            }
        return habilitados;
    }

    // "ana@x.com; beto@x.com, carla@x.com" → lista sin repetidos, en minúsculas
    public static List<string> SepararCorreos(string? texto) =>
        Regex.Split(texto ?? "", @"[\s,;]+").Where(c => c.Length > 0).Select(c => c.Trim().ToLowerInvariant()).Distinct().ToList();

    public async Task<ResultadoSolicitud> CrearAsync(string organizadorId, bool esAdmin, int eventoId, string correos, string? mensaje,
        string urlAdmin, CancellationToken ct = default)
    {
        var evento = await Vigentes(organizadorId, esAdmin).FirstOrDefaultAsync(e => e.Id == eventoId, ct);
        if (evento == null)
            return ResultadoSolicitud.Fallo("Elige uno de tus eventos aprobados y próximos.");

        var lista = SepararCorreos(correos);
        if (lista.Count == 0)
            return ResultadoSolicitud.Fallo("Escribe al menos el correo de una cuenta.");
        if (lista.Count > MaxCuentas)
            return ResultadoSolicitud.Fallo($"Puedes pedir hasta {MaxCuentas} cuentas por solicitud.");
        var invalidos = lista.Where(c => !Regex.IsMatch(c, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")).ToList();
        if (invalidos.Count > 0)
            return ResultadoSolicitud.Fallo($"Correo no válido: {string.Join(", ", invalidos)}.");

        // No se le dice al organizador si un correo está registrado (no puede averiguar qué cuentas existen):
        // el administrador revisa cada correo y solo habilita los que corresponden a cuentas activas.

        var solicitud = new SolicitudPersonal
        {
            OrganizadorId = organizadorId,
            EventoId = eventoId,
            Correos = string.Join(", ", lista),
            Mensaje = string.IsNullOrWhiteSpace(mensaje) ? null : mensaje.Trim()[..Math.Min(500, mensaje.Trim().Length)],
            FechaSolicitud = DateTime.UtcNow
        };
        _db.SolicitudesPersonal.Add(solicitud);
        await _db.SaveChangesAsync(ct);

        var organizador = await _usuarios.FindByIdAsync(organizadorId);
        foreach (var admin in await _usuarios.GetUsersInRoleAsync(Roles.Administrador))
            if (admin.Email != null)
                await _email.EnviarAsync(admin.Email, "Nueva solicitud de personal de control",
                    EmailTemplates.SolicitudParaAdmin($"personal de control en «{evento.Titulo}»", organizador?.Email ?? "Un organizador", urlAdmin));

        return new ResultadoSolicitud(true, null);
    }

    public Task<List<SolicitudPersonal>> ListarAsync(EstadoSolicitud estado, CancellationToken ct = default) =>
        _db.SolicitudesPersonal.AsNoTracking()
            .Include(s => s.Evento).Include(s => s.Organizador)
            .Where(s => s.Estado == estado)
            .OrderBy(s => s.FechaSolicitud)
            .Take(200)
            .ToListAsync(ct);

    public Task<int> PendientesAsync(CancellationToken ct = default) =>
        _db.SolicitudesPersonal.CountAsync(s => s.Estado == EstadoSolicitud.Pendiente, ct);

    private Task<SolicitudPersonal?> PendienteAsync(int id, CancellationToken ct) =>
        _db.SolicitudesPersonal.Include(s => s.Evento).Include(s => s.Organizador)
            .FirstOrDefaultAsync(s => s.Id == id && s.Estado == EstadoSolicitud.Pendiente, ct);

    public async Task<ResultadoSolicitud> AprobarAsync(int id, string administradorId, string urlPuerta, IReadOnlyCollection<string>? elegidos = null,
        CancellationToken ct = default)
    {
        var solicitud = await PendienteAsync(id, ct);
        if (solicitud == null)
            return ResultadoSolicitud.Fallo("La solicitud ya fue respondida.");

        var cuentas = await CuentasAsync(solicitud.ListaCorreos, ct);
        // El administrador decide viendo la reputación: puede habilitar incluso una cuenta con reportes
        var aHabilitar = cuentas.Values
            .Where(c => c.Existe && c.Activa && (elegidos == null || elegidos.Contains(c.Correo, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (aHabilitar.Count == 0)
            return ResultadoSolicitud.Fallo("Marca al menos una cuenta registrada y activa, o rechaza la solicitud.");

        await HabilitarAsync(solicitud, aHabilitar, cuentas.Values.Except(aHabilitar), administradorId, false, urlPuerta, ct);
        return new ResultadoSolicitud(true, null);
    }

    private async Task HabilitarAsync(SolicitudPersonal solicitud, List<CuentaPersonal> aHabilitar, IEnumerable<CuentaPersonal> excluidas,
        string? administradorId, bool automatica, string urlPuerta, CancellationToken ct)
    {
        // Cada cuenta recibe el rol de Control de acceso y queda asignada solo a ese evento
        foreach (var c in aHabilitar)
        {
            var cuenta = (await _usuarios.FindByIdAsync(c.UsuarioId!))!;
            if (!await _usuarios.IsInRoleAsync(cuenta, Roles.ControlAcceso))
                await _usuarios.AddToRoleAsync(cuenta, Roles.ControlAcceso);

            var asignacion = await _db.AsignacionesAcceso.FirstOrDefaultAsync(a => a.UsuarioId == cuenta.Id && a.EventoId == solicitud.EventoId, ct);
            if (asignacion == null)
                _db.AsignacionesAcceso.Add(new AsignacionAcceso { UsuarioId = cuenta.Id, EventoId = solicitud.EventoId });
            else
                asignacion.FinalizadaEn = null;

            await _email.EnviarAsync(c.Correo, $"Validarás las entradas de {solicitud.Evento!.Titulo}",
                EmailTemplates.PersonalAsignado(cuenta.NombreCompleto, solicitud.Evento, urlPuerta));
        }

        solicitud.Estado = EstadoSolicitud.Aprobada;
        solicitud.FechaRespuesta = DateTime.UtcNow;
        solicitud.RespondidaPorId = administradorId;
        solicitud.RespuestaAutomatica = automatica;
        solicitud.Observacion = Observacion(excluidas);
        await _db.SaveChangesAsync(ct);

        if (solicitud.Organizador?.Email != null)
            await _email.EnviarAsync(solicitud.Organizador.Email, "Tu personal de control fue aprobado",
                EmailTemplates.PersonalAprobado(solicitud.Organizador.NombreCompleto, solicitud.Evento!.Titulo, aHabilitar.Select(c => c.Correo)));
    }

    // Solo para el administrador: por qué no se habilitó cada cuenta
    private static string? Observacion(IEnumerable<CuentaPersonal> excluidas)
    {
        var texto = string.Join("; ", excluidas.Select(c => $"{c.Correo}: " + (
            !c.Existe ? "no tiene cuenta" :
            !c.Activa ? "cuenta desactivada" :
            c.MalaFama ? $"mala reputación ({c.Reputacion!.Resumen})" :
            "no elegida")));
        return texto.Length == 0 ? null : texto.Length > 1000 ? texto[..1000] : texto;
    }

    public async Task<ResultadoSolicitud> RechazarAsync(int id, string administradorId, string motivo, string urlSolicitud, CancellationToken ct = default)
    {
        motivo = (motivo ?? "").Trim();
        if (motivo.Length < 5)
            return ResultadoSolicitud.Fallo("Escribe el motivo del rechazo.");

        var solicitud = await PendienteAsync(id, ct);
        if (solicitud == null)
            return ResultadoSolicitud.Fallo("La solicitud ya fue respondida.");

        await RechazarAsync(solicitud, administradorId, false, motivo, null, urlSolicitud, ct);
        return new ResultadoSolicitud(true, null);
    }

    private async Task RechazarAsync(SolicitudPersonal solicitud, string? administradorId, bool automatica, string motivo, string? observacion,
        string urlSolicitud, CancellationToken ct)
    {
        solicitud.Estado = EstadoSolicitud.Rechazada;
        solicitud.MotivoRechazo = motivo.Length > 500 ? motivo[..500] : motivo;
        solicitud.FechaRespuesta = DateTime.UtcNow;
        solicitud.RespondidaPorId = administradorId;
        solicitud.RespuestaAutomatica = automatica;
        solicitud.Observacion = observacion;
        await _db.SaveChangesAsync(ct);

        if (solicitud.Organizador?.Email != null)
            await _email.EnviarAsync(solicitud.Organizador.Email, "Tu solicitud de personal de control",
                EmailTemplates.SolicitudRechazada("personal de control", solicitud.Organizador.NombreCompleto, solicitud.MotivoRechazo, urlSolicitud));
    }

    public async Task<int> ResolverAutomaticasAsync(DateTime ahora, string urlPuerta, string urlSolicitud, CancellationToken ct = default)
    {
        var limite = ahora.AddHours(HorasAntesAprobacionAutomatica);
        var ids = await _db.SolicitudesPersonal
            .Where(s => s.Estado == EstadoSolicitud.Pendiente && s.Evento!.Fecha <= limite)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            var solicitud = await PendienteAsync(id, ct);
            if (solicitud == null)
                continue;   // el administrador la respondió justo ahora

            if (ahora > solicitud.Evento!.FinValidezEntradas || solicitud.Evento.Estado != EstadoEvento.Aprobado)
            {
                await RechazarAsync(solicitud, null, true, "El evento ya terminó o no está publicado.", null, urlSolicitud, ct);
                continue;
            }

            // Solo cuentas registradas, activas y sin mala reputación: una cuenta con mala fama se rechaza sola
            var cuentas = await CuentasAsync(solicitud.ListaCorreos, ct);
            var aptas = cuentas.Values.Where(c => c.Apta).ToList();
            if (aptas.Count > 0)
                await HabilitarAsync(solicitud, aptas, cuentas.Values.Except(aptas), null, true, urlPuerta, ct);
            else
                await RechazarAsync(solicitud, null, true,
                    "Ninguna de las cuentas cumple los requisitos del personal de control (cuenta registrada y activa, con buena reputación). Consulta con el administrador.",
                    Observacion(cuentas.Values), urlSolicitud, ct);
        }

        if (ids.Count > 0)
            _logger.LogInformation("🤖 {Cantidad} solicitudes de personal resueltas automáticamente", ids.Count);
        return ids.Count;
    }

    public async Task<int> FinalizarAsignacionesAsync(DateTime ahora, CancellationToken ct = default)
    {
        // El evento terminó cuando venció el plazo de ingreso de sus entradas
        var desde = ahora.AddHours(-Evento.HorasValidezEntradas);
        var terminadas = await _db.AsignacionesAcceso
            .Where(a => a.FinalizadaEn == null && a.Evento!.Fecha < desde)
            .ToListAsync(ct);
        if (terminadas.Count == 0)
            return 0;

        foreach (var a in terminadas)
            a.FinalizadaEn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Quien ya no tiene turnos pendientes deja de ser personal de control (los organizadores y el administrador no se tocan)
        foreach (var usuarioId in terminadas.Select(a => a.UsuarioId).Distinct())
        {
            if (await _db.AsignacionesAcceso.AnyAsync(a => a.UsuarioId == usuarioId && a.FinalizadaEn == null, ct))
                continue;
            var usuario = await _usuarios.FindByIdAsync(usuarioId);
            if (usuario == null || !await _usuarios.IsInRoleAsync(usuario, Roles.ControlAcceso) ||
                await _usuarios.IsInRoleAsync(usuario, Roles.Administrador) || await _usuarios.IsInRoleAsync(usuario, Roles.Organizador))
                continue;
            await _usuarios.RemoveFromRoleAsync(usuario, Roles.ControlAcceso);   // renueva su SecurityStamp: la sesión pierde el permiso
        }

        _logger.LogInformation("🏁 {Cantidad} asignaciones de control de acceso finalizadas", terminadas.Count);
        return terminadas.Count;
    }
}

// Cada 5 minutos: aprobación automática 1 hora antes del evento y fin del control de acceso al terminar (HU33)
public class AutomatizacionPersonalService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<AutomatizacionPersonalService> _logger;

    public AutomatizacionPersonalService(IServiceScopeFactory scopes, IConfiguration config, ILogger<AutomatizacionPersonalService> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Los enlaces de los correos salen de la URL pública configurada (no hay petición HTTP en segundo plano)
        var baseUrl = (_config["App:UrlPublica"] ?? "http://localhost:5054").TrimEnd('/');
        using var reloj = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var personal = scope.ServiceProvider.GetRequiredService<ISolicitudPersonalService>();
                await personal.ResolverAutomaticasAsync(DateTime.Now, $"{baseUrl}/Acceso", $"{baseUrl}/Personal", stoppingToken);
                await personal.FinalizarAsignacionesAsync(DateTime.Now, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "No se pudo revisar el personal de control");
            }
        } while (await reloj.WaitForNextTickAsync(stoppingToken));
    }
}
