using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using QRCoder;

namespace ActivaFest.Services.Entradas;

public class QrOptions
{
    // Clave secreta para cifrar los QR. En producción se define por variable de entorno Qr__Secreto.
    public string Secreto { get; set; } = "";
}

public static class CodigoSeguro
{
    // 16 bytes aleatorios (128 bits) en base64url: 22 caracteres, aptos para URL y QR
    public static string Generar(int bytes = 16)
    {
        var datos = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public interface IQrEntradaService
{
    // Texto que va dentro del QR: "AF2.{token cifrado}". Cifra el código y, si se indica, la ubicación (HU27)
    string CrearContenido(string codigoEntrada, string? ubicacion = null);

    // Devuelve el código de la entrada si el token es auténtico; null si fue alterado o no es de ActivaFest
    string? LeerContenido(string contenido);

    // Ubicación cifrada en el QR ("Zona|Fila|Número" o la zona); null si no la tiene o el QR no es válido
    string? LeerUbicacion(string contenido);

    byte[] GenerarPng(string codigoEntrada, string? ubicacion = null, int pixelesPorModulo = 10);
}

// =============================
// 🔐 QR CIFRADO DE LA ENTRADA (HU07)
// =============================
// El token se cifra con AES-256-GCM (cifrado autenticado): quien lee el QR no ve el código de la entrada
// y cualquier alteración invalida el token. Las entradas emitidas con el formato anterior "AF1" (firmado
// con HMAC) se siguen aceptando en la puerta.
public class QrEntradaService : IQrEntradaService
{
    private const string Prefijo = "AF2";
    private const string PrefijoFirmado = "AF1";
    private const int TamanoNonce = 12, TamanoEtiqueta = 16;

    private readonly byte[] _claveFirma;
    private readonly byte[] _claveCifrado;

    public QrEntradaService(IOptions<QrOptions> opciones, IWebHostEnvironment env, ILogger<QrEntradaService> logger)
    {
        var secreto = opciones.Value.Secreto;
        if (string.IsNullOrWhiteSpace(secreto))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Configura Qr:Secreto (variable Qr__Secreto) para cifrar las entradas en producción.");

            logger.LogWarning("Qr:Secreto no configurado: se usa una clave de desarrollo. No la uses en producción.");
            secreto = "activafest-clave-solo-desarrollo";
        }

        _claveFirma = SHA256.HashData(Encoding.UTF8.GetBytes(secreto));
        // Clave distinta para cifrar, derivada del mismo secreto
        _claveCifrado = HKDF.DeriveKey(HashAlgorithmName.SHA256, _claveFirma, 32, info: Encoding.UTF8.GetBytes("activafest-qr-cifrado"));
    }

    public string CrearContenido(string codigoEntrada, string? ubicacion = null)
    {
        // El código nunca contiene "|" (base64url): sirve de separador con la ubicación
        var texto = Encoding.UTF8.GetBytes(ubicacion == null ? codigoEntrada : $"{codigoEntrada}|{ubicacion}");
        var token = new byte[TamanoNonce + texto.Length + TamanoEtiqueta];
        var nonce = token.AsSpan(0, TamanoNonce);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_claveCifrado, TamanoEtiqueta);
        aes.Encrypt(nonce, texto, token.AsSpan(TamanoNonce, texto.Length), token.AsSpan(TamanoNonce + texto.Length), Asociado);

        return $"{Prefijo}.{Base64Url(token)}";
    }

    public string? LeerContenido(string contenido)
    {
        var partes = (contenido ?? "").Trim().Split('.');
        return partes switch
        {
            [Prefijo, var token] => Descifrar(token)?.Split('|', 2)[0],
            [PrefijoFirmado, var codigo, var firma] when codigo.Length > 0 => VerificarFirma(codigo, firma),
            _ => null
        };
    }

    public string? LeerUbicacion(string contenido)
    {
        var partes = (contenido ?? "").Trim().Split('.');
        if (partes is not [Prefijo, var token])
            return null;
        var texto = Descifrar(token);
        var separador = texto?.IndexOf('|') ?? -1;
        return separador < 0 ? null : texto![(separador + 1)..];
    }

    public byte[] GenerarPng(string codigoEntrada, string? ubicacion = null, int pixelesPorModulo = 10)
    {
        using var generador = new QRCodeGenerator();
        using var datos = generador.CreateQrCode(CrearContenido(codigoEntrada, ubicacion), QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(datos).GetGraphic(pixelesPorModulo);
    }

    // El prefijo va como dato asociado: un token no se puede reutilizar con otro formato
    private static readonly byte[] Asociado = Encoding.ASCII.GetBytes(Prefijo);

    private string? Descifrar(string tokenBase64)
    {
        byte[] token;
        try { token = DesdeBase64Url(tokenBase64); }
        catch (FormatException) { return null; }

        if (token.Length <= TamanoNonce + TamanoEtiqueta)
            return null;

        var largo = token.Length - TamanoNonce - TamanoEtiqueta;
        var texto = new byte[largo];
        try
        {
            using var aes = new AesGcm(_claveCifrado, TamanoEtiqueta);
            aes.Decrypt(token.AsSpan(0, TamanoNonce), token.AsSpan(TamanoNonce, largo), token.AsSpan(TamanoNonce + largo), texto, Asociado);
            return Encoding.UTF8.GetString(texto);
        }
        catch (CryptographicException)
        {
            return null; // alterado o cifrado con otra clave
        }
    }

    private string? VerificarFirma(string codigo, string firma)
    {
        var esperada = Encoding.ASCII.GetBytes(Firmar(codigo));
        var recibida = Encoding.ASCII.GetBytes(firma);
        // Comparación en tiempo constante para no filtrar información de la firma
        return CryptographicOperations.FixedTimeEquals(esperada, recibida) ? codigo : null;
    }

    // Formato anterior (AF1): se conserva solo para validar entradas ya emitidas
    public string Firmar(string codigo)
    {
        var hash = HMACSHA256.HashData(_claveFirma, Encoding.UTF8.GetBytes(codigo));
        return Base64Url(hash.AsSpan(0, 16).ToArray());
    }

    private static string Base64Url(byte[] datos) => Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DesdeBase64Url(string texto)
    {
        var b64 = texto.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
    }
}
