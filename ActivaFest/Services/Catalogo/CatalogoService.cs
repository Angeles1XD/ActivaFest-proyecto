using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using ActivaFest.Data;
using ActivaFest.Models;

namespace ActivaFest.Services.Catalogo;

public interface ICatalogoService
{
    Task<ResultadoCatalogo> BuscarAsync(CatalogoFiltro filtro, CancellationToken ct = default);

    Task<List<Categoria>> ObtenerCategoriasAsync(CancellationToken ct = default);

    // Eventos próximos publicados por categoría (CategoriaId -> cantidad)
    Task<Dictionary<int, int>> ContarPorCategoriaAsync(CancellationToken ct = default);

    // Eventos con un paquete publicitario vigente, para la portada (HU12)
    Task<List<EventoCardViewModel>> DestacadosAsync(int maximo = 8, CancellationToken ct = default);

    // Se llama cuando cambia la oferta publicada (aprobación, rechazo, edición)
    Task InvalidarAsync(CancellationToken ct = default);
}

// =============================
// 📚 CATÁLOGO DE EVENTOS (HU02 + HU05)
// =============================
// Consulta paginada y filtrada con EF Core. Los resultados se guardan en caché (Redis o memoria)
// bajo una "versión" del catálogo: al invalidar se cambia la versión y todas las páginas caducan a la vez.
public class CatalogoService : ICatalogoService
{
    private const string ClaveVersion = "catalogo:version";
    private const string ClaveCategorias = "catalogo:categorias";

    private static readonly DistributedCacheEntryOptions OpcionesCache = new DistributedCacheEntryOptions()
        .SetAbsoluteExpiration(TimeSpan.FromMinutes(5));

    private readonly ApplicationDbContext _context;
    private readonly IDistributedCache _cache;
    private readonly ILogger<CatalogoService> _logger;

    public CatalogoService(ApplicationDbContext context, IDistributedCache cache, ILogger<CatalogoService> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ResultadoCatalogo> BuscarAsync(CatalogoFiltro filtro, CancellationToken ct = default)
    {
        filtro.Normalizar();

        var version = await _cache.GetStringAsync(ClaveVersion, ct) ?? "0";
        var clave = $"catalogo:{version}:{filtro.ClaveCache()}";

        var enCache = await _cache.GetStringAsync(clave, ct);
        if (enCache != null)
        {
            _logger.LogDebug("🔥 CACHE HIT {Clave}", clave);
            return JsonSerializer.Deserialize<ResultadoCatalogo>(enCache)!;
        }

        _logger.LogDebug("💾 CACHE MISS {Clave}", clave);
        var resultado = await ConsultarAsync(filtro, ct);

        await _cache.SetStringAsync(clave, JsonSerializer.Serialize(resultado), await OpcionesHastaProximoCambioAsync(TimeSpan.FromMinutes(5), ct), ct);
        return resultado;
    }

    // =============================
    // ⭐ DESTACADOS DE LA PORTADA (HU12)
    // =============================
    public async Task<List<EventoCardViewModel>> DestacadosAsync(int maximo = 8, CancellationToken ct = default)
    {
        var version = await _cache.GetStringAsync(ClaveVersion, ct) ?? "0";
        var clave = $"catalogo:{version}:destacados:{maximo}";

        var enCache = await _cache.GetStringAsync(clave, ct);
        if (enCache != null)
            return JsonSerializer.Deserialize<List<EventoCardViewModel>>(enCache)!;

        var ahora = DateTime.Now;
        var vigentes = _context.ContratosDestacado
            .Where(c => c.Estado == EstadoContrato.Activo && c.FechaInicio <= ahora && c.FechaFin > ahora);

        // Mayor prioridad de paquete primero; a igual prioridad, el que contrató antes
        var orden = await vigentes
            .Where(c => c.Evento!.Estado == EstadoEvento.Aprobado && c.Evento.Fecha >= ahora)
            .GroupBy(c => c.EventoId)
            .Select(g => new { EventoId = g.Key, Prioridad = g.Max(c => c.Prioridad), Desde = g.Min(c => c.FechaInicio) })
            .OrderByDescending(x => x.Prioridad).ThenBy(x => x.Desde)
            .Take(maximo)
            .ToListAsync(ct);

        var ids = orden.Select(o => o.EventoId).ToList();
        var tarjetas = await ProyectarTarjetas(_context.Eventos.AsNoTracking().Where(e => ids.Contains(e.Id))).ToListAsync(ct);
        var resultado = ids.Select(id => tarjetas.First(t => t.Id == id)).ToList();

        // HU12: la caché vence justo cuando empieza o termina un destacado: al vencer el periodo deja de aparecer al instante
        await _cache.SetStringAsync(clave, JsonSerializer.Serialize(resultado),
            await OpcionesHastaProximoCambioAsync(TimeSpan.FromMinutes(2), ct), ct);
        return resultado;
    }

    // La caché dura como máximo "maximo", pero vence antes si en ese lapso empieza o termina un destacado (HU12)
    // o empieza un evento (sale del catálogo, HU02): así lo publicado nunca queda desactualizado
    private async Task<DistributedCacheEntryOptions> OpcionesHastaProximoCambioAsync(TimeSpan maximo, CancellationToken ct)
    {
        var ahora = DateTime.Now;
        var limite = ahora + maximo;

        var cambioDestacado = await _context.ContratosDestacado
            .Where(c => c.Estado == EstadoContrato.Activo && c.FechaFin > ahora && (c.FechaInicio > ahora ? c.FechaInicio : c.FechaFin) < limite)
            .Select(c => (DateTime?)(c.FechaInicio > ahora ? c.FechaInicio : c.FechaFin))
            .MinAsync(ct);

        var inicioEvento = await _context.Eventos
            .Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha > ahora && e.Fecha < limite)
            .Select(e => (DateTime?)e.Fecha)
            .MinAsync(ct);

        var proximo = new[] { cambioDestacado, inicioEvento, limite }.Where(d => d.HasValue).Min()!.Value;
        var duracion = proximo - ahora;
        return new DistributedCacheEntryOptions().SetAbsoluteExpiration(duracion < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : duracion);
    }

    // Proyección común de las tarjetas del catálogo, la portada y los destacados
    private IQueryable<EventoCardViewModel> ProyectarTarjetas(IQueryable<Evento> query)
    {
        var ahora = DateTime.Now;
        return query.Select(e => new EventoCardViewModel
        {
            Id = e.Id,
            Titulo = e.Titulo,
            Categoria = e.Categoria!.Nombre,
            CategoriaIcono = e.Categoria.Icono,
            CategoriaColor = e.Categoria.Color,
            Ubicacion = e.Ubicacion,
            Fecha = e.Fecha,
            PrecioDesde = e.TiposEntrada.Min(t => (decimal?)t.Precio),
            ImagenPortada = e.Imagenes.OrderBy(i => i.Orden).Select(i => i.Ruta).FirstOrDefault(),
            Destacado = _context.ContratosDestacado.Any(c =>
                c.EventoId == e.Id && c.Estado == EstadoContrato.Activo && c.FechaInicio <= ahora && c.FechaFin > ahora)
        });
    }

    public async Task<Dictionary<int, int>> ContarPorCategoriaAsync(CancellationToken ct = default)
    {
        var version = await _cache.GetStringAsync(ClaveVersion, ct) ?? "0";
        var clave = $"catalogo:{version}:conteo-categorias";

        var enCache = await _cache.GetStringAsync(clave, ct);
        if (enCache != null)
            return JsonSerializer.Deserialize<Dictionary<int, int>>(enCache)!;

        var conteo = await EventosPublicados(DateTime.Now)
            .GroupBy(e => e.CategoriaId)
            .Select(g => new { g.Key, Cantidad = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Cantidad, ct);

        await _cache.SetStringAsync(clave, JsonSerializer.Serialize(conteo), OpcionesCache, ct);
        return conteo;
    }

    // Eventos aprobados que aún no empiezan (HU02: "solo Aprobado y con fecha futura"): base de todas las consultas públicas
    private IQueryable<Evento> EventosPublicados(DateTime desde) =>
        _context.Eventos
            .AsNoTracking()
            .Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha >= desde);

    private async Task<ResultadoCatalogo> ConsultarAsync(CatalogoFiltro filtro, CancellationToken ct)
    {
        // Un filtro "desde" en el pasado o de hoy no trae eventos que ya empezaron
        var ahora = DateTime.Now;
        var query = EventosPublicados(filtro.Desde?.Date > ahora ? filtro.Desde.Value.Date : ahora);

        if (filtro.Hasta.HasValue)
        {
            var hastaExclusivo = filtro.Hasta.Value.Date.AddDays(1);
            query = query.Where(e => e.Fecha < hastaExclusivo);
        }

        if (filtro.Buscar != null)
        {
            var patron = $"%{EscaparLike(filtro.Buscar)}%";
            query = query.Where(e =>
                EF.Functions.Like(e.Titulo, patron, "\\") ||
                EF.Functions.Like(e.Ubicacion, patron, "\\") ||
                EF.Functions.Like(e.Categoria!.Nombre, patron, "\\"));
        }

        if (filtro.CategoriaId.HasValue)
            query = query.Where(e => e.CategoriaId == filtro.CategoriaId.Value);

        // Precio: el evento debe tener al menos un tipo de entrada dentro del rango pedido
        var precioMin = filtro.SoloGratis ? null : filtro.PrecioMin;
        var precioMax = filtro.SoloGratis ? 0 : filtro.PrecioMax;

        if (precioMin.HasValue || precioMax.HasValue)
        {
            query = query.Where(e => e.TiposEntrada.Any(t =>
                (precioMin == null || t.Precio >= precioMin) &&
                (precioMax == null || t.Precio <= precioMax)));
        }

        var proyeccion = ProyectarTarjetas(query);

        var total = await proyeccion.CountAsync(ct);
        var totalPaginas = Math.Max(1, (int)Math.Ceiling(total / (double)CatalogoFiltro.TamanoPagina));
        var pagina = Math.Min(filtro.Pagina, totalPaginas);

        List<EventoCardViewModel> eventos;

        // HU02: los eventos con un paquete publicitario vigente (HU12) aparecen primero en cualquier orden
        if (filtro.Orden is OrdenCatalogo.PrecioAsc or OrdenCatalogo.PrecioDesc)
        {
            // SQLite no ordena columnas decimal en el servidor: se ordena en memoria sobre el resultado filtrado
            var todos = await proyeccion.ToListAsync(ct);
            var ordenados = filtro.Orden == OrdenCatalogo.PrecioAsc
                ? todos.OrderByDescending(e => e.Destacado).ThenBy(e => e.PrecioDesde ?? decimal.MaxValue).ThenBy(e => e.Fecha)
                : todos.OrderByDescending(e => e.Destacado).ThenByDescending(e => e.PrecioDesde ?? -1).ThenBy(e => e.Fecha);

            eventos = ordenados
                .Skip((pagina - 1) * CatalogoFiltro.TamanoPagina)
                .Take(CatalogoFiltro.TamanoPagina)
                .ToList();
        }
        else
        {
            var ordenada = filtro.Orden == OrdenCatalogo.Recientes
                ? proyeccion.OrderByDescending(e => e.Destacado).ThenByDescending(e => e.Id)
                : proyeccion.OrderByDescending(e => e.Destacado).ThenBy(e => e.Fecha).ThenBy(e => e.Id);

            eventos = await ordenada
                .Skip((pagina - 1) * CatalogoFiltro.TamanoPagina)
                .Take(CatalogoFiltro.TamanoPagina)
                .ToListAsync(ct);
        }

        return new ResultadoCatalogo
        {
            Eventos = eventos,
            Total = total,
            Pagina = pagina,
            TotalPaginas = totalPaginas
        };
    }

    public async Task<List<Categoria>> ObtenerCategoriasAsync(CancellationToken ct = default)
    {
        var enCache = await _cache.GetStringAsync(ClaveCategorias, ct);
        if (enCache != null)
            return JsonSerializer.Deserialize<List<Categoria>>(enCache)!;

        var categorias = await _context.Categorias
            .AsNoTracking()
            .OrderBy(c => c.Id == ApplicationDbContext.CategoriaOtrosId)
            .ThenBy(c => c.Nombre)
            .Select(c => new Categoria { Id = c.Id, Nombre = c.Nombre, Icono = c.Icono, Color = c.Color, Imagen = c.Imagen })
            .ToListAsync(ct);

        await _cache.SetStringAsync(ClaveCategorias, JsonSerializer.Serialize(categorias),
            new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(1)), ct);

        return categorias;
    }

    public Task InvalidarAsync(CancellationToken ct = default) =>
        _cache.SetStringAsync(ClaveVersion, Guid.NewGuid().ToString("N"), ct);

    private static string EscaparLike(string texto) =>
        texto.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
