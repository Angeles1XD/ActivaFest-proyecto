using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Entradas;

namespace ActivaFest.Services.Acceso;

public enum TipoValidacion
{
    Valida,
    YaUsada,
    Invalida,
    OtroEvento,
    Anulada,
    Temprano,
    Vencida
}

public record ResultadoValidacion(
    TipoValidacion Tipo,
    string Mensaje,
    string? Zona = null,
    string? Titular = null,
    DateTime? FechaUso = null,
    int Ingresados = 0,
    int Vendidas = 0,
    string? Asiento = null,  // HU27: "F-12" en recintos con asientos numerados
    string? Puerta = null)
{
    public bool PermiteIngreso => Tipo == TipoValidacion.Valida;

    // Títulos que pide HU08: una segunda lectura es «ENTRADA YA UTILIZADA» y cualquier otro rechazo, «INGRESO RECHAZADO»
    public string Titulo => Tipo switch
    {
        TipoValidacion.Valida => TituloPermitido,
        TipoValidacion.YaUsada => TituloYaUtilizada,
        _ => TituloRechazado
    };

    public const string TituloPermitido = "INGRESO PERMITIDO";
    public const string TituloYaUtilizada = "ENTRADA YA UTILIZADA";
    public const string TituloRechazado = "INGRESO RECHAZADO";
}

public interface IAccesoService
{
    // Eventos que este usuario puede controlar en puerta
    Task<List<Evento>> EventosControlablesAsync(string usuarioId, bool esAdministrador, CancellationToken ct = default);

    Task<bool> PuedeControlarAsync(string usuarioId, bool esAdministrador, int eventoId, CancellationToken ct = default);

    Task<ResultadoValidacion> ValidarAsync(int eventoId, string contenidoQr, string validadorId, DateTime ahora, CancellationToken ct = default);

    Task<(int Ingresados, int Vendidas)> AsistenciaAsync(int eventoId, CancellationToken ct = default);
}

// =============================
// 🚪 VALIDACIÓN DE ACCESO EN PUERTA (HU08)
// =============================
// Un QR solo deja pasar una vez: el paso de Activa a Usada es un UPDATE condicional atómico,
// por eso una copia (captura de pantalla, clon) escaneada después siempre se rechaza.
public class AccesoService : IAccesoService
{
    private readonly ApplicationDbContext _db;
    private readonly IQrEntradaService _qr;
    private readonly ILogger<AccesoService> _logger;

    public AccesoService(ApplicationDbContext db, IQrEntradaService qr, ILogger<AccesoService> logger)
    {
        _db = db;
        _qr = qr;
        _logger = logger;
    }

    // HU19: el administrador controla cualquier evento aprobado; los demás solo sus propios eventos
    // (organizador) o aquellos a los que el administrador los asignó (personal de Control de Acceso)
    private IQueryable<Evento> EventosDe(string usuarioId, bool esAdministrador)
    {
        var query = _db.Eventos.AsNoTracking().Where(e => e.Estado == EstadoEvento.Aprobado && !e.EsGratuito);   // HU34: sin entradas que validar
        return esAdministrador
            ? query
            : query.Where(e => e.OrganizadorId == usuarioId ||
                               _db.AsignacionesAcceso.Any(a => a.EventoId == e.Id && a.UsuarioId == usuarioId && a.FinalizadaEn == null));
    }

    public Task<List<Evento>> EventosControlablesAsync(string usuarioId, bool esAdministrador, CancellationToken ct = default)
    {
        var desde = DateTime.Now.AddHours(-Evento.HorasValidezEntradas);
        return EventosDe(usuarioId, esAdministrador)
            .Where(e => e.Fecha >= desde)
            .OrderBy(e => e.Fecha)
            .ToListAsync(ct);
    }

    public Task<bool> PuedeControlarAsync(string usuarioId, bool esAdministrador, int eventoId, CancellationToken ct = default) =>
        EventosDe(usuarioId, esAdministrador).AnyAsync(e => e.Id == eventoId, ct);

    public async Task<ResultadoValidacion> ValidarAsync(int eventoId, string contenidoQr, string validadorId, DateTime ahora,
        CancellationToken ct = default)
    {
        // 1. La firma HMAC descarta QR falsificados o alterados sin tocar la base de datos
        var codigo = _qr.LeerContenido(contenidoQr ?? "");
        if (codigo == null)
            return await ConAsistenciaAsync(eventoId, new ResultadoValidacion(TipoValidacion.Invalida, "QR no válido: no pertenece a ActivaFest o fue alterado."), ct);

        var entrada = await _db.Entradas
            .AsNoTracking()
            .Include(e => e.Evento)
            .Include(e => e.TipoEntrada)
            .Include(e => e.Asiento) // HU27: el escáner muestra zona y asiento
            .FirstOrDefaultAsync(e => e.Codigo == codigo, ct);

        if (entrada == null)
            return await ConAsistenciaAsync(eventoId, new ResultadoValidacion(TipoValidacion.Invalida, "Entrada inexistente."), ct);

        var titular = await _db.Users.Where(u => u.Id == entrada.UsuarioId).Select(u => u.NombreCompleto).FirstOrDefaultAsync(ct);
        var zona = entrada.TipoEntrada?.Nombre;
        var ubicacion = UbicacionEntrada.De(entrada);

        ResultadoValidacion Resultado(TipoValidacion tipo, string mensaje, DateTime? uso = null) =>
            new(tipo, mensaje, zona, titular, uso, Asiento: ubicacion.Asiento, Puerta: ubicacion.Puerta);

        // 2. Reglas de la entrada
        if (entrada.EventoId != eventoId)
            return await ConAsistenciaAsync(eventoId, Resultado(TipoValidacion.OtroEvento, $"Esta entrada es de otro evento: {entrada.Evento?.Titulo}."), ct);

        if (entrada.Estado == EstadoEntrada.Anulada)
            return await ConAsistenciaAsync(eventoId, Resultado(TipoValidacion.Anulada, "Entrada anulada."), ct);

        if (entrada.Estado == EstadoEntrada.Usada)
            return await ConAsistenciaAsync(eventoId, Resultado(TipoValidacion.YaUsada, "Esta entrada ya se usó para ingresar.", entrada.FechaUso), ct);

        var evento = entrada.Evento!;
        if (ahora < evento.InicioIngreso)
            return await ConAsistenciaAsync(eventoId, Resultado(TipoValidacion.Temprano,
                $"Aún no empieza el ingreso. Las puertas abren el {evento.InicioIngreso:dd/MM HH:mm}."), ct);

        if (!entrada.VigenteParaIngreso(evento, ahora))
            return await ConAsistenciaAsync(eventoId, Resultado(TipoValidacion.Vencida, "El evento ya terminó: entrada vencida."), ct);

        // 3. Marcado atómico: solo uno de dos escaneos simultáneos del mismo QR puede pasar
        DateTime? momento = ahora;
        var marcadas = await _db.Entradas
            .Where(e => e.Id == entrada.Id && e.Estado == EstadoEntrada.Activa)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Estado, EstadoEntrada.Usada)
                .SetProperty(e => e.FechaUso, momento)
                .SetProperty(e => e.ValidadoPorId, validadorId), ct);

        if (marcadas == 0)
        {
            var uso = await _db.Entradas.Where(e => e.Id == entrada.Id).Select(e => e.FechaUso).FirstAsync(ct);
            return await ConAsistenciaAsync(eventoId, Resultado(TipoValidacion.YaUsada, "Esta entrada ya se usó para ingresar.", uso), ct);
        }

        _logger.LogInformation("🚪 Ingreso validado: entrada {Entrada} del evento {Evento} por {Validador}", entrada.Id, eventoId, validadorId);
        return await ConAsistenciaAsync(eventoId, Resultado(TipoValidacion.Valida, "¡Bienvenido! Entrada válida.", momento), ct);
    }

    public async Task<(int Ingresados, int Vendidas)> AsistenciaAsync(int eventoId, CancellationToken ct = default)
    {
        var conteo = await _db.Entradas
            .Where(e => e.EventoId == eventoId && e.Estado != EstadoEntrada.Anulada)
            .GroupBy(_ => 1)
            .Select(g => new { Ingresados = g.Count(e => e.Estado == EstadoEntrada.Usada), Vendidas = g.Count() })
            .FirstOrDefaultAsync(ct);

        return (conteo?.Ingresados ?? 0, conteo?.Vendidas ?? 0);
    }

    private async Task<ResultadoValidacion> ConAsistenciaAsync(int eventoId, ResultadoValidacion resultado, CancellationToken ct)
    {
        var (ingresados, vendidas) = await AsistenciaAsync(eventoId, ct);
        return resultado with { Ingresados = ingresados, Vendidas = vendidas };
    }
}
