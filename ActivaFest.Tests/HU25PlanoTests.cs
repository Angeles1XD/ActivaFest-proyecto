using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Plano;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🗺️ HU25: PLANO DEL RECINTO DESDE PLANTILLAS (pruebas unitarias)
// =============================
public class PlanoServiceTests
{
    private static List<TipoEntrada> Teatro(params (string Nombre, int Filas, int PorFila)[] zonas) =>
        zonas.Select((z, i) => new TipoEntrada { Id = i + 1, Nombre = z.Nombre, Filas = z.Filas, AsientosPorFila = z.PorFila, AsientosNumerados = true }).ToList();

    // Criterio: en la plantilla de teatro cada asiento tiene zona, fila y número únicos
    [Fact]
    public void En_teatro_cada_asiento_tiene_zona_fila_y_numero_unicos()
    {
        var asientos = PlanoService.GenerarAsientos(Teatro(("Platea A", 5, 18), ("Platea B", 5, 18), ("Mezzanine", 2, 18)));

        Assert.Equal(12 * 18, asientos.Count);
        Assert.Equal(asientos.Count, asientos.Select(a => (a.TipoEntradaId, a.Fila, a.Numero)).Distinct().Count());
        // Las letras continúan entre zonas: fila + número es único en todo el teatro (como en el prototipo F-12)
        Assert.Equal(asientos.Count, asientos.Select(a => a.Etiqueta).Distinct().Count());
        Assert.Equal("ABCDEFGHIJKL", string.Concat(asientos.Select(a => a.Fila).Distinct()));
        Assert.All(asientos.Where(a => a.Fila == "F"), a => Assert.Equal(2, a.TipoEntradaId)); // la fila F es de Platea B
        Assert.Equal(Enumerable.Range(1, 18), asientos.Where(a => a.Fila == "A").Select(a => a.Numero));
    }

    [Fact]
    public void Los_asientos_quedan_dentro_del_plano_con_pasillo_central()
    {
        var asientos = PlanoService.GenerarAsientos(Teatro(("Platea", 3, PlanoService.MaxAsientosPorFila)));
        var fila = asientos.Where(a => a.Fila == "A").OrderBy(a => a.Numero).ToList();

        Assert.All(asientos, a => Assert.InRange(a.PosX, 40, 760));
        var saltos = fila.Zip(fila.Skip(1), (a, b) => b.PosX - a.PosX).ToList();
        Assert.Single(saltos, s => s > 26); // un único hueco: el pasillo
        Assert.True(fila[11].PosX < 400 && fila[12].PosX > 400);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    public void Nombra_las_filas_con_letras(int indice, string esperado) => Assert.Equal(esperado, PlanoService.LetraFila(indice));

    // Criterio: el plano se genera desde cualquiera de las 4 plantillas sin dibujar a mano
    [Theory]
    [InlineData(PlantillaPlano.Teatro, "asiento")]
    [InlineData(PlantillaPlano.Estadio, "plano-cancha")]
    [InlineData(PlantillaPlano.CampoAbierto, "plano-escenario")]
    [InlineData(PlantillaPlano.Feria, "plano-stand")]
    public void Genera_el_plano_svg_de_cada_plantilla(PlantillaPlano plantilla, string elemento)
    {
        var datos = new List<TipoEntradaInput>
        {
            new() { Nombre = "VIP", Precio = 120, Capacidad = 40, Filas = 2, AsientosPorFila = 20, Puerta = "Puerta 1" },
            new() { Nombre = "General", Precio = 50, Capacidad = 60, Filas = 3, AsientosPorFila = 20, Puerta = "Puerta 2" }
        };

        var svg = PlanoService.SvgVistaPrevia(plantilla, datos);

        Assert.StartsWith("<svg", svg);
        Assert.Contains(elemento, svg);
        Assert.Contains("VIP", svg);
        Assert.Contains("🚪 Puerta 1", svg);
        Assert.Contains("🚪 Puerta 2", svg);
        if (plantilla == PlantillaPlano.Teatro)
            Assert.Equal(100, Regex.Matches(svg, "<g[^>]*class=\"asiento").Count);
    }

    [Fact]
    public void El_svg_no_permite_inyectar_codigo_ni_estilos()
    {
        var datos = new List<TipoEntradaInput>
        {
            new() { Nombre = "<script>alert(1)</script>", Precio = 10, Capacidad = 10, Color = "red\" onload=\"x", Puerta = "<b>P</b>" }
        };

        var svg = PlanoService.SvgVistaPrevia(PlantillaPlano.CampoAbierto, datos);

        Assert.DoesNotContain("<script>", svg);
        Assert.DoesNotContain("onload", svg);
        Assert.DoesNotContain("<b>", svg);
        Assert.Contains(PlanoService.Paleta[0], svg); // un color inválido usa la paleta
    }

    // Formulario: cada plantilla tiene su máximo de zonas y en teatro la capacidad es filas x asientos
    [Theory]
    [InlineData(PlantillaPlano.Estadio, 5, "admite como máximo 4 zonas")]
    [InlineData(PlantillaPlano.Feria, 4, "admite como máximo 3 zonas")]
    public void Limita_las_zonas_segun_la_plantilla(PlantillaPlano plantilla, int zonas, string mensaje)
    {
        var modelo = Formulario(plantilla, Enumerable.Range(1, zonas).Select(i => new TipoEntradaInput { Nombre = $"Z{i}", Capacidad = 10 }).ToArray());

        Assert.Contains(Errores(modelo), e => e.Contains(mensaje));
    }

    [Fact]
    public void En_teatro_el_aforo_de_la_zona_debe_ser_filas_por_asientos()
    {
        var mal = Formulario(PlantillaPlano.Teatro, new TipoEntradaInput { Nombre = "Platea", Filas = 4, AsientosPorFila = 10, Capacidad = 50 });
        var sinFilas = Formulario(PlantillaPlano.Teatro, new TipoEntradaInput { Nombre = "Platea", Capacidad = 50 });
        var bien = Formulario(PlantillaPlano.Teatro, new TipoEntradaInput { Nombre = "Platea", Filas = 4, AsientosPorFila = 10, Capacidad = 40 });

        Assert.Contains(Errores(mal), e => e.Contains("filas × asientos por fila (40)"));
        Assert.Contains(Errores(sinFilas), e => e.Contains("indica entre 1 y"));
        Assert.DoesNotContain(Errores(bien), e => e.Contains("Platea"));
    }

    private static EventoFormViewModel Formulario(PlantillaPlano plantilla, params TipoEntradaInput[] zonas) =>
        new() { Plantilla = plantilla, TiposEntrada = zonas.ToList(), Fecha = DateTime.Now.AddDays(5) };

    private static List<string> Errores(EventoFormViewModel m) =>
        m.Validate(new ValidationContext(m)).Select(r => r.ErrorMessage!).ToList();
}

// =============================
// 🗺️ HU25: PUBLICAR UN TEATRO Y VER SU PLANO (pruebas de integración)
// =============================
public class HU25PlanoTests : IClassFixture<AppDePrueba>
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private readonly AppDePrueba _app;

    public HU25PlanoTests(AppDePrueba app)
    {
        _app = app;
    }

    private T Con<T>(Func<ApplicationDbContext, T> consulta)
    {
        using var scope = _app.Services.CreateScope();
        return consulta(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static async Task<HttpResponseMessage> PublicarTeatroAsync(HttpClient organizador, string titulo)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await TokenAsync(organizador, "/Eventos/Crear")), "__RequestVerificationToken");
        var campos = new Dictionary<string, string>
        {
            ["Titulo"] = titulo, ["Descripcion"] = "Concierto sinfónico de primavera en el teatro municipal.", ["CategoriaId"] = "1",
            ["Fecha"] = DateTime.Now.AddDays(20).ToString("yyyy-MM-ddTHH:mm"), ["Ubicacion"] = "Teatro Municipal de Arequipa",
            ["Latitud"] = "-16.39", ["Longitud"] = "-71.53", ["Plantilla"] = "Teatro",
            ["TiposEntrada[0].Nombre"] = "Platea A", ["TiposEntrada[0].Precio"] = "120", ["TiposEntrada[0].Filas"] = "5",
            ["TiposEntrada[0].AsientosPorFila"] = "18", ["TiposEntrada[0].Capacidad"] = "90", ["TiposEntrada[0].Puerta"] = "Puerta 1",
            ["TiposEntrada[1].Nombre"] = "Platea B", ["TiposEntrada[1].Precio"] = "80", ["TiposEntrada[1].Filas"] = "5",
            ["TiposEntrada[1].AsientosPorFila"] = "18", ["TiposEntrada[1].Capacidad"] = "90", ["TiposEntrada[1].Puerta"] = "Puerta 2",
            ["TiposEntrada[2].Nombre"] = "Mezzanine", ["TiposEntrada[2].Precio"] = "50", ["TiposEntrada[2].Filas"] = "2",
            ["TiposEntrada[2].AsientosPorFila"] = "18", ["TiposEntrada[2].Capacidad"] = "36", ["TiposEntrada[2].Puerta"] = "Puerta 2"
        };
        foreach (var (k, v) in campos)
            form.Add(new StringContent(v), k);
        for (var i = 0; i < 3; i++)
        {
            var foto = new ByteArrayContent(Png);
            foto.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(foto, "Fotos", $"teatro{i}.png");
        }
        return await organizador.PostAsync("/Eventos/Crear", form);
    }

    [Fact]
    public async Task Publicar_un_teatro_genera_sus_asientos_y_el_aforo_es_la_suma_de_las_zonas()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var titulo = $"Sinfónico {Guid.NewGuid():N}";

        var r = await PublicarTeatroAsync(organizador, titulo);
        Assert.Equal("/Eventos/MisEventos", r.Headers.Location!.OriginalString);

        var evento = Con(db => db.Eventos.Include(e => e.TiposEntrada).ThenInclude(t => t.Asientos).Single(e => e.Titulo == titulo));
        Assert.Equal(PlantillaPlano.Teatro, evento.PlantillaPlano);
        Assert.Equal(216, evento.Aforo);
        Assert.Equal(evento.TiposEntrada.Sum(t => t.Capacidad), evento.Aforo);              // criterio: suma de zonas = aforo
        Assert.All(evento.TiposEntrada, t => Assert.Equal(t.Capacidad, t.Asientos.Count)); // un asiento por lugar
        Assert.Equal(new[] { "Puerta 1", "Puerta 2", "Puerta 2" }, evento.TiposEntrada.OrderBy(t => t.Id).Select(t => t.PuertaIngreso));

        // La ficha muestra el plano con todos los asientos
        var ficha = await organizador.GetStringAsync($"/Eventos/Detalle/{evento.Id}");
        Assert.Equal(216, Regex.Matches(ficha, "<g[^>]*class=\"asiento").Count);
        Assert.Contains("Plano del recinto", ficha);
    }

    [Fact]
    public async Task La_base_de_datos_no_admite_dos_asientos_iguales_en_una_zona()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var titulo = $"Sinfónico {Guid.NewGuid():N}";
        await PublicarTeatroAsync(organizador, titulo);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var existente = db.Asientos.First(a => a.TipoEntrada!.Evento!.Titulo == titulo);
        db.Asientos.Add(new Asiento { TipoEntradaId = existente.TipoEntradaId, Fila = existente.Fila, Numero = existente.Numero });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private async Task<EventoFormViewModel> FormularioDeAsync(int eventoId)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var e = await db.Eventos.Include(x => x.TiposEntrada).Include(x => x.Imagenes).SingleAsync(x => x.Id == eventoId);
        return new EventoFormViewModel
        {
            Id = e.Id, Titulo = e.Titulo, Descripcion = e.Descripcion, CategoriaId = e.CategoriaId, Fecha = e.Fecha, Ubicacion = e.Ubicacion,
            Latitud = e.Latitud, Longitud = e.Longitud, Plantilla = e.PlantillaPlano,
            ImagenesActualesIds = e.Imagenes.Select(i => i.Id).ToList(),
            TiposEntrada = e.TiposEntrada.OrderBy(t => t.Id).Select(t => new TipoEntradaInput
            {
                Id = t.Id, Nombre = t.Nombre, Precio = t.Precio, Capacidad = t.Capacidad, Filas = t.Filas, AsientosPorFila = t.AsientosPorFila,
                Puerta = t.PuertaIngreso, Color = t.Color
            }).ToList()
        };
    }

    private async Task<ActivaFest.Services.Eventos.ResultadoEdicion> EditarAsync(EventoFormViewModel datos)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ActivaFest.Services.Eventos.IEventoEdicionService>()
            .ActualizarAsync(datos.Id!.Value, datos, "https://x/Eventos/Detalle");
    }

    [Fact]
    public async Task Al_editar_la_distribucion_sin_ventas_el_plano_se_regenera()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var titulo = $"Sinfónico {Guid.NewGuid():N}";
        await PublicarTeatroAsync(organizador, titulo);
        var id = Con(db => db.Eventos.Single(e => e.Titulo == titulo).Id);

        var datos = await FormularioDeAsync(id);
        datos.TiposEntrada[2].Filas = 4;            // Mezzanine: de 2 a 4 filas
        datos.TiposEntrada[2].Capacidad = 4 * 18;
        var r = await EditarAsync(datos);

        Assert.True(r.Exito, string.Join(" ", r.Errores));
        Assert.Equal(252, Con(db => db.Asientos.Count(a => a.TipoEntrada!.EventoId == id)));
        Assert.Equal(252, Con(db => db.Eventos.Single(e => e.Id == id).Aforo));
        Assert.Equal("N", Con(db => db.Asientos.Where(a => a.TipoEntrada!.EventoId == id).Max(a => a.Fila)));
    }

    [Fact]
    public async Task Con_ventas_no_se_puede_cambiar_la_plantilla_ni_la_distribucion()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var titulo = $"Sinfónico {Guid.NewGuid():N}";
        await PublicarTeatroAsync(organizador, titulo);
        var id = Con(db => db.Eventos.Single(e => e.Titulo == titulo).Id);

        // Una compra en la primera zona
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Eventos.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Estado, EstadoEvento.Aprobado));
            var comprador = db.Users.First();
            var zona = db.TiposEntrada.Where(t => t.EventoId == id).OrderBy(t => t.Id).First();
            var compras = scope.ServiceProvider.GetRequiredService<ActivaFest.Services.Compras.ICompraService>();
            var asiento = db.Asientos.First(a => a.TipoEntradaId == zona.Id);
            var compra = await compras.CrearOrdenConAsientosAsync(comprador.Id, id, new[] { asiento.Id }); // HU26: en teatro se compra por asiento
            Assert.Null(compra.Error);
        }

        var cambiaFilas = await FormularioDeAsync(id);
        cambiaFilas.TiposEntrada[0].Filas = 6;
        cambiaFilas.TiposEntrada[0].Capacidad = 6 * 18;
        var cambiaPlantilla = await FormularioDeAsync(id);
        cambiaPlantilla.Plantilla = PlantillaPlano.CampoAbierto;

        Assert.False((await EditarAsync(cambiaFilas)).Exito);
        Assert.False((await EditarAsync(cambiaPlantilla)).Exito);
        Assert.Equal(216, Con(db => db.Asientos.Count(a => a.TipoEntrada!.EventoId == id)));

        // Cambiar el precio o la puerta sí se puede
        var soloPrecio = await FormularioDeAsync(id);
        soloPrecio.TiposEntrada[0].Precio = 150;
        soloPrecio.TiposEntrada[0].Puerta = "Puerta 3";
        Assert.True((await EditarAsync(soloPrecio)).Exito);
        Assert.Equal("Puerta 3", Con(db => db.TiposEntrada.Where(t => t.EventoId == id).OrderBy(t => t.Id).First().PuertaIngreso));
    }

    [Fact]
    public async Task La_vista_previa_devuelve_el_plano_solo_al_organizador()
    {
        var url = "/Eventos/VistaPreviaPlano?plantilla=Estadio&TiposEntrada[0].Nombre=Occidente&TiposEntrada[0].Precio=80&TiposEntrada[0].Capacidad=500";

        var anonimo = await _app.Cliente().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, anonimo.StatusCode);

        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var r = await organizador.GetAsync(url);
        Assert.Equal("image/svg+xml", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Occidente", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Los_eventos_existentes_usan_la_plantilla_de_campo_abierto()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = new Evento
        {
            Titulo = $"Antiguo {Guid.NewGuid():N}", Descripcion = "Evento creado antes del plano.", CategoriaId = 1, Ubicacion = "Lima",
            Fecha = DateTime.Now.AddDays(4), Estado = EstadoEvento.Aprobado, Aforo = 50,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 20, Capacidad = 50 } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();

        var ficha = await _app.Cliente().GetStringAsync($"/Eventos/Detalle/{evento.Id}");

        Assert.Equal(PlantillaPlano.CampoAbierto, evento.PlantillaPlano);
        Assert.Contains("plano-campoabierto", ficha);
        Assert.Contains("data-zona=", ficha);
    }
}
