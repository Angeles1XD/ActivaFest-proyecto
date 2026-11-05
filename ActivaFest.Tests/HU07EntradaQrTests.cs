using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Email;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// SMTP falso que falla las primeras N veces
public class EnviadorQueFalla : IEnviadorCorreo
{
    private int _fallosPendientes;
    public int Intentos;
    public List<MensajeCorreo> Enviados { get; } = new();

    public EnviadorQueFalla(int fallos) => _fallosPendientes = fallos;

    public Task EnviarAsync(MensajeCorreo mensaje, CancellationToken ct)
    {
        Interlocked.Increment(ref Intentos);
        if (Interlocked.Decrement(ref _fallosPendientes) >= 0)
            throw new IOException("SMTP no disponible");
        lock (Enviados) Enviados.Add(mensaje);
        return Task.CompletedTask;
    }
}

// =============================
// 📬 HU07: LA ENTRADA LLEGA POR CORREO AUNQUE EL SMTP FALLE UN MOMENTO
// =============================
public class ColaCorreosTests
{
    private static readonly TimeSpan[] SinEspera = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
    private static readonly MensajeCorreo Mensaje = new("ana@test.com", "Tus entradas", "<p>QR</p>", new[] { new AdjuntoCorreo("e.png", new byte[] { 1 }) });

    [Fact]
    public async Task Reintenta_si_el_smtp_falla_y_termina_enviando()
    {
        var smtp = new EnviadorQueFalla(fallos: 2);
        var servicio = new EnvioCorreosService(new ColaCorreos(), smtp, NullLogger<EnvioCorreosService>.Instance, SinEspera);

        Assert.True(await servicio.EnviarConReintentosAsync(Mensaje, CancellationToken.None));
        Assert.Equal(3, smtp.Intentos);
        Assert.Single(smtp.Enviados);
    }

    [Fact]
    public async Task Se_rinde_despues_del_ultimo_reintento()
    {
        var smtp = new EnviadorQueFalla(fallos: 99);
        var servicio = new EnvioCorreosService(new ColaCorreos(), smtp, NullLogger<EnvioCorreosService>.Instance, SinEspera);

        Assert.False(await servicio.EnviarConReintentosAsync(Mensaje, CancellationToken.None));
        Assert.Equal(5, smtp.Intentos);
    }

    // Criterio: la entrada llega en menos de 5 minutos aun con todos los reintentos
    [Fact]
    public void Los_reintentos_terminan_antes_de_cinco_minutos()
    {
        var total = EnvioCorreosService.EsperasPorDefecto.Aggregate(TimeSpan.Zero, (a, b) => a + b);

        Assert.True(total < TimeSpan.FromMinutes(4), $"Los reintentos suman {total}");
    }

    [Fact]
    public async Task Lo_encolado_se_envia_en_segundo_plano_sin_bloquear_a_quien_lo_pide()
    {
        var cola = new ColaCorreos();
        var smtp = new EnviadorQueFalla(fallos: 1);
        var servicio = new EnvioCorreosService(cola, smtp, NullLogger<EnvioCorreosService>.Instance, SinEspera);
        await servicio.StartAsync(CancellationToken.None);

        await cola.EnviarAsync("ana@test.com", "Tus entradas", "<p>QR</p>");

        for (var i = 0; i < 100 && smtp.Enviados.Count == 0; i++)
            await Task.Delay(20);
        await servicio.StopAsync(CancellationToken.None);

        Assert.Single(smtp.Enviados);
    }
}

// =============================
// 🎟️ HU07: ENTRADA DIGITAL CON QR (pruebas de integración)
// =============================
public class HU07EntradaQrTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU07EntradaQrTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    [Fact]
    public async Task Tras_pagar_las_entradas_aparecen_en_mis_entradas_y_llegan_por_correo_con_su_qr()
    {
        string titulo;
        int eventoId, zonaId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var evento = new Evento
            {
                Titulo = $"Festival {Guid.NewGuid():N}", Descripcion = "Festival para probar las entradas.", CategoriaId = 3, Ubicacion = "Arequipa",
                Fecha = DateTime.Now.AddDays(10), Estado = EstadoEvento.Aprobado, Aforo = 20,
                TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 25m, Capacidad = 20 } }
            };
            db.Eventos.Add(evento);
            db.SaveChanges();
            (titulo, eventoId, zonaId) = (evento.Titulo, evento.Id, evento.TiposEntrada.First().Id);
        }

        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email);

        var checkout = (await PostAsync(cliente, $"/Eventos/Detalle/{eventoId}", "/Compras/Iniciar",
            ("eventoId", eventoId.ToString()), ($"cantidades[{zonaId}]", "2"))).Headers.Location!.OriginalString;
        var codigo = checkout.Split('=')[1];
        var pasarela = (await PostAsync(cliente, checkout, "/Compras/Pagar", ("codigo", codigo))).Headers.Location!.OriginalString;
        await PostAsync(cliente, pasarela, "/Compras/PasarelaSimulada", ("codigo", codigo), ("aprobar", "true"));

        // Correo con una imagen QR adjunta por entrada y el plano con la ubicación (HU27)
        var asunto = $"Tus entradas para {titulo}";
        Assert.Contains(_app.Correos.Para(email), c => c.Asunto == asunto);
        Assert.Equal(2 + 1, _app.Correos.AdjuntosDe(asunto));

        // "Mis entradas" muestra las 2 entradas y su QR es una imagen PNG
        var html = await cliente.GetStringAsync("/Entradas");
        var qrs = Regex.Matches(html, @"<img src=""(/Entradas/Qr/\d+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(2, qrs.Count);
        var png = await cliente.GetAsync(qrs[0]);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);

        // El QR de una entrada no lo puede ver otro usuario
        var otro = _app.Cliente();
        await RegistrarAsync(otro, Correo());
        Assert.NotEqual(HttpStatusCode.OK, (await otro.GetAsync(qrs[0])).StatusCode);
    }
}
