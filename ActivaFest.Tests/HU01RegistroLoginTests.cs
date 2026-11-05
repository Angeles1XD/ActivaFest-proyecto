using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using static ActivaFest.Tests.Formularios;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Controllers;
using ActivaFest.Models;

namespace ActivaFest.Tests;

// =============================
// 🔐 HU01: REGISTRO E INICIO DE SESIÓN (pruebas de integración)
// =============================
public class HU01RegistroLoginTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU01RegistroLoginTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<ApplicationUser?> BuscarAsync(string email)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }


    [Fact]
    public async Task Registra_la_cuenta_con_sus_consentimientos_y_rol_Usuario()
    {
        var email = Correo();
        var r = await RegistrarAsync(_app.Cliente(), email, promociones: true);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var usuario = await BuscarAsync(email);
        Assert.NotNull(usuario);
        Assert.True(usuario.Activo);
        Assert.True(usuario.AceptaTratamientoDatos);
        Assert.True(usuario.AceptaPromociones);
        Assert.StartsWith("AQAAAA", usuario.PasswordHash); // hash PBKDF2 de Identity, nunca texto plano

        using var scope = _app.Services.CreateScope();
        var roles = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(usuario);
        Assert.Equal(new[] { Roles.Asistente }, roles);
    }

    [Fact]
    public async Task Sin_aceptar_la_politica_de_privacidad_no_crea_la_cuenta()
    {
        var email = Correo();
        var r = await RegistrarAsync(_app.Cliente(), email, consiente: false);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Debes aceptar la política de privacidad", await TextoAsync(r));
        Assert.Null(await BuscarAsync(email));
    }

    [Fact]
    public async Task El_correo_debe_ser_unico()
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);

        var r = await RegistrarAsync(_app.Cliente(), email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Ya existe una cuenta con ese correo", await TextoAsync(r));
    }

    [Theory]
    [InlineData("Corta12")]   // 7 caracteres
    [InlineData("sinnumeros")]
    public async Task Rechaza_contrasenas_debiles(string password)
    {
        var email = Correo();
        var r = await RegistrarAsync(_app.Cliente(), email, password);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(await BuscarAsync(email));
    }

    [Fact]
    public async Task Nadie_puede_registrarse_como_administrador_ni_organizador_manipulando_el_formulario()
    {
        // HU31: el formulario ya no tiene tipo de cuenta; aunque alguien envíe "Rol", la cuenta nace como Usuario
        foreach (var rol in new[] { Roles.Administrador, Roles.Organizador, Roles.ControlAcceso })
        {
            var cliente = _app.Cliente();
            var email = Correo();
            var datos = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = await TokenAsync(cliente, "/Auth/Registro"),
                ["NombreCompleto"] = "Intruso", ["Email"] = email, ["Password"] = "Clave1234", ["ConfirmarPassword"] = "Clave1234",
                ["Rol"] = rol, ["AceptaTratamientoDatos"] = "true"
            };
            await cliente.PostAsync("/Auth/Registro", new FormUrlEncodedContent(datos));

            using var scope = _app.Services.CreateScope();
            var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuario = (await usuarios.FindByEmailAsync(email))!;
            Assert.Equal(new[] { Roles.Asistente }, await usuarios.GetRolesAsync(usuario));
        }

        Assert.DoesNotContain("name=\"Rol\"", await _app.Cliente().GetStringAsync("/Auth/Registro"));
    }

    [Fact]
    public async Task Con_credenciales_invalidas_el_mensaje_no_revela_que_dato_fallo()
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);

        var claveErrada = await TextoAsync(await IniciarSesionAsync(_app.Cliente(), email, "Incorrecta1"));
        var correoInexistente = await TextoAsync(await IniciarSesionAsync(_app.Cliente(), Correo(), "Clave1234"));

        var mensaje = AuthController.MensajeLoginFallido;
        Assert.Contains(mensaje, claveErrada);
        Assert.Contains(mensaje, correoInexistente);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_puede_iniciar_sesion()
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);

        using (var scope = _app.Services.CreateScope())
        {
            var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuario = (await usuarios.FindByEmailAsync(email))!;
            usuario.Activo = false;
            await usuarios.UpdateAsync(usuario);
        }

        var r = await IniciarSesionAsync(_app.Cliente(), email, "Clave1234");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(AuthController.MensajeLoginFallido, await TextoAsync(r));
    }

    [Fact]
    public async Task Cada_usuario_accede_solo_a_las_opciones_de_su_rol()
    {
        var anonimo = _app.Cliente();
        Assert.StartsWith("/Auth/Login", (await anonimo.GetAsync("/Entradas")).Headers.Location!.PathAndQuery);

        var usuario = _app.Cliente();
        await RegistrarAsync(usuario, Correo());
        Assert.Equal(HttpStatusCode.OK, (await usuario.GetAsync("/Entradas")).StatusCode);
        Assert.StartsWith("/Auth/AccesoDenegado", (await usuario.GetAsync("/Admin")).Headers.Location!.PathAndQuery);
        Assert.StartsWith("/Auth/AccesoDenegado", (await usuario.GetAsync("/Eventos/Crear")).Headers.Location!.PathAndQuery);

        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        Assert.Equal(HttpStatusCode.OK, (await organizador.GetAsync("/Eventos/Crear")).StatusCode);
        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Admin")).Headers.Location!.PathAndQuery);

        var admin = _app.Cliente();
        Assert.Equal(HttpStatusCode.Redirect, (await IniciarSesionAsync(admin, "admin@test.com", "Admin123!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin")).StatusCode);
    }

    // Regresión: con [Range(true, true)] la validación del navegador (jQuery) rechazaba la casilla aunque estuviera marcada
    [Fact]
    public async Task La_casilla_de_consentimiento_se_puede_marcar_en_el_navegador()
    {
        var html = await _app.Cliente().GetStringAsync("/Auth/Registro");
        var casilla = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*id=\"AceptaTratamientoDatos\"[^>]*>").Value;

        Assert.NotEmpty(casilla);
        Assert.DoesNotContain("data-val-range", casilla);
        Assert.Contains("required", casilla);
    }
}
