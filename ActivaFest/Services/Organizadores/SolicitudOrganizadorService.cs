using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Email;

namespace ActivaFest.Services.Organizadores;

public class SolicitudOrganizadorInput
{
    public TipoOrganizador Tipo { get; set; }

    [Required(ErrorMessage = "Escribe tu nombre completo o la razón social.")]
    [StringLength(150)]
    [Display(Name = "Nombre completo o razón social")]
    public string NombreOrganizador { get; set; } = "";

    [Required(ErrorMessage = "Escribe tu DNI o RUC.")]
    [Display(Name = "DNI o RUC")]
    public string Documento { get; set; } = "";

    [Required(ErrorMessage = "Escribe un teléfono de contacto.")]
    [Display(Name = "Teléfono (WhatsApp)")]
    public string Telefono { get; set; } = "";

    [Required(ErrorMessage = "Cuéntanos qué eventos organizarás.")]
    [StringLength(1000, MinimumLength = 30, ErrorMessage = "Describe tus eventos en al menos 30 caracteres.")]
    [Display(Name = "¿Qué eventos organizarás?")]
    public string DescripcionEventos { get; set; } = "";

    [StringLength(300)]
    [Display(Name = "Página web o redes sociales (opcional)")]
    public string? WebORedes { get; set; }

    [Display(Name = "¿Por cuánto tiempo?")]
    public PlazoOrganizador PlazoSolicitado { get; set; } = PlazoOrganizador.Dias90;

    public bool AceptaCondiciones { get; set; }
}

public record ResultadoSolicitud(bool Exito, string? Error, SolicitudOrganizador? Solicitud = null)
{
    public static ResultadoSolicitud Fallo(string error) => new(false, error);
}

public interface ISolicitudOrganizadorService
{
    Task<SolicitudOrganizador?> UltimaAsync(string usuarioId, CancellationToken ct = default);
    Task<ResultadoSolicitud> CrearAsync(string usuarioId, SolicitudOrganizadorInput datos, string urlAdmin, CancellationToken ct = default);
    Task<List<SolicitudOrganizador>> ListarAsync(EstadoSolicitud estado, CancellationToken ct = default);
    Task<int> PendientesAsync(CancellationToken ct = default);
    Task<ResultadoSolicitud> AprobarAsync(int id, string administradorId, PlazoOrganizador plazo, string urlCrearEvento, CancellationToken ct = default);
    Task<ResultadoSolicitud> RechazarAsync(int id, string administradorId, string motivo, string urlSolicitud, CancellationToken ct = default);

    // Quita el rol de organizador a quienes se les venció el plazo (lo ejecuta un proceso cada hora)
    Task<int> VencerAsync(CancellationToken ct = default);
}

// =============================
// 📝 SOLICITUDES PARA ORGANIZAR EVENTOS (HU31)
// =============================
public class SolicitudOrganizadorService : ISolicitudOrganizadorService
{
    // Requisitos que se muestran antes de solicitar (y en la documentación)
    public static readonly string[] Requisitos =
    {
        "Tener una cuenta de usuario activa en ActivaFest.",
        "Nombre completo (persona natural) o razón social (empresa o asociación).",
        "DNI de 8 dígitos si eres persona natural, o RUC de 11 dígitos si es una empresa o asociación.",
        "Un teléfono de contacto (de preferencia con WhatsApp) para coordinar con el equipo.",
        "Describir qué eventos organizarás: tipo de evento, lugar aproximado y público.",
        "Página web o redes sociales del organizador (opcional, agiliza la revisión).",
        "Elegir por cuánto tiempo: 30, 90 o 180 días, o indefinido. El administrador puede ajustarlo.",
        "Aceptar las condiciones del organizador."
    };

    public static readonly string[] Condiciones =
    {
        "Publicar información veraz: fecha, lugar, precios y al menos 3 fotos reales del recinto.",
        "Cada evento pasa por la revisión del administrador antes de aparecer en el catálogo.",
        "Atender las consultas y devoluciones de quienes compren entradas para tus eventos.",
        "La plataforma cobra una comisión por cada entrada vendida.",
        "Si publicas eventos falsos o engañosos, se rechazan y se puede desactivar tu cuenta."
    };

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _usuarios;
    private readonly IEmailService _email;
    private readonly ILogger<SolicitudOrganizadorService> _logger;

    public SolicitudOrganizadorService(ApplicationDbContext db, UserManager<ApplicationUser> usuarios, IEmailService email,
        ILogger<SolicitudOrganizadorService> logger)
    {
        _db = db;
        _usuarios = usuarios;
        _email = email;
        _logger = logger;
    }

    public Task<SolicitudOrganizador?> UltimaAsync(string usuarioId, CancellationToken ct = default) =>
        _db.SolicitudesOrganizador.AsNoTracking()
            .Where(s => s.UsuarioId == usuarioId)
            .OrderByDescending(s => s.FechaSolicitud).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

    public static string? Validar(SolicitudOrganizadorInput d)
    {
        var documento = Regex.Replace(d.Documento ?? "", @"\s", "");
        if (d.Tipo == TipoOrganizador.PersonaNatural && !Regex.IsMatch(documento, @"^\d{8}$"))
            return "El DNI debe tener 8 dígitos.";
        if (d.Tipo == TipoOrganizador.Empresa && !Regex.IsMatch(documento, @"^(10|15|17|20)\d{9}$"))
            return "El RUC debe tener 11 dígitos y empezar con 10, 15, 17 o 20.";

        var telefono = Regex.Replace(d.Telefono ?? "", @"[\s\-()]", "");
        if (!Regex.IsMatch(telefono, @"^(\+?51)?9\d{8}$") && !Regex.IsMatch(telefono, @"^\+\d{8,15}$"))
            return "Escribe un celular válido (por ejemplo 987654321 o +51 987 654 321).";

        if (!Enum.IsDefined(d.PlazoSolicitado))
            return "Elige por cuánto tiempo quieres organizar eventos.";
        if (!d.AceptaCondiciones)
            return "Debes aceptar las condiciones del organizador.";
        return null;
    }

    public async Task<ResultadoSolicitud> CrearAsync(string usuarioId, SolicitudOrganizadorInput datos, string urlAdmin, CancellationToken ct = default)
    {
        var usuario = await _usuarios.FindByIdAsync(usuarioId);
        if (usuario == null || !usuario.Activo)
            return ResultadoSolicitud.Fallo("Tu cuenta no está activa.");
        if (await _usuarios.IsInRoleAsync(usuario, Roles.Organizador))
            return ResultadoSolicitud.Fallo("Tu cuenta ya está habilitada para crear eventos.");
        if (await _db.SolicitudesOrganizador.AnyAsync(s => s.UsuarioId == usuarioId && s.Estado == EstadoSolicitud.Pendiente, ct))
            return ResultadoSolicitud.Fallo("Ya tienes una solicitud en revisión. Te avisaremos por correo cuando la revisemos.");

        var error = Validar(datos);
        if (error != null)
            return ResultadoSolicitud.Fallo(error);

        var solicitud = new SolicitudOrganizador
        {
            UsuarioId = usuarioId,
            Tipo = datos.Tipo,
            NombreOrganizador = datos.NombreOrganizador.Trim(),
            Documento = Regex.Replace(datos.Documento, @"\s", ""),
            Telefono = datos.Telefono.Trim(),
            DescripcionEventos = datos.DescripcionEventos.Trim(),
            WebORedes = string.IsNullOrWhiteSpace(datos.WebORedes) ? null : datos.WebORedes.Trim(),
            PlazoSolicitado = datos.PlazoSolicitado,
            FechaSolicitud = DateTime.UtcNow
        };
        _db.SolicitudesOrganizador.Add(solicitud);
        await _db.SaveChangesAsync(ct);

        // Aviso a los administradores para que la revisen
        foreach (var admin in await _usuarios.GetUsersInRoleAsync(Roles.Administrador))
            if (admin.Email != null)
                await _email.EnviarAsync(admin.Email, "Nueva solicitud para organizar eventos",
                    EmailTemplates.SolicitudParaAdmin("organizar eventos", $"{solicitud.NombreOrganizador} ({usuario.Email})", urlAdmin));

        _logger.LogInformation("📝 {Email} solicitó habilitarse como organizador", usuario.Email);
        return new ResultadoSolicitud(true, null, solicitud);
    }

    public Task<List<SolicitudOrganizador>> ListarAsync(EstadoSolicitud estado, CancellationToken ct = default) =>
        _db.SolicitudesOrganizador.AsNoTracking()
            .Include(s => s.Usuario)
            .Where(s => s.Estado == estado)
            .OrderBy(s => estado == EstadoSolicitud.Pendiente ? s.FechaSolicitud : DateTime.MinValue)
            .ThenByDescending(s => s.FechaRespuesta)
            .Take(200)
            .ToListAsync(ct);

    public Task<int> PendientesAsync(CancellationToken ct = default) =>
        _db.SolicitudesOrganizador.CountAsync(s => s.Estado == EstadoSolicitud.Pendiente, ct);

    public async Task<ResultadoSolicitud> AprobarAsync(int id, string administradorId, PlazoOrganizador plazo, string urlCrearEvento, CancellationToken ct = default)
    {
        var solicitud = await _db.SolicitudesOrganizador.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (solicitud == null || solicitud.Estado != EstadoSolicitud.Pendiente)
            return ResultadoSolicitud.Fallo("La solicitud ya fue respondida.");
        if (!Enum.IsDefined(plazo))
            return ResultadoSolicitud.Fallo("Plazo no válido.");

        var usuario = await _usuarios.FindByIdAsync(solicitud.UsuarioId);
        if (usuario == null)
            return ResultadoSolicitud.Fallo("La cuenta ya no existe.");

        var hasta = plazo == PlazoOrganizador.Indefinido ? (DateTime?)null : DateTime.UtcNow.AddDays((int)plazo);
        usuario.OrganizadorHasta = hasta;
        await _usuarios.UpdateAsync(usuario);
        if (!await _usuarios.IsInRoleAsync(usuario, Roles.Organizador))
            await _usuarios.AddToRoleAsync(usuario, Roles.Organizador);   // cambia el sello de seguridad: el rol se aplica en su sesión

        solicitud.Estado = EstadoSolicitud.Aprobada;
        solicitud.PlazoAprobado = plazo;
        solicitud.HabilitadoHasta = hasta;
        solicitud.FechaRespuesta = DateTime.UtcNow;
        solicitud.RespondidaPorId = administradorId;
        await _db.SaveChangesAsync(ct);

        if (usuario.Email != null)
            await _email.EnviarAsync(usuario.Email, "¡Ya puedes publicar tus eventos en ActivaFest!",
                EmailTemplates.OrganizadorAprobado(usuario.NombreCompleto, hasta?.ToLocalTime(), urlCrearEvento));

        _logger.LogInformation("✅ {Email} habilitado como organizador {Plazo}", usuario.Email, hasta == null ? "indefinidamente" : $"hasta {hasta:yyyy-MM-dd}");
        return new ResultadoSolicitud(true, null, solicitud);
    }

    public async Task<ResultadoSolicitud> RechazarAsync(int id, string administradorId, string motivo, string urlSolicitud, CancellationToken ct = default)
    {
        motivo = (motivo ?? "").Trim();
        if (motivo.Length < 5)
            return ResultadoSolicitud.Fallo("Escribe el motivo del rechazo para que la persona sepa qué corregir.");

        var solicitud = await _db.SolicitudesOrganizador.Include(s => s.Usuario).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (solicitud == null || solicitud.Estado != EstadoSolicitud.Pendiente)
            return ResultadoSolicitud.Fallo("La solicitud ya fue respondida.");

        solicitud.Estado = EstadoSolicitud.Rechazada;
        solicitud.MotivoRechazo = motivo.Length > 500 ? motivo[..500] : motivo;
        solicitud.FechaRespuesta = DateTime.UtcNow;
        solicitud.RespondidaPorId = administradorId;
        await _db.SaveChangesAsync(ct);

        if (solicitud.Usuario?.Email != null)
            await _email.EnviarAsync(solicitud.Usuario.Email, "Tu solicitud para organizar eventos",
                EmailTemplates.SolicitudRechazada("organizar eventos", solicitud.Usuario.NombreCompleto, solicitud.MotivoRechazo, urlSolicitud));
        return new ResultadoSolicitud(true, null, solicitud);
    }

    public async Task<int> VencerAsync(CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow;
        var vencidos = await _db.Users.Where(u => u.OrganizadorHasta != null && u.OrganizadorHasta < ahora).ToListAsync(ct);
        var quitados = 0;
        foreach (var usuario in vencidos)
        {
            if (await _usuarios.IsInRoleAsync(usuario, Roles.Organizador))
            {
                await _usuarios.RemoveFromRoleAsync(usuario, Roles.Organizador);   // sus eventos publicados siguen vigentes
                quitados++;
                if (usuario.Email != null)
                    await _email.EnviarAsync(usuario.Email, "Tu periodo como organizador terminó",
                        EmailTemplates.OrganizadorVencido(usuario.NombreCompleto));
            }
            usuario.OrganizadorHasta = null;
            await _usuarios.UpdateAsync(usuario);
        }
        if (quitados > 0)
            _logger.LogInformation("⏰ {Cantidad} cuentas de organizador vencieron", quitados);
        return quitados;
    }
}

// Revisa cada hora los permisos de organizador vencidos
public class VencimientoOrganizadoresService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<VencimientoOrganizadoresService> _logger;

    public VencimientoOrganizadoresService(IServiceScopeFactory scopes, ILogger<VencimientoOrganizadoresService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var reloj = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ISolicitudOrganizadorService>().VencerAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "No se pudieron revisar los organizadores vencidos");
            }
        } while (await reloj.WaitForNextTickAsync(stoppingToken));
    }
}
