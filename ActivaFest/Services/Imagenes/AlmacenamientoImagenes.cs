namespace ActivaFest.Services.Imagenes;

public interface IAlmacenamientoImagenes
{
    // Guarda la imagen y devuelve la URL pública con la que se mostrará
    Task<string> GuardarAsync(IFormFile archivo, string carpeta, CancellationToken ct = default);

    Task EliminarAsync(string url, CancellationToken ct = default);
}

// =============================
// 🖼️ VALIDACIÓN DE IMÁGENES
// =============================
public static class ValidadorImagenes
{
    public const long TamanoMaximo = 5 * 1024 * 1024; // 5 MB

    private static readonly Dictionary<string, byte[][]> Firmas = new()
    {
        [".jpg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".png"] = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } },
        [".webp"] = new[] { "RIFF"u8.ToArray() }
    };

    public static IReadOnlyCollection<string> ExtensionesPermitidas => Firmas.Keys;

    // Devuelve null si es válida o el mensaje de error
    public static string? Validar(IFormFile archivo)
    {
        if (archivo.Length == 0)
            return $"El archivo {archivo.FileName} está vacío.";

        if (archivo.Length > TamanoMaximo)
            return $"La imagen {archivo.FileName} supera los 5 MB.";

        var extension = Path.GetExtension(archivo.FileName).ToLowerInvariant();
        if (!Firmas.TryGetValue(extension, out var firmas))
            return $"{archivo.FileName}: solo se permiten imágenes JPG, PNG o WEBP.";

        // Comprueba la firma real del archivo, no solo la extensión
        using var stream = archivo.OpenReadStream();
        var cabecera = new byte[12];
        var leidos = stream.Read(cabecera, 0, cabecera.Length);

        var firmaValida = firmas.Any(f => leidos >= f.Length && cabecera.Take(f.Length).SequenceEqual(f));
        if (firmaValida && extension == ".webp")
            firmaValida = leidos >= 12 && cabecera.Skip(8).Take(4).SequenceEqual("WEBP"u8.ToArray());

        return firmaValida ? null : $"{archivo.FileName} no es una imagen válida.";
    }
}

// Guarda las imágenes en wwwroot/uploads. En producción se reemplaza por un almacenamiento en la nube (HU09).
public class AlmacenamientoLocalImagenes : IAlmacenamientoImagenes
{
    private const string CarpetaRaiz = "uploads";

    private readonly IWebHostEnvironment _env;

    public AlmacenamientoLocalImagenes(IWebHostEnvironment env)
    {
        _env = env;
    }

    public async Task<string> GuardarAsync(IFormFile archivo, string carpeta, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(archivo.FileName).ToLowerInvariant();
        var nombre = $"{Guid.NewGuid():N}{extension}";
        var directorio = Path.Combine(_env.WebRootPath, CarpetaRaiz, carpeta);
        Directory.CreateDirectory(directorio);

        await using var destino = File.Create(Path.Combine(directorio, nombre));
        await archivo.CopyToAsync(destino, ct);

        return $"/{CarpetaRaiz}/{carpeta}/{nombre}";
    }

    public Task EliminarAsync(string url, CancellationToken ct = default)
    {
        if (!url.StartsWith($"/{CarpetaRaiz}/"))
            return Task.CompletedTask;

        var ruta = Path.GetFullPath(Path.Combine(_env.WebRootPath, url.TrimStart('/')));
        var raiz = Path.GetFullPath(Path.Combine(_env.WebRootPath, CarpetaRaiz));

        // Evita borrar archivos fuera de la carpeta de uploads
        if (ruta.StartsWith(raiz) && File.Exists(ruta))
            File.Delete(ruta);

        return Task.CompletedTask;
    }
}
