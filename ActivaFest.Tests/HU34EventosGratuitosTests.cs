using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Eventos;
using ActivaFest.Services.Organizadores;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🎉 HU34: EVENTOS GRATUITOS (TARIFA DE PUBLICACIÓN, SIN VENTA DE ENTRADAS Y «ME INTERESA»)
// =============================
public class HU34EventosGratuitosTests : IClassFixture<AppDePrueba>
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private readonly AppDePrueba _app;

    public HU34EventosGratuitosTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<T> ConAsync<T>(Func<IServiceProvider, ApplicationDbContext, Task<T>> accion)
    {
        using var scope = _app.Services.CreateScope();
        return await accion(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    private async Task<(HttpClient Cliente, string Email)> OrganizadorAsync()
    {
        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email, rol: Roles.Organizador);
        return (cliente, email);
    }

    // Publica un evento con todas las zonas a precio 0 (o con precio) por el formulario real
    private static async Task<HttpResponseMessage> PublicarAsync(HttpClient cliente, string titulo, int capacidad, decimal precio = 0)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await TokenAsync(cliente, "/Eventos/Crear")), "__RequestVerificationToken");
        foreach (var (clave, valor) in new Dictionary<string, string>
                 {
                     ["Titulo"] = titulo, ["Descripcion"] = "Feria cultural con música en vivo y talleres para toda la familia.",
                     ["CategoriaId"] = "1", ["Fecha"] = DateTime.Now.AddDays(12).ToString("yyyy-MM-ddTHH:mm"),
                     ["Ubicacion"] = "Plaza de Armas, Arequipa", ["Latitud"] = "-16.39", ["Longitud"] = "-71.53",
                     ["TiposEntrada[0].Nombre"] = "General", ["TiposEntrada[0].Precio"] = precio.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     ["TiposEntrada[0].Capacidad"] = capacidad.ToString()
                 })
            form.Add(new StringContent(valor), clave);
        for (var i = 0; i < 3; i++)
        {
            var foto = new ByteArrayContent(Png);
            foto.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(foto, "Fotos", $"recinto{i}.png");
        }
        return await cliente.PostAsync("/Eventos/Crear", form);
    }

    private Task<Evento> EventoAsync(string titulo) =>
        ConAsync((_, db) => db.Eventos.AsNoTracking().Include(e => e.TiposEntrada).SingleAsync(e => e.Titulo == titulo));

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        return admin;
    }

    private Task Aprobar(HttpClient admin, int id) => PostAsync(admin, "/Admin", $"/Admin/Aprobar/{id}");

    [Theory]
    [InlineData(1, 0)]
    [InlineData(100, 0)]
    [InlineData(101, 29)]
    [InlineData(500, 29)]
    [InlineData(501, 59)]
    [InlineData(20000, 59)]
    public async Task La_tarifa_de_publicacion_depende_del_aforo(int aforo, decimal tarifa) =>
        Assert.Equal(tarifa, await ConAsync((sp, _) => Task.FromResult(sp.GetRequiredService<IEventoGratuitoService>().TarifaPara(aforo))));

    [Fact]
    public async Task Un_evento_gratuito_grande_se_revisa_despues_de_pagar_su_publicacion()
    {
        var (organizador, email) = await OrganizadorAsync();
        var titulo = $"Feria libre {Guid.NewGuid():N}"[..24];

        var r = await PublicarAsync(organizador, titulo, 300);
        var evento = await EventoAsync(titulo);
        Assert.Equal($"/Publicacion/Pagar/{evento.Id}", r.Headers.Location!.OriginalString);
        Assert.True(evento.EsGratuito);
        Assert.True(evento.PendientePagoPublicacion);
        Assert.Contains(_app.Correos.Para(email), c => c.Asunto.StartsWith("Falta pagar la publicación"));

        // El administrador no puede aprobarlo mientras no se pague
        var admin = await AdminAsync();
        Assert.Contains("data-dato=\"esperando-pago\"", await TextoAsync(await admin.GetAsync("/Admin")));
        await Aprobar(admin, evento.Id);
        Assert.Equal(EstadoEvento.Pendiente, (await EventoAsync(titulo)).Estado);

        // Paga S/ 29 en la pasarela simulada
        var pagina = $"/Publicacion/Pagar/{evento.Id}";
        Assert.Contains("S/ 29.00", await TextoAsync(await organizador.GetAsync(pagina)));
        var pasarela = (await PostAsync(organizador, pagina, pagina)).Headers.Location!.OriginalString;
        Assert.StartsWith("/Publicacion/PasarelaSimulada", pasarela);
        await PostAsync(organizador, pasarela, pasarela, ("aprobar", "true"));

        Assert.False((await EventoAsync(titulo)).PendientePagoPublicacion);
        Assert.True(await ConAsync((_, db) => db.PagosPublicacion.AnyAsync(p => p.EventoId == evento.Id && p.Estado == EstadoPagoPublicacion.Pagado && p.Monto == 29m)));
        Assert.Contains(_app.Correos.Para(email), c => c.Asunto.StartsWith("Publicación pagada"));

        await Aprobar(admin, evento.Id);
        Assert.Equal(EstadoEvento.Aprobado, (await EventoAsync(titulo)).Estado);

        // La ficha muestra «Entrada libre» y no permite comprar
        var ficha = await TextoAsync(await _app.Cliente().GetAsync($"/Eventos/Detalle/{evento.Id}"));
        Assert.Contains("data-dato=\"entrada-libre\"", ficha);
        Assert.DoesNotContain("/Compras/Iniciar", ficha);
        var compra = await ConAsync((sp, _) => sp.GetRequiredService<ICompraService>()
            .CrearOrdenAsync("cualquiera", evento.Id, new Dictionary<int, int> { [evento.TiposEntrada.First().Id] = 1 }));
        Assert.Contains("entrada libre", compra.Error);

        // El dashboard del administrador cuenta el evento y la tarifa cobrada
        var dashboard = await TextoAsync(await admin.GetAsync($"/Analitica?rango=personalizado&desde={DateTime.Today:yyyy-MM-dd}&hasta={DateTime.Today.AddDays(30):yyyy-MM-dd}"));
        Assert.Contains("data-dato=\"dashboard-gratuitos\"", dashboard);
        Assert.Contains(titulo, dashboard);
    }

    [Fact]
    public async Task Un_evento_gratuito_pequeno_no_paga_publicacion()
    {
        var (organizador, _) = await OrganizadorAsync();
        var titulo = $"Taller libre {Guid.NewGuid():N}"[..24];

        var r = await PublicarAsync(organizador, titulo, 80);

        Assert.Equal("/Eventos/MisEventos", r.Headers.Location!.OriginalString);
        var evento = await EventoAsync(titulo);
        Assert.True(evento.EsGratuito);
        Assert.False(evento.PendientePagoPublicacion);
    }

    [Fact]
    public async Task El_publico_marca_me_interesa_y_el_organizador_ve_el_alcance()
    {
        var (organizador, _) = await OrganizadorAsync();
        var titulo = $"Concierto libre {Guid.NewGuid():N}"[..26];
        await PublicarAsync(organizador, titulo, 50);
        var evento = await EventoAsync(titulo);
        await Aprobar(await AdminAsync(), evento.Id);
        var ficha = $"/Eventos/Detalle/{evento.Id}";

        // Sin sesión se pide iniciar sesión
        var anonimo = _app.Cliente();
        Assert.Contains("/Auth/Login", await TextoAsync(await anonimo.GetAsync(ficha)));

        var asistente = _app.Cliente();
        await RegistrarAsync(asistente, Correo());
        await PostAsync(asistente, ficha, $"/Eventos/MeInteresa/{evento.Id}");
        Assert.Contains("✓ Te interesa", await TextoAsync(await asistente.GetAsync(ficha)));
        Assert.Equal(1, await ConAsync((_, db) => db.InteresesEvento.CountAsync(i => i.EventoId == evento.Id)));

        // El panel del organizador muestra visitas y «Me interesa» en lugar de ventas
        var panel = await TextoAsync(await organizador.GetAsync($"/Panel/Evento/{evento.Id}"));
        Assert.Contains("data-dato=\"panel-gratuito\"", panel);

        // Volver a pulsar quita el interés
        await PostAsync(asistente, ficha, $"/Eventos/MeInteresa/{evento.Id}");
        Assert.Equal(0, await ConAsync((_, db) => db.InteresesEvento.CountAsync(i => i.EventoId == evento.Id)));

        // La página «Organiza tu evento» muestra el alcance de los eventos gratuitos
        Assert.Contains("data-dato=\"alcance-gratuitos\"", await TextoAsync(await anonimo.GetAsync("/Organizador/Solicitar")));
    }

    [Fact]
    public async Task Me_interesa_solo_existe_en_eventos_gratuitos()
    {
        var (organizador, _) = await OrganizadorAsync();
        var titulo = $"Concierto pago {Guid.NewGuid():N}"[..26];
        await PublicarAsync(organizador, titulo, 50, precio: 40);
        var evento = await EventoAsync(titulo);
        Assert.False(evento.EsGratuito);
        await Aprobar(await AdminAsync(), evento.Id);

        var asistente = _app.Cliente();
        await RegistrarAsync(asistente, Correo());
        var r = await PostAsync(asistente, $"/Eventos/Detalle/{evento.Id}", $"/Eventos/MeInteresa/{evento.Id}");

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Un_evento_gratuito_no_pide_personal_de_control_ni_cambia_a_pago()
    {
        var (organizador, email) = await OrganizadorAsync();
        var titulo = $"Feria sin control {Guid.NewGuid():N}"[..28];
        await PublicarAsync(organizador, titulo, 60);
        var evento = await EventoAsync(titulo);
        await Aprobar(await AdminAsync(), evento.Id);
        var orgId = await ConAsync(async (sp, _) => (await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id);

        var r = await ConAsync((sp, _) => sp.GetRequiredService<ISolicitudPersonalService>()
            .CrearAsync(orgId, false, evento.Id, "trabajador@correo.pe", null, "http://x"));
        Assert.False(r.Exito);
        Assert.DoesNotContain(await ConAsync((sp, _) => sp.GetRequiredService<ISolicitudPersonalService>().EventosAsync(orgId, false)), e => e.Id == evento.Id);

        // Ponerle precio a un evento gratuito no está permitido
        var datos = await ConAsync(async (_, db) =>
        {
            var e = await db.Eventos.Include(x => x.TiposEntrada).Include(x => x.Imagenes).SingleAsync(x => x.Id == evento.Id);
            return new EventoFormViewModel
            {
                Id = e.Id, Titulo = e.Titulo, Descripcion = e.Descripcion, CategoriaId = e.CategoriaId, Fecha = e.Fecha, Ubicacion = e.Ubicacion,
                Latitud = e.Latitud, Longitud = e.Longitud, Plantilla = e.PlantillaPlano, ImagenesActualesIds = e.Imagenes.Select(i => i.Id).ToList(),
                TiposEntrada = e.TiposEntrada.Select(t => new TipoEntradaInput { Id = t.Id, Nombre = t.Nombre, Precio = 25, Capacidad = t.Capacidad }).ToList()
            };
        });
        var edicion = await ConAsync((sp, _) => sp.GetRequiredService<IEventoEdicionService>().ActualizarAsync(evento.Id, datos, "http://x"));
        Assert.False(edicion.Exito);
        Assert.Contains(edicion.Errores, e => e.Contains("entrada libre"));
    }
}
