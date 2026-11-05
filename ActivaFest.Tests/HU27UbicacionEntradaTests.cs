using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Entradas;
using ActivaFest.Services.Plano;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// Lee un PNG RGB de 8 bits sin filtros (como el que genera PlanoImagen) para revisar sus píxeles
public static class LectorPng
{
    public static (int Ancho, int Alto, Func<int, int, (byte R, byte G, byte B)> Pixel) Leer(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8));
        int ancho = 0, alto = 0;
        using var idat = new MemoryStream();
        for (var i = 8; i < png.Length;)
        {
            var largo = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(i));
            var tipo = System.Text.Encoding.ASCII.GetString(png, i + 4, 4);
            if (tipo == "IHDR") { ancho = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(i + 8)); alto = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(i + 12)); }
            if (tipo == "IDAT") idat.Write(png, i + 8, largo);
            i += 12 + largo;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var crudo = new MemoryStream();
        zlib.CopyTo(crudo);
        var datos = crudo.ToArray();
        Assert.Equal(alto * (ancho * 3 + 1), datos.Length);

        return (ancho, alto, (x, y) =>
        {
            var p = y * (ancho * 3 + 1) + 1 + x * 3;
            return (datos[p], datos[p + 1], datos[p + 2]);
        });
    }
}

// =============================
// 📍 HU27: UBICACIÓN EN EL QR Y PLANO PNG (pruebas unitarias)
// =============================
public class UbicacionEnQrYPlanoTests
{
    private readonly QrEntradaService _qr = Fabrica.Qr();

    [Fact]
    public void El_token_qr_incluye_zona_fila_y_asiento_cifrados()
    {
        var codigo = CodigoSeguro.Generar();

        var contenido = _qr.CrearContenido(codigo, "Platea B|F|12");

        Assert.Equal(codigo, _qr.LeerContenido(contenido));          // la puerta sigue leyendo el código
        Assert.Equal("Platea B|F|12", _qr.LeerUbicacion(contenido));  // y la ubicación viaja dentro del token
        Assert.DoesNotContain("Platea", contenido);                    // cifrada: no se lee a simple vista
        Assert.Null(_qr.LeerUbicacion(_qr.CrearContenido(codigo)));
    }

    [Fact]
    public void El_plano_png_resalta_el_asiento_de_la_entrada()
    {
        var zonas = new List<TipoEntrada> { new() { Id = 1, Nombre = "Platea", Filas = 3, AsientosPorFila = 10, AsientosNumerados = true, Capacidad = 30 } };
        var asientos = PlanoService.GenerarAsientos(zonas);
        var id = 0;
        asientos.ForEach(a => a.Id = ++id);
        var evento = new Evento { PlantillaPlano = PlantillaPlano.Teatro, TiposEntrada = zonas };
        var mio = asientos.Single(a => a.Fila == "B" && a.Numero == 4);

        var png = PlanoService.PngDeEvento(evento, asientos, new HashSet<int> { mio.Id }, new HashSet<int>());
        var (ancho, alto, pixel) = LectorPng.Leer(png);

        Assert.Equal(600, ancho);
        Assert.True(alto > 200);
        Assert.Equal(((byte)236, (byte)72, (byte)153), pixel((int)(mio.PosX * 0.75), (int)(mio.PosY * 0.75)));  // rosa: resaltado
        var otro = asientos.Single(a => a.Fila == "A" && a.Numero == 1);
        Assert.NotEqual(((byte)236, (byte)72, (byte)153), pixel((int)(otro.PosX * 0.75), (int)(otro.PosY * 0.75)));
    }

    [Theory]
    [InlineData(PlantillaPlano.CampoAbierto)]
    [InlineData(PlantillaPlano.Estadio)]
    [InlineData(PlantillaPlano.Feria)]
    [InlineData(PlantillaPlano.Coliseo)]
    [InlineData(PlantillaPlano.Discoteca)]
    public void En_plantillas_por_zona_se_resalta_la_zona(PlantillaPlano plantilla)
    {
        var zonas = new List<TipoEntrada>
        {
            new() { Id = 1, Nombre = "VIP", Capacidad = 10, Color = "#6366f1" },
            new() { Id = 2, Nombre = "General", Capacidad = 10, Color = "#0d9488" }
        };
        var evento = new Evento { PlantillaPlano = plantilla, TiposEntrada = zonas };

        var png = PlanoService.PngDeEvento(evento, Array.Empty<Asiento>(), new HashSet<int>(), new HashSet<int> { 2 });
        var (_, _, pixel) = LectorPng.Leer(png);

        var (x, y) = PlanoService.CentroZona(plantilla, 1, 2);
        Assert.Equal(((byte)236, (byte)72, (byte)153), pixel((int)(x * 0.75), (int)(y * 0.75)));
    }
}

// =============================
// 📍 HU27: LA ENTRADA MUESTRA ZONA, FILA, ASIENTO Y PUERTA (pruebas de integración)
// =============================
public class HU27UbicacionEntradaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU27UbicacionEntradaTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    // Compra real de los asientos F-2 y F-3 en un teatro (zona Platea B, Puerta 2)
    private async Task<(HttpClient Cliente, string Email, string Titulo, int EventoId)> CompraEnTeatroAsync(DateTime? fecha = null)
    {
        int eventoId;
        string titulo;
        List<int> asientos;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var evento = new Evento
            {
                Titulo = $"Sinfónico {Guid.NewGuid():N}", Descripcion = "Concierto sinfónico.", CategoriaId = 1, Ubicacion = "Teatro Municipal",
                Fecha = fecha ?? DateTime.Now.AddDays(10), Estado = EstadoEvento.Aprobado, PlantillaPlano = PlantillaPlano.Teatro, Aforo = 40,
                TiposEntrada =
                {
                    new TipoEntrada { Nombre = "Platea A", Precio = 120, Capacidad = 20, AsientosNumerados = true, Filas = 5, AsientosPorFila = 4, PuertaIngreso = "Puerta 1" },
                    new TipoEntrada { Nombre = "Platea B", Precio = 80, Capacidad = 20, AsientosNumerados = true, Filas = 5, AsientosPorFila = 4, PuertaIngreso = "Puerta 2" }
                }
            };
            db.Eventos.Add(evento);
            db.SaveChanges();
            var generados = PlanoService.GenerarAsientos(evento.TiposEntrada.OrderBy(t => t.Id).ToList());
            generados.ForEach(a => a.TipoEntrada = null);
            db.Asientos.AddRange(generados);
            db.SaveChanges();
            (eventoId, titulo) = (evento.Id, evento.Titulo);
            asientos = db.Asientos.Where(a => a.TipoEntrada!.EventoId == evento.Id && a.Fila == "F" && (a.Numero == 2 || a.Numero == 3)).Select(a => a.Id).ToList();
        }

        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email);

        var campos = new List<(string, string)> { ("eventoId", eventoId.ToString()) };
        campos.AddRange(asientos.Select(a => ("asientos", a.ToString())));
        var checkout = (await PostAsync(cliente, $"/Eventos/Detalle/{eventoId}", "/Compras/Iniciar", campos.ToArray())).Headers.Location!.OriginalString;
        var codigo = checkout.Split('=')[1];
        var pasarela = (await PostAsync(cliente, checkout, "/Compras/Pagar", ("codigo", codigo))).Headers.Location!.OriginalString;
        await PostAsync(cliente, pasarela, "/Compras/PasarelaSimulada", ("codigo", codigo), ("aprobar", "true"));
        return (cliente, email, titulo, eventoId);
    }

    // Criterio: la entrada (vista) muestra zona, fila, asiento y puerta, con el plano y la ubicación resaltada
    [Fact]
    public async Task Mis_entradas_muestra_zona_fila_asiento_puerta_y_el_plano_con_la_ubicacion_resaltada()
    {
        var (cliente, _, _, _) = await CompraEnTeatroAsync();

        var html = await TextoAsync(await cliente.GetAsync("/Entradas"));
        Assert.Contains("<dt>Zona</dt><dd>Platea B</dd>", html);
        Assert.Contains("<dt>Fila</dt><dd>F</dd>", html);
        Assert.Contains("<dt>Asiento</dt><dd>2</dd>", html);
        Assert.Contains("<dt>Asiento</dt><dd>3</dd>", html);
        Assert.Contains("Ingresa por la <b>Puerta 2</b>", html);

        var plano = Regex.Match(html, @"data-url=""(/Entradas/Plano/\d+)""").Groups[1].Value;
        var svg = await cliente.GetStringAsync(plano);
        Assert.StartsWith("<svg", svg);
        Assert.Single(Regex.Matches(svg, "asiento asiento-tuyo"));
        Assert.Matches(@"class=""asiento asiento-tuyo""[^>]*data-fila=""F"" data-numero=""[23]""", svg);

        // Solo el dueño ve el plano de su entrada
        var otro = _app.Cliente();
        await RegistrarAsync(otro, Correo());
        Assert.Equal(HttpStatusCode.NotFound, (await otro.GetAsync(plano)).StatusCode);
    }

    // Criterio: el correo muestra zona, fila, asiento y puerta, con el plano y la ubicación resaltada
    [Fact]
    public async Task El_correo_trae_la_ubicacion_y_el_plano_con_los_asientos_resaltados()
    {
        var (_, email, titulo, _) = await CompraEnTeatroAsync();

        var asunto = $"Tus entradas para {titulo}";
        var correo = Assert.Single(_app.Correos.Para(email), c => c.Asunto == asunto);
        var html = WebUtility.HtmlDecode(correo.Html);
        Assert.Contains("Fila F, asiento 2", html);
        Assert.Contains("Fila F, asiento 3", html);
        Assert.Contains("🚪 Puerta 2", html);
        Assert.Contains("2 × Platea B", html);

        var archivos = _app.Correos.ArchivosDe(asunto);
        var plano = Assert.Single(archivos, a => a.NombreArchivo == "plano-tu-ubicacion.png");
        var (_, _, pixel) = LectorPng.Leer(plano.Contenido);
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var f2 = db.Asientos.Single(a => a.TipoEntrada!.Evento!.Titulo == titulo && a.Fila == "F" && a.Numero == 2);
        Assert.Equal(((byte)236, (byte)72, (byte)153), pixel((int)(f2.PosX * 0.75), (int)(f2.PosY * 0.75)));

        // El QR adjunto lleva la ubicación cifrada
        Assert.Equal(2, archivos.Count(a => a.NombreArchivo.StartsWith("entrada-")));
    }

    // Criterio: al validar en puerta, el escáner muestra la zona y el asiento
    [Fact]
    public async Task Al_validar_en_puerta_el_escaner_muestra_la_zona_y_el_asiento()
    {
        var (_, _, _, eventoId) = await CompraEnTeatroAsync(DateTime.Now.AddHours(1));

        string contenido;
        var emailPuerta = Correo();
        await RegistrarAsync(_app.Cliente(), emailPuerta);
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var personal = (await usuarios.FindByEmailAsync(emailPuerta))!;
            await usuarios.AddToRoleAsync(personal, Roles.ControlAcceso);
            db.AsignacionesAcceso.Add(new AsignacionAcceso { UsuarioId = personal.Id, EventoId = eventoId });
            await db.SaveChangesAsync();

            var entrada = await db.Entradas.Include(e => e.TipoEntrada).Include(e => e.Asiento)
                .FirstAsync(e => e.EventoId == eventoId && e.Asiento!.Numero == 3);
            contenido = scope.ServiceProvider.GetRequiredService<IQrEntradaService>().CrearContenido(entrada.Codigo, UbicacionEntrada.De(entrada).ParaQr);
        }

        var puerta = _app.Cliente();
        await IniciarSesionAsync(puerta, emailPuerta, "Clave1234");
        var token = await TokenAsync(puerta, $"/Acceso/Escanear/{eventoId}");
        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/acceso/validar") { Content = JsonContent.Create(new { eventoId, contenido }) };
        peticion.Headers.Add("RequestVerificationToken", token);
        var r = await (await puerta.SendAsync(peticion)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("INGRESO PERMITIDO", r.GetProperty("titulo").GetString());
        Assert.Equal("Platea B", r.GetProperty("zona").GetString());
        Assert.Equal("F-3", r.GetProperty("asiento").GetString());
        Assert.Equal("Puerta 2", r.GetProperty("puerta").GetString());
    }
}
