using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🛒 HU06: COMPRAR ENTRADAS Y PAGAR EN LÍNEA (pruebas de integración, pasarela simulada)
// =============================
public class HU06CompraTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU06CompraTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<(int EventoId, int ZonaId)> EventoAsync(int capacidad = 50)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = new Evento
        {
            Titulo = $"Concierto {Guid.NewGuid():N}", Descripcion = "Concierto de prueba para compras.", CategoriaId = 1, Ubicacion = "Arequipa",
            Fecha = DateTime.Now.AddDays(15), Estado = EstadoEvento.Aprobado, Aforo = capacidad,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 40m, Capacidad = capacidad } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        return (evento.Id, evento.TiposEntrada.First().Id);
    }

    private TipoEntrada Zona(int id)
    {
        using var scope = _app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TiposEntrada.AsNoTracking().First(t => t.Id == id);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    // Paso 1 del usuario: en la ficha elige la cantidad y pulsa "Comprar entradas"
    private static Task<HttpResponseMessage> IniciarAsync(HttpClient cliente, int eventoId, int zonaId, int cantidad) =>
        PostAsync(cliente, $"/Eventos/Detalle/{eventoId}", "/Compras/Iniciar", ("eventoId", eventoId.ToString()), ($"cantidades[{zonaId}]", cantidad.ToString()));

    [Fact]
    public async Task La_compra_se_completa_en_menos_de_cinco_pasos_y_emite_las_entradas()
    {
        var (eventoId, zonaId) = await EventoAsync();
        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, Correo());
        var pasos = 0;

        // 1) Ficha: elige 2 entradas
        var r = await IniciarAsync(cliente, eventoId, zonaId, 2); pasos++;
        var checkout = r.Headers.Location!.OriginalString;
        Assert.StartsWith("/Compras/Checkout", checkout);

        // 2) Resumen: "Pagar"
        var codigo = checkout.Split('=')[1];
        r = await PostAsync(cliente, checkout, "/Compras/Pagar", ("codigo", codigo)); pasos++;
        var pasarela = r.Headers.Location!.OriginalString;
        Assert.StartsWith("/Compras/PasarelaSimulada", pasarela);

        // 3) Pasarela: aprueba el pago
        r = await PostAsync(cliente, pasarela, "/Compras/PasarelaSimulada", ("codigo", codigo), ("aprobar", "true")); pasos++;
        var confirmacion = r.Headers.Location!.OriginalString;
        Assert.StartsWith("/Compras/Confirmacion", confirmacion);

        // 4) Confirmación con las entradas
        var html = await TextoAsync(await cliente.GetAsync(confirmacion)); pasos++;
        Assert.Contains("¡Compra confirmada!", html);
        // El botón "Ver mi ubicación en el plano" de cada entrada necesita su script también en la confirmación
        Assert.Contains("entrada-plano", html);
        Assert.Contains("/js/entradas.js", html);
        Assert.Contains("Tus 2 entradas", html);

        Assert.True(pasos <= 5, $"La compra tomó {pasos} pasos (RNF01: máximo 5)");
        Assert.Equal(2, Zona(zonaId).Vendidas);
        Assert.Equal(0, Zona(zonaId).Reservadas);
    }

    [Fact]
    public async Task Solo_un_usuario_con_sesion_iniciada_puede_comprar()
    {
        var (eventoId, zonaId) = await EventoAsync();
        var anonimo = _app.Cliente();

        var r = await IniciarAsync(anonimo, eventoId, zonaId, 1);

        Assert.StartsWith("/Auth/Login", r.Headers.Location!.PathAndQuery);
        Assert.Equal(0, Zona(zonaId).Reservadas);
        Assert.Contains("Inicia sesión para comprar", await TextoAsync(await anonimo.GetAsync($"/Eventos/Detalle/{eventoId}")));
    }

    [Fact]
    public async Task No_se_compran_mas_entradas_que_el_aforo_disponible()
    {
        var (eventoId, zonaId) = await EventoAsync(capacidad: 3);
        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, Correo());

        var r = await IniciarAsync(cliente, eventoId, zonaId, 4);

        Assert.DoesNotContain("/Compras/Checkout", r.Headers.Location?.OriginalString ?? "");
        Assert.Equal(0, Zona(zonaId).Reservadas);
        Assert.Equal(0, Zona(zonaId).Vendidas);
    }

    [Fact]
    public async Task Un_pago_rechazado_no_descuenta_aforo_y_devuelve_al_usuario_a_la_ficha()
    {
        var (eventoId, zonaId) = await EventoAsync(capacidad: 10);
        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, Correo());

        var checkout = (await IniciarAsync(cliente, eventoId, zonaId, 4)).Headers.Location!.OriginalString;
        var codigo = checkout.Split('=')[1];
        Assert.Equal(6, Zona(zonaId).Disponibles);
        var pasarela = (await PostAsync(cliente, checkout, "/Compras/Pagar", ("codigo", codigo))).Headers.Location!.OriginalString;

        var r = await PostAsync(cliente, pasarela, "/Compras/PasarelaSimulada", ("codigo", codigo), ("aprobar", "false"));

        Assert.Equal($"/Eventos/Detalle/{eventoId}#comprar", r.Headers.Location!.OriginalString);
        Assert.Equal(10, Zona(zonaId).Disponibles);
        Assert.Equal(0, Zona(zonaId).Vendidas);
        Assert.Contains("El pago fue rechazado y no se te cobró nada", await TextoAsync(await cliente.GetAsync(r.Headers.Location!.OriginalString)));
    }
}
