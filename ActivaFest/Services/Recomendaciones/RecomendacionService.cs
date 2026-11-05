using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.Text;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Asistente;

namespace ActivaFest.Services.Recomendaciones;

public record Ubicacion(double Latitud, double Longitud);

public interface IRecomendacionService
{
    Task RegistrarVistaAsync(string usuarioId, int eventoId, CancellationToken ct = default);

    Task RegistrarBusquedaAsync(string usuarioId, int? categoriaId, string? termino, CancellationToken ct = default);

    Task<int> ContarHistorialAsync(string usuarioId, CancellationToken ct = default);

    Task<int> BorrarHistorialAsync(string usuarioId, CancellationToken ct = default);

    // Sin historial (o anónimo) devuelve los más populares cercanos (HU17)
    Task<List<EventoCardViewModel>> RecomendarAsync(string? usuarioId, Ubicacion? ubicacion, int maximo = 6, CancellationToken ct = default);
}

// =============================
// 🎯 RECOMENDACIONES PERSONALIZADAS (HU17 / RF15)
// =============================
// Filtrado por contenido con ML.NET: cada evento se representa con un vector de palabras (título, categoría
// y descripción) y el perfil del usuario es la suma ponderada de los vectores de lo que vio, compró y buscó.
// La afinidad es la similitud coseno entre el perfil y cada evento próximo.
//
// Con historial:  0.55 afinidad + 0.20 cercanía + 0.15 popularidad + 0.05 pronto + 0.05 destacado
// Usuario nuevo:  0.50 cercanía + 0.40 popularidad + 0.05 pronto + 0.05 destacado  ("populares cercanos")
public class RecomendacionService : IRecomendacionService
{
    public const int MaximoRecomendaciones = 6;

    private const int DiasHistorial = 90;
    private const double VidaMediaDias = 30;
    private const double RadioKm = 50;
    private const int MaxRegistrosPorUsuario = 300;

    // Peso de cada señal en el perfil del usuario
    private const double PesoVista = 1, PesoBusqueda = 2, PesoCompra = 3;

    private static readonly MLContext Ml = new(seed: 0);

    private readonly ApplicationDbContext _db;

    public RecomendacionService(ApplicationDbContext db)
    {
        _db = db;
    }

    // =============================
    // 📝 HISTORIAL (HISTORIAL_BUSQUEDA y VISUALIZACION_EVENTO)
    // =============================
    public async Task RegistrarVistaAsync(string usuarioId, int eventoId, CancellationToken ct = default)
    {
        // Recargar la ficha no infla el interés: una vista por evento por hora
        var haceUnaHora = DateTime.UtcNow.AddHours(-1);
        if (await _db.VisualizacionesEvento.AnyAsync(v => v.UsuarioId == usuarioId && v.EventoId == eventoId && v.Fecha > haceUnaHora, ct))
            return;

        _db.VisualizacionesEvento.Add(new VisualizacionEvento { UsuarioId = usuarioId, EventoId = eventoId });
        await _db.SaveChangesAsync(ct);
        await RecortarAsync(_db.VisualizacionesEvento, usuarioId, ct);
    }

    // Una búsqueda por categoría se guarda con el nombre de la categoría como término
    public async Task RegistrarBusquedaAsync(string usuarioId, int? categoriaId, string? termino, CancellationToken ct = default)
    {
        var terminos = new List<string>();

        if (categoriaId.HasValue)
        {
            var categoria = await _db.Categorias.Where(c => c.Id == categoriaId).Select(c => c.Nombre).FirstOrDefaultAsync(ct);
            if (categoria != null)
                terminos.Add(BaseConocimiento.Normalizar(categoria));
        }

        var limpio = BaseConocimiento.Normalizar(termino ?? "");
        if (limpio.Length >= 3)
            terminos.Add(limpio.Length > 100 ? limpio[..100] : limpio);

        if (terminos.Count == 0)
            return;

        _db.HistorialBusquedas.AddRange(terminos.Distinct().Select(t => new HistorialBusqueda { UsuarioId = usuarioId, Termino = t }));
        await _db.SaveChangesAsync(ct);
        await RecortarAsync(_db.HistorialBusquedas, usuarioId, ct);
    }

    public async Task<int> ContarHistorialAsync(string usuarioId, CancellationToken ct = default) =>
        await _db.HistorialBusquedas.CountAsync(h => h.UsuarioId == usuarioId, ct) +
        await _db.VisualizacionesEvento.CountAsync(v => v.UsuarioId == usuarioId, ct);

    public async Task<int> BorrarHistorialAsync(string usuarioId, CancellationToken ct = default) =>
        await _db.HistorialBusquedas.Where(h => h.UsuarioId == usuarioId).ExecuteDeleteAsync(ct) +
        await _db.VisualizacionesEvento.Where(v => v.UsuarioId == usuarioId).ExecuteDeleteAsync(ct);

    // Se conservan solo los registros más recientes de cada usuario
    private static async Task RecortarAsync<T>(DbSet<T> tabla, string usuarioId, CancellationToken ct) where T : class
    {
        var limite = await tabla
            .Where(r => EF.Property<string>(r, "UsuarioId") == usuarioId)
            .OrderByDescending(r => EF.Property<int>(r, "Id"))
            .Skip(MaxRegistrosPorUsuario)
            .Select(r => (int?)EF.Property<int>(r, "Id"))
            .FirstOrDefaultAsync(ct);

        if (limite.HasValue)
            await tabla.Where(r => EF.Property<string>(r, "UsuarioId") == usuarioId && EF.Property<int>(r, "Id") <= limite).ExecuteDeleteAsync(ct);
    }

    // =============================
    // 🎯 RECOMENDAR
    // =============================
    private record Candidato(int Id, string Titulo, string Descripcion, string Ubicacion, DateTime Fecha, double? Latitud, double? Longitud,
        int CategoriaId, string Categoria, string? Icono, string? Color, decimal? PrecioDesde, int Capacidad, int Vendidas, int Visitas,
        string? Portada, bool Destacado)
    {
        public string Texto => $"{Titulo} {Categoria} {Categoria} {Descripcion}";
    }

    // Algo que el usuario vio, compró o buscó: su texto y su peso (ya con el decaimiento por antigüedad)
    private record Senal(string Etiqueta, string Texto, double Peso);

    public async Task<List<EventoCardViewModel>> RecomendarAsync(string? usuarioId, Ubicacion? ubicacion, int maximo = MaximoRecomendaciones,
        CancellationToken ct = default)
    {
        var ahora = DateTime.Now;
        maximo = Math.Clamp(maximo, 1, MaximoRecomendaciones);

        var candidatos = await _db.Eventos.AsNoTracking()
            .Where(e => e.Estado == EstadoEvento.Aprobado && e.Fecha >= ahora)
            .Where(e => usuarioId == null || (e.OrganizadorId != usuarioId && !_db.Entradas.Any(x => x.EventoId == e.Id && x.UsuarioId == usuarioId)))
            .OrderBy(e => e.Fecha)
            .Take(200)
            .Select(e => new Candidato(
                e.Id, e.Titulo, e.Descripcion ?? "", e.Ubicacion, e.Fecha, e.Latitud, e.Longitud,
                e.CategoriaId, e.Categoria!.Nombre, e.Categoria.Icono, e.Categoria.Color,
                e.TiposEntrada.Min(t => (decimal?)t.Precio),
                e.TiposEntrada.Sum(t => t.Capacidad),
                e.TiposEntrada.Sum(t => t.Vendidas),
                e.Visitas,
                e.Imagenes.OrderBy(i => i.Orden).Select(i => i.Ruta).FirstOrDefault(),
                _db.ContratosDestacado.Any(c => c.EventoId == e.Id && c.Estado == EstadoContrato.Activo && c.FechaInicio <= ahora && c.FechaFin > ahora)))
            .ToListAsync(ct);

        if (candidatos.Count == 0)
            return new();

        var senales = usuarioId == null ? new List<Senal>() : await SenalesAsync(usuarioId, ct);
        var afinidades = Afinidades(candidatos, senales);
        var conHistorial = senales.Count > 0;
        var maxVisitas = Math.Max(1, candidatos.Max(c => c.Visitas));

        var puntuados = candidatos.Select((c, i) =>
        {
            var (afinidad, parecidoA) = afinidades[i];
            double? distancia = ubicacion != null && c.Latitud.HasValue && c.Longitud.HasValue
                ? DistanciaKm(ubicacion.Latitud, ubicacion.Longitud, c.Latitud.Value, c.Longitud.Value)
                : null;
            var cercania = distancia.HasValue ? Math.Max(0, 1 - distancia.Value / RadioKm) : 0;
            var vendido = c.Capacidad == 0 ? 0 : Math.Min(1, (double)c.Vendidas / c.Capacidad);
            var popularidad = 0.6 * vendido + 0.4 * c.Visitas / maxVisitas;
            var dias = (c.Fecha - ahora).TotalDays;
            var pronto = Math.Max(0, 1 - dias / 30);
            var destacado = c.Destacado ? 1 : 0;

            var aportes = conHistorial
                ? new[] { (0.55 * afinidad, "afinidad"), (0.20 * cercania, "cercania"), (0.15 * popularidad, "popular"), (0.05 * pronto, "pronto"), (0.05 * destacado, "") }
                : new[] { (0.50 * cercania, "cercania"), (0.40 * popularidad, "popular"), (0.05 * pronto, "pronto"), (0.05 * destacado, "") };

            // El motivo es el factor que más aportó (explicación para el usuario)
            var principal = aportes.Where(a => a.Item1 > 0.001 && a.Item2 != "").OrderByDescending(a => a.Item1).Select(a => a.Item2).FirstOrDefault();
            var motivo = principal switch
            {
                "afinidad" => $"Parecido a «{parecidoA}»",
                "cercania" => $"A {distancia:0.0} km de ti",
                "popular" when vendido > 0 => $"Popular: {vendido:P0} vendido",
                "popular" => "De los más vistos",
                "pronto" => dias < 7 ? "Es muy pronto" : "Se acerca la fecha",
                _ => "Próximamente en ActivaFest"
            };

            return (Puntaje: aportes.Sum(a => a.Item1), Tarjeta: new EventoCardViewModel
            {
                Id = c.Id,
                Titulo = c.Titulo,
                Categoria = c.Categoria,
                CategoriaIcono = c.Icono,
                CategoriaColor = c.Color,
                Ubicacion = c.Ubicacion,
                Fecha = c.Fecha,
                PrecioDesde = c.PrecioDesde,
                ImagenPortada = c.Portada,
                Destacado = c.Destacado,
                DistanciaKm = distancia.HasValue ? Math.Round(distancia.Value, 1) : null,
                Motivo = motivo
            });
        });

        return puntuados
            .OrderByDescending(p => p.Puntaje)
            .ThenBy(p => p.Tarjeta.Fecha)
            .Take(maximo)
            .Select(p => p.Tarjeta)
            .ToList();
    }

    // Lo que vio, compró y buscó en los últimos 90 días (lo reciente pesa más)
    private async Task<List<Senal>> SenalesAsync(string usuarioId, CancellationToken ct)
    {
        var desde = DateTime.UtcNow.AddDays(-DiasHistorial);
        double Decaimiento(DateTime fecha) => Math.Pow(0.5, (DateTime.UtcNow - fecha).TotalDays / VidaMediaDias);

        var vistas = await (from v in _db.VisualizacionesEvento.AsNoTracking()
                            join e in _db.Eventos on v.EventoId equals e.Id
                            where v.UsuarioId == usuarioId && v.Fecha >= desde
                            select new { e.Titulo, e.Descripcion, Categoria = e.Categoria!.Nombre, v.Fecha }).ToListAsync(ct);

        var compras = await _db.Entradas.AsNoTracking()
            .Where(x => x.UsuarioId == usuarioId && x.FechaEmision >= desde)
            .Select(x => new { x.Evento!.Titulo, x.Evento.Descripcion, Categoria = x.Evento.Categoria!.Nombre, Fecha = x.FechaEmision })
            .ToListAsync(ct);

        var busquedas = await _db.HistorialBusquedas.AsNoTracking()
            .Where(h => h.UsuarioId == usuarioId && h.Fecha >= desde)
            .Select(h => new { h.Termino, h.Fecha })
            .ToListAsync(ct);

        return vistas.Select(v => new Senal(v.Titulo, $"{v.Titulo} {v.Categoria} {v.Categoria} {v.Descripcion}", PesoVista * Decaimiento(v.Fecha)))
            .Concat(compras.Select(c => new Senal(c.Titulo, $"{c.Titulo} {c.Categoria} {c.Categoria} {c.Descripcion}", PesoCompra * Decaimiento(c.Fecha))))
            .Concat(busquedas.Select(b => new Senal(b.Termino, b.Termino, PesoBusqueda * Decaimiento(b.Fecha))))
            .ToList();
    }

    // =============================
    // 🤖 FILTRADO POR CONTENIDO CON ML.NET
    // =============================
    private class Documento
    {
        public string Texto { get; set; } = "";
    }

    // Para cada candidato: similitud coseno con el perfil del usuario [0, 1] y la señal que más se le parece
    private static List<(double Afinidad, string ParecidoA)> Afinidades(List<Candidato> candidatos, List<Senal> senales)
    {
        if (senales.Count == 0)
            return candidatos.Select(_ => (0d, "")).ToList();

        var textos = candidatos.Select(c => c.Texto).Concat(senales.Select(s => s.Texto)).ToList();
        var vectores = Vectorizar(textos);
        var deCandidatos = vectores.Take(candidatos.Count).ToList();
        var deSenales = vectores.Skip(candidatos.Count).ToList();

        // Perfil = suma de las señales ponderadas por su peso
        var perfil = new float[vectores[0].Length];
        for (var s = 0; s < senales.Count; s++)
            for (var k = 0; k < perfil.Length; k++)
                perfil[k] += (float)senales[s].Peso * deSenales[s][k];

        return deCandidatos.Select(v =>
        {
            var parecido = Enumerable.Range(0, senales.Count).MaxBy(s => Coseno(v, deSenales[s]) * senales[s].Peso);
            return (Coseno(v, perfil), senales[parecido].Etiqueta);
        }).ToList();
    }

    // Bolsa de palabras (sin palabras vacías en español) normalizada: cada texto pasa a ser un vector numérico
    private static List<float[]> Vectorizar(List<string> textos)
    {
        var datos = Ml.Data.LoadFromEnumerable(textos.Select(t => new Documento { Texto = BaseConocimiento.Normalizar(t) }));

        var opciones = new TextFeaturizingEstimator.Options
        {
            StopWordsRemoverOptions = new StopWordsRemovingEstimator.Options { Language = TextFeaturizingEstimator.Language.Spanish },
            WordFeatureExtractor = new WordBagEstimator.Options { NgramLength = 1 },
            CharFeatureExtractor = null,
            Norm = TextFeaturizingEstimator.NormFunction.L2
        };

        var modelo = Ml.Transforms.Text.FeaturizeText("Vector", opciones, nameof(Documento.Texto)).Fit(datos);
        return modelo.Transform(datos)
            .GetColumn<VBuffer<float>>("Vector")
            .Select(v => v.DenseValues().ToArray())
            .ToList();
    }

    private static double Coseno(float[] a, float[] b)
    {
        double punto = 0, normaA = 0, normaB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            punto += a[i] * b[i];
            normaA += a[i] * a[i];
            normaB += b[i] * b[i];
        }
        return normaA == 0 || normaB == 0 ? 0 : Math.Max(0, punto / Math.Sqrt(normaA * normaB));
    }

    // Distancia en línea recta entre dos coordenadas (fórmula de Haversine)
    public static double DistanciaKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radioTierra = 6371;
        double ARad(double grados) => grados * Math.PI / 180;

        var dLat = ARad(lat2 - lat1);
        var dLon = ARad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ARad(lat1)) * Math.Cos(ARad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radioTierra * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
