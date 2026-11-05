using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 💰 HU16: REPORTE DE COMISIONES (pruebas de integración)
// =============================
public class HU16ReporteComisionesTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU16ReporteComisionesTests(AppDePrueba app)
    {
        _app = app;
    }

    // Una venta real (pasarela simulada) para que el reporte tenga filas
    private async Task<string> VentaAsync()
    {
        int eventoId, zonaId;
        string titulo;
        using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(_app.Services))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ActivaFest.Data.ApplicationDbContext>(scope.ServiceProvider);
            var evento = new ActivaFest.Models.Evento
            {
                Titulo = $"Feria {Guid.NewGuid():N}", Descripcion = "Feria para el reporte de comisiones.", CategoriaId = 4, Ubicacion = "Arequipa",
                Fecha = DateTime.Now.AddDays(8), Estado = ActivaFest.Models.EstadoEvento.Aprobado, Aforo = 50,
                TiposEntrada = { new ActivaFest.Models.TipoEntrada { Nombre = "General", Precio = 50m, Capacidad = 50 } }
            };
            db.Eventos.Add(evento);
            db.SaveChanges();
            (eventoId, zonaId, titulo) = (evento.Id, evento.TiposEntrada.First().Id, evento.Titulo);
        }

        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, Correo());
        async Task<HttpResponseMessage> Post(string pagina, string url, params (string, string)[] campos)
        {
            var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
            datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, pagina)));
            return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
        }

        var checkout = (await Post($"/Eventos/Detalle/{eventoId}", "/Compras/Iniciar", ("eventoId", eventoId.ToString()), ($"cantidades[{zonaId}]", "2"))).Headers.Location!.OriginalString;
        var codigo = checkout.Split('=')[1];
        var pasarela = (await Post(checkout, "/Compras/Pagar", ("codigo", codigo))).Headers.Location!.OriginalString;
        await Post(pasarela, "/Compras/PasarelaSimulada", ("codigo", codigo), ("aprobar", "true"));
        return titulo;
    }

    [Fact]
    public async Task El_administrador_ve_el_reporte_por_evento_y_lo_exporta()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");

        var titulo = await VentaAsync(); // 2 entradas x S/ 50 al 8 %

        var html = await TextoAsync(await admin.GetAsync("/Comisiones"));
        Assert.Contains("% comisión", html);
        var fila = html[html.IndexOf(titulo, StringComparison.Ordinal)..];
        Assert.Matches(@"^[\s\S]*?<td class=""derecha num"">2</td>[\s\S]*?S/\s?100\.00[\s\S]*?8 %[\s\S]*?S/\s?8\.00", fila);

        var r = await admin.GetAsync("/Comisiones/Exportar");
        Assert.Equal("text/csv", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", r.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("Entradas vendidas;Ventas brutas;% comisión;Comisión", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Solo_el_administrador_accede_al_reporte()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: ActivaFest.Models.Roles.Organizador);

        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Comisiones")).Headers.Location!.PathAndQuery);
        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Comisiones/Exportar")).Headers.Location!.PathAndQuery);
    }
}
