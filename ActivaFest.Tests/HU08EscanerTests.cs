using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Acceso;
using ActivaFest.Services.Entradas;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🚪 HU08: ESCANEAR EL QR EN LA PUERTA (pruebas de integración)
// =============================
public class HU08EscanerTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU08EscanerTests(AppDePrueba app)
    {
        _app = app;
    }

    private T Con<T>(Func<ApplicationDbContext, T> consulta)
    {
        using var scope = _app.Services.CreateScope();
        return consulta(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    // Evento que empieza en una hora (ya se puede ingresar) con una entrada emitida
    private async Task<(int EventoId, string Codigo)> EventoConEntradaAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var comprador = new ApplicationUser { UserName = Correo(), Email = Correo(), NombreCompleto = "Ana Comprador" };
        await usuarios.CreateAsync(comprador, "Clave1234");

        var evento = new Evento
        {
            Titulo = $"Concierto {Guid.NewGuid():N}", Descripcion = "Concierto para probar la puerta.", CategoriaId = 1, Ubicacion = "Arequipa",
            Fecha = DateTime.Now.AddHours(1), Estado = EstadoEvento.Aprobado, Aforo = 10,
            TiposEntrada = { new TipoEntrada { Nombre = "VIP", Precio = 50, Capacidad = 10, Vendidas = 1 } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();

        var orden = new Orden
        {
            Codigo = Guid.NewGuid().ToString("N")[..12], UsuarioId = comprador.Id, EventoId = evento.Id, Estado = EstadoOrden.Pagada,
            Total = 50, FechaPago = DateTime.UtcNow, FechaExpiracion = DateTime.UtcNow,
            Items = { new OrdenItem { TipoEntradaId = evento.TiposEntrada.First().Id, Cantidad = 1, PrecioUnitario = 50 } }
        };
        db.Ordenes.Add(orden);
        await db.SaveChangesAsync();

        var entrada = new Entrada
        {
            Codigo = CodigoSeguro.Generar(), OrdenId = orden.Id, EventoId = evento.Id, TipoEntradaId = evento.TiposEntrada.First().Id,
            UsuarioId = comprador.Id, Estado = EstadoEntrada.Activa, FechaEmision = DateTime.UtcNow
        };
        db.Entradas.Add(entrada);
        await db.SaveChangesAsync();
        return (evento.Id, entrada.Codigo);
    }

    // Personal de Control de Acceso con el evento asignado (HU19), con la sesión iniciada
    private async Task<HttpClient> PersonalAsync(params int[] eventos)
    {
        var email = Correo();
        var cliente = _app.Cliente();
        await RegistrarAsync(cliente, email);

        using (var scope = _app.Services.CreateScope())
        {
            var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var usuario = (await usuarios.FindByEmailAsync(email))!;
            await usuarios.AddToRoleAsync(usuario, Roles.ControlAcceso);
            db.AsignacionesAcceso.AddRange(eventos.Select(e => new AsignacionAcceso { UsuarioId = usuario.Id, EventoId = e }));
            await db.SaveChangesAsync();
        }

        // Nueva sesión para que la cookie incluya el rol
        var puerta = _app.Cliente();
        await IniciarSesionAsync(puerta, email, "Clave1234");
        return puerta;
    }

    private string Qr(string codigo)
    {
        using var scope = _app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IQrEntradaService>().CrearContenido(codigo);
    }

    private static async Task<JsonElement> EscanearAsync(HttpClient puerta, int eventoId, string contenido)
    {
        var token = await TokenAsync(puerta, $"/Acceso/Escanear/{eventoId}");
        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/acceso/validar")
        {
            Content = JsonContent.Create(new { eventoId, contenido })
        };
        peticion.Headers.Add("RequestVerificationToken", token);
        var r = await puerta.SendAsync(peticion);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Una_entrada_activa_pasa_a_usada_y_la_segunda_lectura_muestra_entrada_ya_utilizada()
    {
        var (eventoId, codigo) = await EventoConEntradaAsync();
        var puerta = await PersonalAsync(eventoId);

        var primera = await EscanearAsync(puerta, eventoId, Qr(codigo));
        Assert.Equal("INGRESO PERMITIDO", primera.GetProperty("titulo").GetString());
        Assert.True(primera.GetProperty("permiteIngreso").GetBoolean());
        Assert.Equal("VIP", primera.GetProperty("zona").GetString());
        Assert.Equal(EstadoEntrada.Usada, Con(db => db.Entradas.AsNoTracking().Single(e => e.Codigo == codigo).Estado));

        var segunda = await EscanearAsync(puerta, eventoId, Qr(codigo));
        Assert.Equal("ENTRADA YA UTILIZADA", segunda.GetProperty("titulo").GetString());
        Assert.False(segunda.GetProperty("permiteIngreso").GetBoolean());
    }

    [Fact]
    public async Task Un_qr_invalido_o_de_otro_evento_muestra_ingreso_rechazado()
    {
        var (eventoId, _) = await EventoConEntradaAsync();
        var (otroEventoId, codigoOtro) = await EventoConEntradaAsync();
        var puerta = await PersonalAsync(eventoId);

        var invalido = await EscanearAsync(puerta, eventoId, "https://sitio-cualquiera.com/qr");
        var alterado = await EscanearAsync(puerta, eventoId, Qr("codigo-inventado"));
        var deOtroEvento = await EscanearAsync(puerta, eventoId, Qr(codigoOtro));

        Assert.All(new[] { invalido, alterado, deOtroEvento }, r =>
        {
            Assert.Equal("INGRESO RECHAZADO", r.GetProperty("titulo").GetString());
            Assert.False(r.GetProperty("permiteIngreso").GetBoolean());
        });
        Assert.Equal(EstadoEntrada.Activa, Con(db => db.Entradas.AsNoTracking().Single(e => e.Codigo == codigoOtro).Estado));
    }

    [Fact]
    public async Task Solo_el_rol_control_de_acceso_puede_usar_el_escaner()
    {
        var (eventoId, codigo) = await EventoConEntradaAsync();

        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        var usuario = _app.Cliente();
        await RegistrarAsync(usuario, Correo());

        foreach (var cliente in new[] { organizador, admin, usuario })
        {
            Assert.StartsWith("/Auth/AccesoDenegado", (await cliente.GetAsync($"/Acceso/Escanear/{eventoId}")).Headers.Location!.PathAndQuery);
            var api = await cliente.PostAsJsonAsync("/api/acceso/validar", new { eventoId, contenido = Qr(codigo) });
            Assert.NotEqual(HttpStatusCode.OK, api.StatusCode);
        }

        Assert.Equal(EstadoEntrada.Activa, Con(db => db.Entradas.AsNoTracking().Single(e => e.Codigo == codigo).Estado));
    }

    [Fact]
    public async Task El_escaner_es_una_vista_movil_con_lector_de_camara()
    {
        var (eventoId, _) = await EventoConEntradaAsync();
        var puerta = await PersonalAsync(eventoId);

        var html = await puerta.GetStringAsync($"/Acceso/Escanear/{eventoId}");

        Assert.Contains("html5-qrcode", html);
        Assert.Contains("/js/escaner.js", html);
        Assert.Contains("name=\"viewport\"", html);
    }
}
