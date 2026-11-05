using System.Text;
using Microsoft.AspNetCore.Http;
using ActivaFest.Services.Imagenes;

namespace ActivaFest.Tests;

public class ValidadorImagenesTests
{
    private static readonly byte[] CabeceraPng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
    private static readonly byte[] CabeceraJpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 0 };

    private static IFormFile Archivo(string nombre, byte[] contenido, long? tamano = null)
    {
        var stream = new MemoryStream(contenido);
        return new FormFile(stream, 0, tamano ?? contenido.Length, "Fotos", nombre);
    }

    [Theory]
    [InlineData("recinto.png")]
    [InlineData("RECINTO.PNG")]
    public void Acepta_png_valido(string nombre)
    {
        Assert.Null(ValidadorImagenes.Validar(Archivo(nombre, CabeceraPng)));
    }

    [Fact]
    public void Acepta_jpg_valido()
    {
        Assert.Null(ValidadorImagenes.Validar(Archivo("foto.jpeg", CabeceraJpg)));
    }

    [Fact]
    public void Acepta_webp_valido()
    {
        var webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");
        Assert.Null(ValidadorImagenes.Validar(Archivo("foto.webp", webp)));
    }

    [Fact]
    public void Rechaza_archivo_con_extension_de_imagen_pero_contenido_falso()
    {
        var falso = Encoding.UTF8.GetBytes("<script>alert(1)</script>");
        Assert.NotNull(ValidadorImagenes.Validar(Archivo("malicioso.jpg", falso)));
    }

    [Fact]
    public void Rechaza_riff_que_no_es_webp()
    {
        var wav = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEfmt ");
        Assert.NotNull(ValidadorImagenes.Validar(Archivo("audio.webp", wav)));
    }

    [Theory]
    [InlineData("animacion.gif")]
    [InlineData("documento.pdf")]
    [InlineData("sin-extension")]
    public void Rechaza_extensiones_no_permitidas(string nombre)
    {
        Assert.NotNull(ValidadorImagenes.Validar(Archivo(nombre, CabeceraPng)));
    }

    [Fact]
    public void Rechaza_imagenes_de_mas_de_5_mb()
    {
        var error = ValidadorImagenes.Validar(Archivo("grande.png", CabeceraPng, ValidadorImagenes.TamanoMaximo + 1));
        Assert.Contains("5 MB", error);
    }

    [Fact]
    public void Rechaza_archivo_vacio()
    {
        Assert.NotNull(ValidadorImagenes.Validar(Archivo("vacio.png", Array.Empty<byte>())));
    }
}
