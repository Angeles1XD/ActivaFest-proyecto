using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ActivaFest.Models;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🔑 HU18: RECUPERAR CONTRASEÑA (pruebas de integración)
// =============================
public class HU18RecuperarContrasenaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU18RecuperarContrasenaTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<HttpResponseMessage> SolicitarEnlaceAsync(HttpClient cliente, string email) =>
        await cliente.PostAsync("/Auth/OlvideContrasena", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(cliente, "/Auth/OlvideContrasena"),
            ["Email"] = email
        }));

    // Abre el enlace del correo y envía la nueva contraseña con el token que trae
    private static async Task<HttpResponseMessage> RestablecerAsync(HttpClient cliente, string enlace, string nueva)
    {
        var html = await cliente.GetStringAsync(enlace);
        string Oculto(string nombre) => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{nombre}\" value=\"([^\"]*)\"").Groups[1].Value);

        return await cliente.PostAsync("/Auth/RestablecerContrasena", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(cliente, enlace),
            ["Email"] = Oculto("Email"),
            ["Token"] = Oculto("Token"),
            ["Password"] = nueva,
            ["ConfirmarPassword"] = nueva
        }));
    }

    private string EnlaceDelCorreo(string email)
    {
        var correo = Assert.Single(_app.Correos.Para(email).Where(c => c.Asunto.StartsWith("Restablecer")));
        var url = WebUtility.HtmlDecode(Regex.Match(correo.Html, "href=\"([^\"]*RestablecerContrasena[^\"]*)\"").Groups[1].Value);
        return new Uri(url).PathAndQuery;
    }

    [Fact]
    public async Task El_enlace_permite_crear_una_nueva_contrasena_e_ingresar_con_ella()
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);

        await SolicitarEnlaceAsync(_app.Cliente(), email);
        var r = await RestablecerAsync(_app.Cliente(), EnlaceDelCorreo(email), "NuevaClave9");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await IniciarSesionAsync(_app.Cliente(), email, "NuevaClave9")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await IniciarSesionAsync(_app.Cliente(), email, "Clave1234")).StatusCode); // la anterior ya no sirve
    }

    [Fact]
    public async Task El_enlace_solo_se_puede_usar_una_vez()
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);
        await SolicitarEnlaceAsync(_app.Cliente(), email);
        var enlace = EnlaceDelCorreo(email);

        await RestablecerAsync(_app.Cliente(), enlace, "NuevaClave9");
        var segundoUso = await RestablecerAsync(_app.Cliente(), enlace, "OtraClave77");

        Assert.Equal(HttpStatusCode.OK, segundoUso.StatusCode);
        Assert.Contains("ya fue usado o venció", await TextoAsync(segundoUso));
        Assert.Equal(HttpStatusCode.OK, (await IniciarSesionAsync(_app.Cliente(), email, "OtraClave77")).StatusCode);
    }

    [Fact]
    public void El_enlace_vence_en_una_hora()
    {
        var opciones = _app.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;

        Assert.Equal(TimeSpan.FromHours(1), opciones.TokenLifespan);
    }

    [Fact]
    public async Task Un_enlace_alterado_no_sirve()
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);
        await SolicitarEnlaceAsync(_app.Cliente(), email);
        var enlace = Regex.Replace(EnlaceDelCorreo(email), "token=[^&]+", "token=CfDJ8inventado");

        var r = await RestablecerAsync(_app.Cliente(), enlace, "NuevaClave9");

        Assert.Contains("no es válido", await TextoAsync(r));
    }

    [Fact]
    public async Task Si_el_correo_no_existe_responde_igual_sin_revelarlo()
    {
        var registrado = Correo();
        await RegistrarAsync(_app.Cliente(), registrado);
        var inexistente = Correo();

        var a = await SolicitarEnlaceAsync(_app.Cliente(), registrado);
        var b = await SolicitarEnlaceAsync(_app.Cliente(), inexistente);

        Assert.Equal(a.StatusCode, b.StatusCode);
        Assert.Equal(a.Headers.Location, b.Headers.Location);
        Assert.Single(_app.Correos.Para(registrado).Where(c => c.Asunto.StartsWith("Restablecer")));
        Assert.Empty(_app.Correos.Para(inexistente));
    }
}
