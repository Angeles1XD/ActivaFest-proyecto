using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// ✅ HU10: APROBAR O RECHAZAR EVENTOS (pruebas de integración)
// =============================
public class HU10ModeracionTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU10ModeracionTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        return admin;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string url, params (string Clave, string Valor)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Clave, c.Valor)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, "/Admin")));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    // Un organizador registrado envía un evento: queda Pendiente
    private async Task<(Evento Evento, string EmailOrganizador)> EventoAsync(EstadoEvento estado = EstadoEvento.Pendiente, int dias = 10, int fotos = 3)
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email, rol: Roles.Organizador);

        using var scope = _app.Services.CreateScope();
        var organizador = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = new Evento
        {
            Titulo = $"Feria {Guid.NewGuid():N}", Descripcion = "Feria artesanal con productos de la región.", CategoriaId = 4,
            Ubicacion = "Plaza de Armas", Fecha = DateTime.Now.AddDays(dias), Estado = estado, OrganizadorId = organizador!.Id,
            Aforo = 100, TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 10, Capacidad = 100 } },
            Imagenes = Enumerable.Range(0, fotos).Select(i => new EventoImagen { Ruta = $"/uploads/eventos/f{i}.png", Orden = i }).ToList()
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        return (evento, email);
    }

    private async Task<Evento> RecargarAsync(int id)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Eventos.AsNoTracking().FirstAsync(e => e.Id == id);
    }

    [Fact]
    public async Task El_administrador_ve_la_lista_de_eventos_pendientes()
    {
        var (pendiente, _) = await EventoAsync();
        var (aprobado, _) = await EventoAsync(EstadoEvento.Aprobado);

        var html = await (await AdminAsync()).GetStringAsync("/Admin");

        Assert.Contains(pendiente.Titulo, html);
        Assert.DoesNotContain(aprobado.Titulo, html);
    }

    [Fact]
    public async Task Al_aprobar_el_evento_se_publica_en_el_catalogo_y_se_avisa_al_organizador()
    {
        var (evento, email) = await EventoAsync();

        await PostAsync(await AdminAsync(), $"/Admin/Aprobar/{evento.Id}");

        Assert.Equal(EstadoEvento.Aprobado, (await RecargarAsync(evento.Id)).Estado);
        Assert.Equal(HttpStatusCode.OK, (await _app.Cliente().GetAsync($"/Eventos/Detalle/{evento.Id}")).StatusCode);
        Assert.Contains(_app.Correos.Para(email), c => c.Asunto == $"Evento aprobado: {evento.Titulo}");
    }

    [Fact]
    public async Task Al_rechazar_se_registra_el_motivo_y_se_notifica_al_organizador_por_correo()
    {
        var (evento, email) = await EventoAsync();

        await PostAsync(await AdminAsync(), $"/Admin/Rechazar/{evento.Id}", ("motivo", "Las fotos no corresponden al recinto."));

        var rechazado = await RecargarAsync(evento.Id);
        Assert.Equal(EstadoEvento.Rechazado, rechazado.Estado);
        Assert.Equal("Las fotos no corresponden al recinto.", rechazado.MotivoRechazo);
        var correo = Assert.Single(_app.Correos.Para(email), c => c.Asunto == $"Evento no aprobado: {evento.Titulo}");
        Assert.Contains("Las fotos no corresponden al recinto.", correo.Html);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Cliente().GetAsync($"/Eventos/Detalle/{evento.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task No_se_puede_rechazar_sin_motivo(string motivo)
    {
        var (evento, email) = await EventoAsync();

        await PostAsync(await AdminAsync(), $"/Admin/Rechazar/{evento.Id}", ("motivo", motivo));

        Assert.Equal(EstadoEvento.Pendiente, (await RecargarAsync(evento.Id)).Estado);
        Assert.DoesNotContain(_app.Correos.Para(email), c => c.Asunto.StartsWith("Evento no aprobado"));
    }

    [Fact]
    public async Task Un_motivo_demasiado_largo_se_rechaza()
    {
        var (evento, _) = await EventoAsync();

        await PostAsync(await AdminAsync(), $"/Admin/Rechazar/{evento.Id}", ("motivo", new string('x', 501)));

        Assert.Equal(EstadoEvento.Pendiente, (await RecargarAsync(evento.Id)).Estado);
    }

    [Fact]
    public async Task Solo_los_eventos_pendientes_se_moderan_segun_el_diagrama_de_estados()
    {
        var admin = await AdminAsync();
        var (aprobado, _) = await EventoAsync(EstadoEvento.Aprobado);
        var (rechazado, _) = await EventoAsync(EstadoEvento.Rechazado);

        // Un evento publicado (con posibles ventas) no pasa a Rechazado
        await PostAsync(admin, $"/Admin/Rechazar/{aprobado.Id}", ("motivo", "Ya no me gusta."));
        Assert.Equal(EstadoEvento.Aprobado, (await RecargarAsync(aprobado.Id)).Estado);

        // Uno rechazado no se aprueba directamente: el organizador debe corregirlo y vuelve a Pendiente
        await PostAsync(admin, $"/Admin/Aprobar/{rechazado.Id}");
        Assert.Equal(EstadoEvento.Rechazado, (await RecargarAsync(rechazado.Id)).Estado);
    }

    [Fact]
    public async Task No_se_aprueba_un_evento_cuya_fecha_ya_paso()
    {
        var (vencido, _) = await EventoAsync(dias: -2);

        await PostAsync(await AdminAsync(), $"/Admin/Aprobar/{vencido.Id}");

        Assert.Equal(EstadoEvento.Pendiente, (await RecargarAsync(vencido.Id)).Estado);
    }

    [Fact]
    public async Task No_se_aprueba_un_evento_con_menos_de_tres_fotos()
    {
        var (evento, _) = await EventoAsync(fotos: 2);

        await PostAsync(await AdminAsync(), $"/Admin/Aprobar/{evento.Id}");

        Assert.Equal(EstadoEvento.Pendiente, (await RecargarAsync(evento.Id)).Estado);
    }

    [Fact]
    public async Task Solo_el_administrador_puede_moderar()
    {
        var (evento, _) = await EventoAsync();
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);

        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Admin")).Headers.Location!.PathAndQuery);
        var datos = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(organizador, "/Eventos/Crear")
        });
        await organizador.PostAsync($"/Admin/Aprobar/{evento.Id}", datos);

        Assert.Equal(EstadoEvento.Pendiente, (await RecargarAsync(evento.Id)).Estado);
    }
}
