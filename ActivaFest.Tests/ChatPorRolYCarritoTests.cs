using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Asistente;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🔐 EL CHAT SE ADECÚA AL ROL Y 🛒 CARRITO (integración por /api/asistente-ia)
// =============================
public abstract class PruebasDeChat
{
    protected readonly AppDePrueba _app;

    protected PruebasDeChat(AppDePrueba app)
    {
        _app = app;
    }

    protected static async Task<JsonElement> PreguntarAsync(HttpClient cliente, string mensaje, JsonElement? pendiente = null)
    {
        var r = await cliente.PostAsJsonAsync("/api/asistente-ia", new { mensaje, pendiente });
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Pide la reserva y la confirma, como el usuario en el chat
    protected static async Task<JsonElement> ReservarAsync(HttpClient cliente, string mensaje)
    {
        var resumen = await PreguntarAsync(cliente, mensaje);
        return await PreguntarAsync(cliente, "Sí, reservar", resumen.GetProperty("pendiente"));
    }

    protected static string Intencion(JsonElement r) => r.GetProperty("intencion").GetString()!;
    protected static string Texto(JsonElement r) => r.GetProperty("reply").GetString()!;

    protected async Task<(HttpClient Cliente, string Id)> UsuarioAsync(string rol = Roles.Asistente)
    {
        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email, rol: rol);
        return (cliente, Con(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).Single()));
    }

    protected async Task<HttpClient> AdminAsync()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        return admin;
    }

    protected T Con<T>(Func<ApplicationDbContext, T> accion)
    {
        using var scope = _app.Services.CreateScope();
        return accion(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    protected static string Titulo(string nombre) => $"{nombre} {Guid.NewGuid():N}"[..(nombre.Length + 9)];

    protected Evento Evento(string titulo) =>
        Con(db => EntradasDePrueba.Evento(db, titulo, PlantillaPlano.CampoAbierto, DateTime.Today.AddDays(5).AddHours(20)));

}

// Cada clase tiene su propia app: el chat limita los mensajes por minuto
public class ChatPorRolTests : PruebasDeChat, IClassFixture<AppDePrueba>
{
    public ChatPorRolTests(AppDePrueba app) : base(app) { }

    // ---------- Usuario común y visitante: solo compras, reservas y eventos ----------
    [Theory]
    [InlineData("¿Cuántos eventos hay en toda la página?")]
    [InlineData("¿Cuántos usuarios hay registrados?")]
    [InlineData("Dame las ventas de la plataforma")]
    [InlineData("¿Cuánto cobra la plataforma de comisión?")]
    [InlineData("Hazme un gráfico de pastel de los ingresos")]
    public async Task El_usuario_comun_no_llega_a_los_datos_de_otros_roles(string pregunta)
    {
        var (ana, _) = await UsuarioAsync();

        var r = await PreguntarAsync(ana, pregunta);

        Assert.Equal(AlcanceChat.IntencionDenegada, Intencion(r));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("tabla").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("grafico").ValueKind);
    }

    [Fact]
    public async Task El_visitante_tampoco_ve_datos_de_la_plataforma_y_para_reservar_inicia_sesion()
    {
        var visitante = _app.Cliente();

        Assert.Equal(AlcanceChat.IntencionDenegada, Intencion(await PreguntarAsync(visitante, "¿Cuántos usuarios hay en toda la plataforma?")));
        Assert.Contains("inicia sesión", Texto(await PreguntarAsync(visitante, "Ver mi carrito")));
    }

    [Fact]
    public async Task El_usuario_sigue_pudiendo_preguntar_por_compras()
    {
        var (ana, _) = await UsuarioAsync();

        var r = await PreguntarAsync(ana, "¿Cómo compro entradas?");

        Assert.NotEqual(AlcanceChat.IntencionDenegada, Intencion(r));
    }

    // ---------- Organizador: sus eventos y sus ventas, no la plataforma ----------
    [Fact]
    public async Task El_organizador_ve_su_dashboard_pero_no_el_de_la_plataforma()
    {
        var (org, _) = await UsuarioAsync(Roles.Organizador);

        var plataforma = await PreguntarAsync(org, "¿Cuántos eventos hay en toda la plataforma?");
        Assert.Equal(AlcanceChat.IntencionDenegada, Intencion(plataforma));
        Assert.Contains("tu dashboard", Texto(plataforma));

        Assert.Equal(Intenciones.VentasOrganizador, Intencion(await PreguntarAsync(org, "Muéstrame mi dashboard de ventas")));
        Assert.Equal(Intenciones.ConsultaMisEventos, Intencion(await PreguntarAsync(org, "Hazme un gráfico de barras de lo que llevo")));
    }

    // ---------- Administrador: toda la plataforma, con tabla y gráfico ----------
    [Fact]
    public async Task El_administrador_consulta_toda_la_plataforma_desde_el_chat()
    {
        var admin = await AdminAsync();
        Evento(Titulo("Plataforma"));

        var r = await PreguntarAsync(admin, "¿Cuántos eventos hay en total?");

        Assert.Equal(Intenciones.ConsultaPlataforma, Intencion(r));
        Assert.Contains("en toda la plataforma", Texto(r));
        Assert.Equal("Eventos de toda la plataforma", r.GetProperty("tabla").GetProperty("titulo").GetString());
        Assert.Equal("doughnut", r.GetProperty("grafico").GetProperty("tipo").GetString());
    }

    [Fact]
    public async Task El_administrador_elige_el_tipo_de_grafico_y_sigue_moderando()
    {
        var admin = await AdminAsync();
        Evento(Titulo("Graficazo"));

        var barras = await PreguntarAsync(admin, "Crea un gráfico de barras de eventos por estado");
        Assert.Equal("bar", barras.GetProperty("grafico").GetProperty("tipo").GetString());

        Assert.Equal(Intenciones.ConsultaPlataforma, Intencion(await PreguntarAsync(admin, "¿Qué eventos faltan aprobar?")));
        Assert.Equal(Intenciones.ConsultaPlataforma, Intencion(await PreguntarAsync(admin, "¿Cuántos eventos publicados hay?")));
        Assert.Equal(Intenciones.ConsultaPlataforma, Intencion(await PreguntarAsync(admin, "Dame un resumen de la plataforma")));
    }
}

public class CarritoTests : PruebasDeChat, IClassFixture<AppDePrueba>
{
    public CarritoTests(AppDePrueba app) : base(app) { }

    // ---------- Reservar por el chat y verlo en el carrito ----------
    [Fact]
    public async Task Lo_que_reserva_el_asistente_aparece_en_el_carrito()
    {
        var (ana, id) = await UsuarioAsync();
        var titulo = Titulo("Carritazo");
        var evento = Evento(titulo);

        var reserva = await ReservarAsync(ana, $"Resérvame dos entradas VIP para {titulo}");
        Assert.Contains("/Carrito", reserva.GetProperty("enlaces").EnumerateArray().Select(e => e.GetProperty("url").GetString()));

        var html = await TextoAsync(await ana.GetAsync("/Carrito"));
        Assert.Contains(titulo, html);
        Assert.Contains("2 × VIP", html);
        Assert.Contains(Formato.Monto(240), html);
        var codigo = Con(db => db.Ordenes.Single(o => o.UsuarioId == id && o.EventoId == evento.Id).Codigo);
        Assert.Contains($"/Compras/Checkout?codigo={codigo}", html);
        Assert.Contains("nav-carrito-contador", html);

        var carrito = await PreguntarAsync(ana, "¿Qué tengo en mi carrito?");
        Assert.Equal(Intenciones.MiCarrito, Intencion(carrito));
        Assert.Contains(titulo, Texto(carrito));
        Assert.Contains("2 VIP", Texto(carrito));
    }

    [Fact]
    public async Task El_carrito_vacio_lo_dice_y_pide_sesion()
    {
        var (ana, _) = await UsuarioAsync();

        Assert.Contains("Tu carrito está vacío", await TextoAsync(await ana.GetAsync("/Carrito")));
        Assert.Contains("vacío", Texto(await PreguntarAsync(ana, "Ver mi carrito")));

        var visitante = await _app.Cliente().GetAsync("/Carrito");
        Assert.StartsWith("/Auth/Login", visitante.Headers.Location!.PathAndQuery);
    }

    private async Task<HttpResponseMessage> CancelarAsync(HttpClient cliente, string codigo)
    {
        var datos = new Dictionary<string, string> { ["__RequestVerificationToken"] = await TokenAsync(cliente, "/Entradas"), ["codigo"] = codigo };
        return await cliente.PostAsync("/Carrito/Cancelar", new FormUrlEncodedContent(datos));
    }

    [Fact]
    public async Task Solo_el_dueno_cancela_la_reserva_y_las_entradas_se_liberan()
    {
        var (ana, id) = await UsuarioAsync();
        var (beto, _) = await UsuarioAsync();
        var titulo = Titulo("Cancelazo");
        var evento = Evento(titulo);
        await ReservarAsync(ana, $"Resérvame dos entradas VIP para {titulo}");
        var orden = Con(db => db.Ordenes.Single(o => o.UsuarioId == id && o.EventoId == evento.Id));
        int Reservadas() => Con(db => db.TiposEntrada.Single(t => t.EventoId == evento.Id && t.Nombre == "VIP").Reservadas);
        Assert.Equal(2, Reservadas());

        await CancelarAsync(beto, orden.Codigo);
        Assert.Equal(EstadoOrden.PendientePago, Con(db => db.Ordenes.Single(o => o.Id == orden.Id).Estado));

        await CancelarAsync(ana, orden.Codigo);
        Assert.Equal(EstadoOrden.Cancelada, Con(db => db.Ordenes.Single(o => o.Id == orden.Id).Estado));
        Assert.Equal(0, Reservadas());
        Assert.DoesNotContain(titulo, await TextoAsync(await ana.GetAsync("/Carrito")));
    }

    [Fact]
    public async Task El_carrito_de_cada_usuario_es_solo_suyo()
    {
        var (ana, _) = await UsuarioAsync();
        var (beto, _) = await UsuarioAsync();
        var titulo = Titulo("Privadito");
        Evento(titulo);
        await ReservarAsync(ana, $"Resérvame una entrada VIP para {titulo}");

        Assert.DoesNotContain(titulo, await TextoAsync(await beto.GetAsync("/Carrito")));
    }

    [Fact]
    public async Task Agregar_al_carrito_desde_la_ficha_lleva_al_carrito_y_no_al_pago()
    {
        var (ana, _) = await UsuarioAsync();
        var titulo = Titulo("Fichazo");
        var evento = Evento(titulo);
        var zona = Con(db => db.TiposEntrada.Single(t => t.EventoId == evento.Id && t.Nombre == "General").Id);
        Assert.Contains("id=\"btnCarrito\"", await TextoAsync(await ana.GetAsync($"/Eventos/Detalle/{evento.Id}")));

        var datos = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(ana, $"/Eventos/Detalle/{evento.Id}"),
            ["eventoId"] = evento.Id.ToString(),
            [$"cantidades[{zona}]"] = "3",
            ["destino"] = "carrito"
        };
        var r = await ana.PostAsync("/Compras/Iniciar", new FormUrlEncodedContent(datos));

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/Carrito", r.Headers.Location!.OriginalString);
        Assert.Contains("3 × General", await TextoAsync(await ana.GetAsync("/Carrito")));
    }

    [Fact]
    public async Task Una_reserva_vencida_sale_del_total_y_se_puede_volver_a_elegir()
    {
        var (ana, id) = await UsuarioAsync();
        var titulo = Titulo("Vencidazo");
        var evento = Evento(titulo);
        await ReservarAsync(ana, $"Resérvame una entrada VIP para {titulo}");
        Con(db =>
        {
            db.Ordenes.Single(o => o.UsuarioId == id && o.EventoId == evento.Id).FechaExpiracion = DateTime.UtcNow.AddMinutes(-5);
            return db.SaveChanges();
        });

        var html = await TextoAsync(await ana.GetAsync("/Carrito"));

        Assert.Contains("Reservas vencidas", html);
        Assert.Contains("Tu carrito está vacío", html);
        Assert.Contains(titulo, html);
        Assert.DoesNotContain("nav-carrito-contador", html);
    }
}
