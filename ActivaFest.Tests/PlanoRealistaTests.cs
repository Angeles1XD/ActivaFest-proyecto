using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Plano;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🪑 PLANO REALISTA (mejora de HU25/HU26): butacas en arco, cancha, abanico y stands
// =============================
public class PlanoRealistaTests
{
    private static List<TipoEntrada> Zonas(params (int Filas, int PorFila)[] zonas) =>
        zonas.Select((z, i) => new TipoEntrada { Id = i + 1, Nombre = $"Zona {i + 1}", Precio = 50, Filas = z.Filas, AsientosPorFila = z.PorFila, Color = PlanoService.ColorPorDefecto(i) }).ToList();

    private static List<AsientoPlano> Plano(List<Asiento> asientos) =>
        asientos.Select((a, i) => new AsientoPlano(i + 1, a.TipoEntradaId, a.Fila, a.Numero, a.PosX, a.PosY, a.Estado)).ToList();

    private static List<ZonaPlano> ZonasPlano(int cantidad) =>
        Enumerable.Range(1, cantidad).Select(i => new ZonaPlano(i, $"Zona {i}", PlanoService.ColorPorDefecto(i - 1), $"Puerta {i}", 40, 100, 100)).ToList();

    [Fact]
    public void Las_filas_del_teatro_forman_un_arco_simetrico_que_mira_al_escenario()
    {
        var fila = PlanoService.GenerarAsientos(Zonas((1, 18))).OrderBy(a => a.Numero).ToList();

        // Los extremos quedan más cerca del escenario (más arriba) que el centro, y a la misma altura entre sí
        Assert.True(fila[0].PosY < fila[8].PosY && fila[17].PosY < fila[9].PosY);
        Assert.Equal(fila[0].PosY, fila[17].PosY);
        Assert.Equal(800, fila[0].PosX + fila[17].PosX);

        // Cada butaca lleva su número y gira hacia el escenario (izquierda en un sentido, derecha en el otro)
        var svg = PlanoService.GenerarSvg(PlantillaPlano.Teatro, ZonasPlano(1), Plano(fila.ToList()));
        Assert.Matches(@"data-numero=""1""[^>]*transform=""translate\(\d+ \d+\) rotate\(\d", svg);
        Assert.Matches(@"data-numero=""18""[^>]*transform=""translate\(\d+ \d+\) rotate\(-\d", svg);
        Assert.Contains("<text y=\"0.5\" class=\"butaca-num\">18</text>", svg);
        foreach (var pieza in new[] { "plano-telon", "plano-tablado", "plano-candileja", "plano-pasillo", "ESCENARIO", "Cabina" })
            Assert.Contains(pieza, svg);
    }

    [Fact]
    public void Las_butacas_no_se_superponen_ni_en_el_teatro_mas_grande()
    {
        var asientos = PlanoService.GenerarAsientos(Zonas((10, 24), (10, 24), (10, 24), (10, 24)));

        Assert.Equal(960, asientos.Count);
        Assert.All(asientos, a => Assert.InRange(a.PosX, 40, 760));
        var minima = double.MaxValue;
        for (var i = 0; i < asientos.Count; i++)
            for (var j = i + 1; j < asientos.Count; j++)
                minima = Math.Min(minima, Math.Sqrt(Math.Pow(asientos[i].PosX - asientos[j].PosX, 2) + Math.Pow(asientos[i].PosY - asientos[j].PosY, 2)));
        Assert.True(minima >= 23, $"Distancia mínima entre butacas: {minima}");
    }

    [Fact]
    public void Entre_zonas_queda_un_pasillo_transversal_con_su_rotulo()
    {
        var asientos = PlanoService.GenerarAsientos(Zonas((2, 10), (2, 10)));
        var centro = asientos.Where(a => a.Numero == 5).OrderBy(a => a.PosY).Select(a => a.PosY).ToList();

        Assert.Equal(26, centro[1] - centro[0]);   // filas de una misma zona
        Assert.Equal(50, centro[2] - centro[1]);   // + pasillo entre zonas
        var svg = PlanoService.GenerarSvg(PlantillaPlano.Teatro, ZonasPlano(2), Plano(asientos));
        Assert.Equal(2, Regex.Matches(svg, "class=\"plano-rotulo-zona\"").Count);
        Assert.Contains("Zona 2 · S/ 40.00", System.Net.WebUtility.HtmlDecode(svg));
    }

    [Fact]
    public void Reubicar_lleva_los_asientos_guardados_a_la_geometria_actual_sin_tocar_su_estado()
    {
        var zonas = Zonas((3, 8));
        var guardados = PlanoService.GenerarAsientos(zonas);
        var esperado = guardados.Select(a => (a.PosX, a.PosY)).ToList();
        guardados.ForEach(a => { a.PosX = 1; a.PosY = 1; });
        guardados[0].Estado = EstadoAsiento.Vendido;

        Assert.Equal(24, PlanoService.Reubicar(zonas, guardados));
        Assert.Equal(esperado, guardados.Select(a => (a.PosX, a.PosY)));
        Assert.Equal(EstadoAsiento.Vendido, guardados[0].Estado);
        Assert.Equal(0, PlanoService.Reubicar(zonas, guardados));   // idempotente
    }

    [Theory]
    [InlineData(PlantillaPlano.Estadio, 4)]
    [InlineData(PlantillaPlano.CampoAbierto, 5)]
    [InlineData(PlantillaPlano.Feria, 3)]
    [InlineData(PlantillaPlano.Coliseo, 4)]
    [InlineData(PlantillaPlano.Discoteca, 4)]
    public void El_rotulo_de_cada_zona_cae_dentro_de_ella(PlantillaPlano plantilla, int maximo)
    {
        for (var total = 1; total <= maximo; total++)
            for (var i = 0; i < total; i++)
                Assert.True(Dentro(PlanoService.CentroZona(plantilla, i, total), PlanoService.PoligonoZona(plantilla, i, total)), $"{plantilla} {i}/{total}");
    }

    private static bool Dentro((double X, double Y) p, IReadOnlyList<(double X, double Y)> poligono)
    {
        var dentro = false;
        for (int i = 0, j = poligono.Count - 1; i < poligono.Count; j = i++)
            if ((poligono[i].Y > p.Y) != (poligono[j].Y > p.Y) &&
                p.X < (poligono[j].X - poligono[i].X) * (p.Y - poligono[i].Y) / (poligono[j].Y - poligono[i].Y) + poligono[i].X)
                dentro = !dentro;
        return dentro;
    }

    [Fact]
    public void El_estadio_tiene_cancha_reglamentaria_y_las_tribunas_sin_zona_se_ven_sin_venta()
    {
        var svg = PlanoService.GenerarSvg(PlantillaPlano.Estadio, ZonasPlano(2));

        Assert.Equal(2, Regex.Matches(svg, "class=\"plano-zona\" data-zona").Count);
        Assert.Equal(2, Regex.Matches(svg, "plano-tribuna-vacia").Count);
        foreach (var pieza in new[] { "plano-cancha-linea", "plano-arco", "plano-gradas", "plano-luz" })
            Assert.Contains(pieza, svg);
    }

    [Fact]
    public void El_campo_abierto_tiene_escenario_pantallas_vallas_y_servicios()
    {
        var svg = System.Net.WebUtility.HtmlDecode(PlanoService.GenerarSvg(PlantillaPlano.CampoAbierto, ZonasPlano(3)));

        Assert.Equal(3, Regex.Matches(svg, "class=\"plano-zona\" data-zona").Count);
        Assert.Equal(3, Regex.Matches(svg, "class=\"plano-valla\"").Count);   // delante del escenario y entre zonas
        foreach (var pieza in new[] { "ESCENARIO", "Pantalla", "plano-parlante", "Torre de sonido", "Baños", "Comida", "Tópico", "af-publico" })
            Assert.Contains(pieza, svg);
    }

    [Fact]
    public void La_feria_tiene_stands_numerados_patio_de_comidas_y_accesos()
    {
        var svg = System.Net.WebUtility.HtmlDecode(PlanoService.GenerarSvg(PlantillaPlano.Feria, ZonasPlano(2)));

        Assert.Equal(15, Regex.Matches(svg, "class=\"plano-stand\"").Count);
        Assert.Contains(">C5</text>", svg);
        Assert.Contains("Patio de comidas", svg);
        Assert.Equal(6, Regex.Matches(svg, "class=\"plano-mesa\"").Count);
        Assert.True(Regex.Matches(svg, "plano-molinete").Count >= 4);
    }

    [Fact]
    public void El_coliseo_tiene_escenario_al_centro_campo_y_anillos_de_tribuna()
    {
        var svg = PlanoService.GenerarSvg(PlantillaPlano.Coliseo, ZonasPlano(3));

        Assert.Equal(3, Regex.Matches(svg, "class=\"plano-zona\" data-zona").Count);
        Assert.Equal(2, Regex.Matches(svg, "<g class=\"plano-gradas\">").Count);       // un anillo de gradas por tribuna
        Assert.Matches("<path d=\"M[^\"]*Z M?[^\"]*A[^\"]*Z\" fill=\"#[0-9a-f]{6}\" fill-rule=\"evenodd\"", svg.Replace("ZM", "Z M"));
        foreach (var pieza in new[] { "plano-ring", "plano-ring-cuerdas", "ESCENARIO", "af-parquet" })
            Assert.Contains(pieza, svg);

        // El anillo exterior siempre cabe en el plano
        var alto = PlanoService.AltoPlano(PlantillaPlano.Coliseo, 4, null);
        var contorno = PlanoService.PoligonoZona(PlantillaPlano.Coliseo, 3, 4);
        Assert.All(contorno, p => { Assert.InRange(p.X, 0, 800); Assert.InRange(p.Y, 0, alto - 50); });
    }

    [Fact]
    public void La_discoteca_tiene_dj_pista_barras_y_las_areas_sin_zona_se_ven_como_lounge()
    {
        var svg = System.Net.WebUtility.HtmlDecode(PlanoService.GenerarSvg(PlantillaPlano.Discoteca, ZonasPlano(2)));

        Assert.Equal(2, Regex.Matches(svg, "class=\"plano-zona\" data-zona").Count);
        Assert.Equal(2, Regex.Matches(svg, "class=\"plano-area-vacia\"").Count);
        foreach (var pieza in new[] { "plano-dj", "plano-plato", "af-pista", "plano-haz", "🍸 Barra", "🛋️ Lounge", "🧥 Guardarropa", "plano-sofa" })
            Assert.Contains(pieza, svg);
    }

    [Theory]
    [InlineData(PlantillaPlano.Coliseo)]
    [InlineData(PlantillaPlano.Discoteca)]
    public void Las_nuevas_plantillas_se_venden_por_zona_sin_asientos_numerados(PlantillaPlano plantilla)
    {
        var zona = PlanoService.AplicarZona(new TipoEntrada { Capacidad = 300 }, new TipoEntradaInput { Filas = 5, AsientosPorFila = 10 }, plantilla, 0);

        Assert.False(zona.AsientosNumerados);
        Assert.Null(zona.Filas);
        Assert.Equal(300, zona.Capacidad);
        Assert.Equal(4, PlanoService.MaxZonas[plantilla]);
    }

    [Fact]
    public void La_puerta_se_ensancha_con_nombres_largos()
    {
        var zonas = new List<ZonaPlano> { new(1, "General", "#6366f1", "Ingreso principal por la avenida", 10, 10, 10) };
        var svg = PlanoService.GenerarSvg(PlantillaPlano.Feria, zonas);
        var ancho = int.Parse(Regex.Match(svg, "<g class=\"plano-puerta\"><rect x=\"-?\\d+\" y=\"\\d+\" width=\"(\\d+)\"").Groups[1].Value);
        Assert.True(ancho > 116);
    }
}

// =============================
// 🗺️ PLANO OFICIAL DEL RECINTO (imagen que sube el organizador)
// =============================
public class PlanoOficialTests : IClassFixture<AppDePrueba>
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
    private readonly AppDePrueba _app;

    public PlanoOficialTests(AppDePrueba app)
    {
        _app = app;
    }

    private T Con<T>(Func<ApplicationDbContext, T> consulta)
    {
        using var scope = _app.Services.CreateScope();
        return consulta(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static async Task<HttpResponseMessage> PublicarAsync(HttpClient organizador, string titulo, (byte[] Datos, string Tipo, string Nombre)? plano)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await TokenAsync(organizador, "/Eventos/Crear")), "__RequestVerificationToken");
        var campos = new Dictionary<string, string>
        {
            ["Titulo"] = titulo, ["Descripcion"] = "Gala en el teatro con plano oficial del recinto.", ["CategoriaId"] = "1",
            ["Fecha"] = DateTime.Now.AddDays(20).ToString("yyyy-MM-ddTHH:mm"), ["Ubicacion"] = "Teatro Municipal",
            ["Latitud"] = "-16.39", ["Longitud"] = "-71.53", ["Plantilla"] = "Teatro",
            ["TiposEntrada[0].Nombre"] = "Platea", ["TiposEntrada[0].Precio"] = "60", ["TiposEntrada[0].Filas"] = "3",
            ["TiposEntrada[0].AsientosPorFila"] = "10", ["TiposEntrada[0].Capacidad"] = "30", ["TiposEntrada[0].Puerta"] = "Puerta 1"
        };
        foreach (var (k, v) in campos)
            form.Add(new StringContent(v), k);
        for (var i = 0; i < 3; i++)
        {
            var foto = new ByteArrayContent(Png);
            foto.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(foto, "Fotos", $"foto{i}.png");
        }
        if (plano is { } p)
        {
            var archivo = new ByteArrayContent(p.Datos);
            archivo.Headers.ContentType = new MediaTypeHeaderValue(p.Tipo);
            form.Add(archivo, "PlanoOficialArchivo", p.Nombre);
        }
        return await organizador.PostAsync("/Eventos/Crear", form);
    }

    private async Task<int> AprobarAsync(string titulo)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = await db.Eventos.SingleAsync(e => e.Titulo == titulo);
        evento.Estado = EstadoEvento.Aprobado;
        await db.SaveChangesAsync();
        return evento.Id;
    }

    [Fact]
    public async Task El_organizador_sube_el_plano_oficial_y_se_muestra_en_la_ficha_junto_al_interactivo()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var titulo = $"Gala {Guid.NewGuid():N}";

        var r = await PublicarAsync(organizador, titulo, (Png, "image/png", "plano-teatro.png"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        var id = await AprobarAsync(titulo);
        var ruta = Con(db => db.Eventos.Single(e => e.Id == id).PlanoOficial);
        Assert.StartsWith("/uploads/planos/", ruta);

        var ficha = await TextoAsync(await _app.Cliente().GetAsync($"/Eventos/Detalle/{id}"));
        Assert.Contains("🗺️ Plano oficial del recinto", ficha);
        Assert.Contains($"<img src=\"{ruta}\"", ficha);
        Assert.Contains("🪑 Plano interactivo", ficha);
        Assert.Equal(30, Regex.Matches(ficha, "<g[^>]*class=\"asiento").Count);   // el interactivo sigue ahí
        Assert.Contains("data-zoom=\"1\"", ficha);
        Assert.Contains("Tu selección", ficha);
    }

    [Fact]
    public async Task Sin_plano_oficial_no_aparecen_las_pestanas()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var titulo = $"Gala {Guid.NewGuid():N}";
        await PublicarAsync(organizador, titulo, null);
        var id = await AprobarAsync(titulo);

        var ficha = await TextoAsync(await _app.Cliente().GetAsync($"/Eventos/Detalle/{id}"));
        Assert.DoesNotContain("plano-pestanas", ficha);
        Assert.Null(Con(db => db.Eventos.Single(e => e.Id == id).PlanoOficial));
    }

    [Fact]
    public async Task Un_archivo_que_no_es_imagen_se_rechaza()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var titulo = $"Gala {Guid.NewGuid():N}";

        var r = await PublicarAsync(organizador, titulo, ("no es una imagen"u8.ToArray(), "application/pdf", "plano.pdf"));

        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);   // vuelve al formulario con el error
        Assert.False(Con(db => db.Eventos.Any(e => e.Titulo == titulo)));
    }

    [Fact]
    public async Task Al_editar_se_puede_reemplazar_o_quitar_el_plano_oficial()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var titulo = $"Gala {Guid.NewGuid():N}";
        await PublicarAsync(organizador, titulo, (Png, "image/png", "v1.png"));
        var id = Con(db => db.Eventos.Single(e => e.Titulo == titulo).Id);
        var original = Con(db => db.Eventos.Single(e => e.Id == id).PlanoOficial);

        var datos = Formulario(id);
        datos.PlanoOficialArchivo = new FormFile(new MemoryStream(Png), 0, Png.Length, "PlanoOficialArchivo", "v2.png")
            { Headers = new HeaderDictionary(), ContentType = "image/png" };
        Assert.True((await EditarAsync(datos)).Exito);
        var reemplazo = Con(db => db.Eventos.Single(e => e.Id == id).PlanoOficial);
        Assert.StartsWith("/uploads/planos/", reemplazo);
        Assert.NotEqual(original, reemplazo);

        datos = Formulario(id);
        datos.QuitarPlanoOficial = true;
        Assert.True((await EditarAsync(datos)).Exito);
        Assert.Null(Con(db => db.Eventos.Single(e => e.Id == id).PlanoOficial));
    }

    private EventoFormViewModel Formulario(int eventoId) => Con(db =>
    {
        var e = db.Eventos.Include(x => x.TiposEntrada).Include(x => x.Imagenes).Single(x => x.Id == eventoId);
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
    });

    private async Task<ActivaFest.Services.Eventos.ResultadoEdicion> EditarAsync(EventoFormViewModel datos)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ActivaFest.Services.Eventos.IEventoEdicionService>()
            .ActualizarAsync(datos.Id!.Value, datos, "https://x/Eventos/Detalle");
    }
}
