using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 📝 HU09: PUBLICAR UN EVENTO (pruebas de integración)
// =============================
public class HU09PublicarEventoTests : IClassFixture<AppDePrueba>
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private readonly AppDePrueba _app;

    public HU09PublicarEventoTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<(HttpClient Cliente, string Email)> OrganizadorAsync()
    {
        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email, rol: Roles.Organizador);
        return (cliente, email);
    }

    // Formulario válido; cada prueba cambia solo el dato que quiere romper
    private static Dictionary<string, string> Datos(string titulo) => new()
    {
        ["Titulo"] = titulo,
        ["Descripcion"] = "Gran concierto de rock al aire libre con bandas locales.",
        ["CategoriaId"] = "1",
        ["Fecha"] = DateTime.Now.AddDays(10).ToString("yyyy-MM-ddTHH:mm"),
        ["Ubicacion"] = "Estadio Monumental, Arequipa",
        ["Latitud"] = "-16.4",
        ["Longitud"] = "-71.53",
        ["TiposEntrada[0].Nombre"] = "General",
        ["TiposEntrada[0].Precio"] = "50",
        ["TiposEntrada[0].Capacidad"] = "300",
        ["TiposEntrada[1].Nombre"] = "VIP",
        ["TiposEntrada[1].Precio"] = "120",
        ["TiposEntrada[1].Capacidad"] = "50"
    };

    private static async Task<HttpResponseMessage> PublicarAsync(HttpClient cliente, Dictionary<string, string> datos, int fotos = 3)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await TokenAsync(cliente, "/Eventos/Crear")), "__RequestVerificationToken");
        foreach (var (clave, valor) in datos)
            form.Add(new StringContent(valor), clave);

        for (var i = 0; i < fotos; i++)
        {
            var foto = new ByteArrayContent(Png);
            foto.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(foto, "Fotos", $"recinto{i}.png");
        }

        return await cliente.PostAsync("/Eventos/Crear", form);
    }

    private async Task<Evento?> BuscarAsync(string titulo)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Eventos
            .Include(e => e.TiposEntrada).Include(e => e.Imagenes)
            .FirstOrDefaultAsync(e => e.Titulo == titulo);
    }

    [Fact]
    public async Task El_evento_se_crea_pendiente_con_aforo_precios_y_fotos_y_no_es_visible_hasta_su_aprobacion()
    {
        var (organizador, email) = await OrganizadorAsync();
        var titulo = $"Rock {Guid.NewGuid():N}";

        var r = await PublicarAsync(organizador, Datos(titulo));

        Assert.Equal("/Eventos/MisEventos", r.Headers.Location!.OriginalString);
        var evento = await BuscarAsync(titulo);
        Assert.NotNull(evento);
        Assert.Equal(EstadoEvento.Pendiente, evento.Estado);
        Assert.Equal(350, evento.Aforo);
        Assert.Equal(new[] { ("General", 50m), ("VIP", 120m) }, evento.TiposEntrada.OrderBy(t => t.Precio).Select(t => (t.Nombre, t.Precio)));
        Assert.Equal(3, evento.Imagenes.Count);
        Assert.Contains(_app.Correos.Para(email), c => c.Asunto.Contains(titulo));

        // No visible para el público: ni en el catálogo ni en su ficha
        var publico = _app.Cliente();
        Assert.DoesNotContain($"/Eventos/Detalle/{evento.Id}\"", await publico.GetStringAsync($"/Eventos?Buscar={titulo}"));
        Assert.Equal(HttpStatusCode.NotFound, (await publico.GetAsync($"/Eventos/Detalle/{evento.Id}")).StatusCode);

        // Su organizador sí puede revisarlo
        Assert.Equal(HttpStatusCode.OK, (await organizador.GetAsync($"/Eventos/Detalle/{evento.Id}")).StatusCode);
    }

    public static TheoryData<string, string, string, int> DatosInvalidos => new()
    {
        { "Fecha", DateTime.Now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm"), "La fecha del evento debe ser futura", 3 },
        { "Titulo", "", "El título es obligatorio", 3 },
        { "Ubicacion", "", "La dirección es obligatoria", 3 },
        { "Latitud", "", "Marca la ubicación del recinto en el mapa", 3 },
        { "TiposEntrada[0].Capacidad", "0", "La capacidad debe ser al menos 1", 3 },
        { "TiposEntrada[0].Precio", "-5", "El precio debe estar entre 0 y 100 000", 3 },
        { "Titulo", "Válido", "Sube al menos 3 fotos del recinto", 2 }
    };

    [Theory]
    [MemberData(nameof(DatosInvalidos))]
    public async Task Valida_campos_obligatorios_aforo_mayor_que_cero_y_fecha_futura(string campo, string valor, string mensaje, int fotos)
    {
        var (organizador, _) = await OrganizadorAsync();
        var titulo = $"Invalido {Guid.NewGuid():N}";
        var datos = Datos(titulo);
        datos[campo] = campo == "Titulo" && valor == "Válido" ? titulo : valor;

        var r = await PublicarAsync(organizador, datos, fotos);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(mensaje, await TextoAsync(r));
        Assert.Null(await BuscarAsync(titulo));
    }

    // Si el formulario vuelve con un error (p. ej. el precio), las fotos ya elegidas se conservan y no se piden de nuevo
    [Fact]
    public async Task Al_corregir_un_error_no_hay_que_volver_a_subir_las_fotos()
    {
        var (organizador, _) = await OrganizadorAsync();
        var titulo = $"Sin precio {Guid.NewGuid():N}";
        var datos = Datos(titulo);
        datos["TiposEntrada[0].Precio"] = "-5";

        var html = await TextoAsync(await PublicarAsync(organizador, datos));
        Assert.Contains("data-dato=\"fotos-subidas\"", html);
        var rutas = System.Text.RegularExpressions.Regex.Matches(html, "name=\"FotosSubidas\" value=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(3, rutas.Count);

        // Otro organizador no puede usar esas rutas como si fueran suyas
        var (otro, _) = await OrganizadorAsync();
        var ajeno = Datos($"Ajeno {Guid.NewGuid():N}");
        var r = await PublicarConSubidasAsync(otro, ajeno, rutas);
        Assert.Contains("Sube al menos 3 fotos del recinto", await TextoAsync(r));

        // Corrige solo el precio y envía sin elegir fotos otra vez
        datos["TiposEntrada[0].Precio"] = "50";
        r = await PublicarConSubidasAsync(organizador, datos, rutas);

        Assert.Equal("/Eventos/MisEventos", r.Headers.Location!.OriginalString);
        var evento = await BuscarAsync(titulo);
        Assert.Equal(rutas, evento!.Imagenes.OrderBy(i => i.Orden).Select(i => i.Ruta));
    }

    // Editar solo el precio de un evento que ya tiene fotos no pide elegir archivos (ni muestra mensajes en inglés)
    [Fact]
    public async Task Editar_solo_el_precio_no_pide_fotos_de_nuevo()
    {
        var (organizador, _) = await OrganizadorAsync();
        var titulo = $"Gratis {Guid.NewGuid():N}";
        var datos = Datos(titulo);
        datos["TiposEntrada[0].Precio"] = "0";
        await PublicarAsync(organizador, datos);
        var evento = await BuscarAsync(titulo);

        var pagina = await TextoAsync(await organizador.GetAsync($"/Eventos/Editar/{evento!.Id}"));
        Assert.DoesNotMatch("<input[^>]*data-val-required[^>]*name=\"Fotos\"", pagina);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await TokenAsync(organizador, $"/Eventos/Editar/{evento.Id}")), "__RequestVerificationToken");
        datos["TiposEntrada[0].Precio"] = "35";
        datos["TiposEntrada[0].Id"] = evento.TiposEntrada.Single(t => t.Nombre == "General").Id.ToString();
        datos["TiposEntrada[1].Id"] = evento.TiposEntrada.Single(t => t.Nombre == "VIP").Id.ToString();
        foreach (var (clave, valor) in datos)
            form.Add(new StringContent(valor), clave);
        foreach (var imagen in evento.Imagenes)
            form.Add(new StringContent(imagen.Id.ToString()), "ImagenesActualesIds");
        // El navegador envía el campo de archivos vacío
        var vacio = new ByteArrayContent(Array.Empty<byte>());
        vacio.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        vacio.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"Fotos\"", FileName = "\"\"" };
        form.Add(vacio);

        var r = await organizador.PostAsync($"/Eventos/Editar/{evento.Id}", form);

        Assert.True(r.StatusCode == HttpStatusCode.Redirect, await TextoAsync(r));
        evento = await BuscarAsync(titulo);
        Assert.Equal(35m, evento!.TiposEntrada.Single(t => t.Nombre == "General").Precio);
        Assert.Equal(3, evento.Imagenes.Count);
    }

    [Fact]
    public async Task Los_mensajes_de_validacion_automaticos_salen_en_espanol()
    {
        var (organizador, _) = await OrganizadorAsync();
        var datos = Datos($"Idioma {Guid.NewGuid():N}");
        datos["TiposEntrada[0].Capacidad"] = "abc";

        var html = await TextoAsync(await PublicarAsync(organizador, datos));

        Assert.Contains("no es válido", html);
        Assert.DoesNotContain("is invalid", html);
        Assert.DoesNotContain("field is required", html);
    }

    private static async Task<HttpResponseMessage> PublicarConSubidasAsync(HttpClient cliente, Dictionary<string, string> datos, List<string> rutas)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await TokenAsync(cliente, "/Eventos/Crear")), "__RequestVerificationToken");
        foreach (var (clave, valor) in datos)
            form.Add(new StringContent(valor), clave);
        foreach (var ruta in rutas)
            form.Add(new StringContent(ruta), "FotosSubidas");
        return await cliente.PostAsync("/Eventos/Crear", form);
    }

    [Fact]
    public async Task Rechaza_archivos_que_no_son_imagenes()
    {
        var (organizador, _) = await OrganizadorAsync();
        var titulo = $"Falso {Guid.NewGuid():N}";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await TokenAsync(organizador, "/Eventos/Crear")), "__RequestVerificationToken");
        foreach (var (clave, valor) in Datos(titulo))
            form.Add(new StringContent(valor), clave);
        for (var i = 0; i < 3; i++)
            form.Add(new ByteArrayContent("MZ ejecutable disfrazado"u8.ToArray()), "Fotos", $"foto{i}.png");

        var r = await organizador.PostAsync("/Eventos/Crear", form);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(await BuscarAsync(titulo));
    }

    [Fact]
    public async Task Un_usuario_sin_rol_de_organizador_no_puede_publicar()
    {
        var usuario = _app.Cliente();
        await RegistrarAsync(usuario, Correo());

        Assert.StartsWith("/Auth/AccesoDenegado", (await usuario.GetAsync("/Eventos/Crear")).Headers.Location!.PathAndQuery);
    }
}
