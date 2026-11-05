using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ActivaFest.Services.Imagenes;

public class ImagenesOptions
{
    // "Local" (wwwroot/uploads) o "Cloudinary"
    public string Proveedor { get; set; } = "Local";

    public CloudinaryOptions Cloudinary { get; set; } = new();
}

public class CloudinaryOptions
{
    public string CloudName { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string CarpetaRaiz { get; set; } = "activafest";

    public bool Configurado =>
        !string.IsNullOrWhiteSpace(CloudName) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}

// =============================
// ☁️ ALMACENAMIENTO DE IMÁGENES EN LA NUBE (HU09) — Cloudinary, capa gratuita
// =============================
// Subida firmada por el servidor: el secreto nunca llega al navegador.
public class CloudinaryAlmacenamiento : IAlmacenamientoImagenes
{
    private readonly HttpClient _http;
    private readonly CloudinaryOptions _opciones;
    private readonly ILogger<CloudinaryAlmacenamiento> _logger;

    public CloudinaryAlmacenamiento(HttpClient http, IOptions<ImagenesOptions> opciones, ILogger<CloudinaryAlmacenamiento> logger)
    {
        _http = http;
        _opciones = opciones.Value.Cloudinary;
        _logger = logger;
    }

    private string UrlApi(string accion) => $"https://api.cloudinary.com/v1_1/{_opciones.CloudName}/image/{accion}";

    public async Task<string> GuardarAsync(IFormFile archivo, string carpeta, CancellationToken ct = default)
    {
        var parametros = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["folder"] = $"{_opciones.CarpetaRaiz}/{carpeta}",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };

        using var contenido = new MultipartFormDataContent();
        foreach (var (clave, valor) in parametros)
            contenido.Add(new StringContent(valor), clave);
        contenido.Add(new StringContent(_opciones.ApiKey), "api_key");
        contenido.Add(new StringContent(Firmar(parametros, _opciones.ApiSecret)), "signature");

        await using var stream = archivo.OpenReadStream();
        var archivoContenido = new StreamContent(stream);
        contenido.Add(archivoContenido, "file", Path.GetFileName(archivo.FileName));

        using var respuesta = await _http.PostAsync(UrlApi("upload"), contenido, ct);
        var cuerpo = await respuesta.Content.ReadAsStringAsync(ct);

        if (!respuesta.IsSuccessStatusCode)
        {
            _logger.LogError("Cloudinary rechazó la imagen {Archivo}: {Estado} {Cuerpo}", archivo.FileName, (int)respuesta.StatusCode, cuerpo);
            throw new InvalidOperationException("No se pudo subir la imagen.");
        }

        using var doc = JsonDocument.Parse(cuerpo);
        return doc.RootElement.GetProperty("secure_url").GetString()!;
    }

    public async Task EliminarAsync(string url, CancellationToken ct = default)
    {
        var publicId = ExtraerPublicId(url, _opciones.CloudName);
        if (publicId == null)
            return;

        var parametros = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["public_id"] = publicId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };

        var formulario = new Dictionary<string, string>(parametros)
        {
            ["api_key"] = _opciones.ApiKey,
            ["signature"] = Firmar(parametros, _opciones.ApiSecret)
        };

        try
        {
            using var respuesta = await _http.PostAsync(UrlApi("destroy"), new FormUrlEncodedContent(formulario), ct);
            if (!respuesta.IsSuccessStatusCode)
                _logger.LogWarning("No se pudo borrar {PublicId} de Cloudinary: {Estado}", publicId, (int)respuesta.StatusCode);
        }
        catch (Exception ex)
        {
            // Una imagen huérfana en la nube no debe romper la edición del evento
            _logger.LogWarning(ex, "Error al borrar {PublicId} de Cloudinary", publicId);
        }
    }

    // Firma de Cloudinary: SHA-1 de "clave=valor&clave=valor" (orden alfabético) + api_secret
    public static string Firmar(IDictionary<string, string> parametros, string secreto)
    {
        var cadena = string.Join("&", parametros.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(cadena + secreto))).ToLowerInvariant();
    }

    // https://res.cloudinary.com/{cloud}/image/upload/v1712/activafest/eventos/abc.jpg -> activafest/eventos/abc
    public static string? ExtraerPublicId(string url, string cloudName)
    {
        var prefijo = $"https://res.cloudinary.com/{cloudName}/image/upload/";
        if (!url.StartsWith(prefijo, StringComparison.Ordinal))
            return null;

        var resto = url[prefijo.Length..];
        var partes = resto.Split('/').ToList();
        if (partes.Count > 1 && partes[0].Length > 1 && partes[0][0] == 'v' && partes[0][1..].All(char.IsDigit))
            partes.RemoveAt(0);

        var ruta = string.Join("/", partes);
        var punto = ruta.LastIndexOf('.');
        return punto > 0 ? ruta[..punto] : ruta;
    }
}
