using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Organizadores;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 📝 HU31: SOLICITUD PARA ORGANIZAR EVENTOS · 🚪 HU32: PERSONAL DE CONTROL Y CONTACTO
// =============================
public class HU31HU32SolicitudesTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU31HU32SolicitudesTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<T> ConAsync<T>(Func<UserManager<ApplicationUser>, ApplicationDbContext, Task<T>> accion)
    {
        using var scope = _app.Services.CreateScope();
        return await accion(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(), scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    private static (string, string)[] Solicitud(string documento = "12345678", string telefono = "987654321", string plazo = "Dias30") => new[]
    {
        ("Tipo", "PersonaNatural"), ("NombreOrganizador", "Ana Pérez Producciones"), ("Documento", documento), ("Telefono", telefono),
        ("DescripcionEventos", "Conciertos de rock para jóvenes en Arequipa, de 300 a 1000 personas."), ("WebORedes", "https://facebook.com/ana"),
        ("PlazoSolicitado", plazo), ("AceptaCondiciones", "true")
    };

    private async Task<(HttpClient Cliente, string Email, string Id)> UsuarioAsync()
    {
        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email);
        var id = await ConAsync(async (u, _) => (await u.FindByEmailAsync(email))!.Id);
        return (cliente, email, id);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        return admin;
    }

    [Fact]
    public async Task Una_cuenta_nueva_no_puede_crear_eventos_y_ve_los_requisitos_para_solicitarlo()
    {
        var (cliente, _, _) = await UsuarioAsync();

        Assert.StartsWith("/Auth/AccesoDenegado", (await cliente.GetAsync("/Eventos/Crear")).Headers.Location!.PathAndQuery);

        var html = await TextoAsync(await cliente.GetAsync("/Organizador/Solicitar"));
        foreach (var requisito in SolicitudOrganizadorService.Requisitos)
            Assert.Contains(requisito, html);
        Assert.Contains("Enviar solicitud", html);

        // Sin sesión también se ven los requisitos, con la invitación a crear la cuenta
        var anonimo = await TextoAsync(await _app.Cliente().GetAsync("/Organizador/Solicitar"));
        Assert.Contains("Primero, tu cuenta", anonimo);
        Assert.Contains(SolicitudOrganizadorService.Requisitos[2], anonimo);
    }

    [Theory]
    [InlineData("1234", "987654321", "El DNI debe tener 8 dígitos.")]
    [InlineData("12345678", "12345", "Escribe un celular válido")]
    public async Task Los_datos_de_la_solicitud_se_validan(string documento, string telefono, string error)
    {
        var (cliente, _, id) = await UsuarioAsync();

        var html = await TextoAsync(await PostAsync(cliente, "/Organizador/Solicitar", "/Organizador/Solicitar", Solicitud(documento, telefono)));

        Assert.Contains(error, html);
        Assert.False(await ConAsync((_, db) => db.SolicitudesOrganizador.AnyAsync(s => s.UsuarioId == id)));
    }

    [Fact]
    public async Task El_admin_aprueba_por_un_tiempo_y_la_cuenta_puede_crear_eventos()
    {
        var (cliente, email, id) = await UsuarioAsync();

        var r = await PostAsync(cliente, "/Organizador/Solicitar", "/Organizador/Solicitar", Solicitud());
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Solicitud en revisión", await TextoAsync(await cliente.GetAsync("/Organizador/Solicitar")));
        Assert.Contains(_app.Correos.Para("admin@test.com"), c => c.Asunto == "Nueva solicitud para organizar eventos");

        // Solo una solicitud pendiente a la vez
        var otra = await TextoAsync(await PostAsync(cliente, "/Organizador/Solicitar", "/Organizador/Solicitar", Solicitud()));
        Assert.DoesNotContain("Ana Pérez Producciones\" required", otra);
        Assert.Equal(1, await ConAsync((_, db) => db.SolicitudesOrganizador.CountAsync(s => s.UsuarioId == id)));

        // El admin la ve con sus datos y la aprueba por 90 días (aunque pidió 30)
        var admin = await AdminAsync();
        var panel = await TextoAsync(await admin.GetAsync("/Solicitudes"));
        Assert.Contains("Ana Pérez Producciones", panel);
        Assert.Contains("12345678", panel);
        var solicitudId = await ConAsync((_, db) => db.SolicitudesOrganizador.Where(s => s.UsuarioId == id).Select(s => s.Id).SingleAsync());
        await PostAsync(admin, "/Solicitudes", "/Solicitudes/AprobarOrganizador", ("id", solicitudId.ToString()), ("plazo", "Dias90"));

        var (roles, hasta) = await ConAsync(async (u, _) => { var x = (await u.FindByIdAsync(id))!; return (await u.GetRolesAsync(x), x.OrganizadorHasta); });
        Assert.Contains(Roles.Organizador, roles);
        Assert.InRange(hasta!.Value, DateTime.UtcNow.AddDays(89), DateTime.UtcNow.AddDays(91));
        Assert.Contains(_app.Correos.Para(email), c => c.Asunto.StartsWith("¡Ya puedes publicar"));

        // Al volver a entrar ya puede crear eventos
        var nuevo = _app.Cliente();
        await IniciarSesionAsync(nuevo, email, "Clave1234");
        Assert.Equal(HttpStatusCode.OK, (await nuevo.GetAsync("/Eventos/Crear")).StatusCode);
        Assert.Contains("Tu cuenta ya es organizadora", await TextoAsync(await nuevo.GetAsync("/Organizador/Solicitar")));
    }

    [Fact]
    public async Task Si_el_admin_rechaza_ve_el_motivo_y_puede_volver_a_solicitar()
    {
        var (cliente, email, id) = await UsuarioAsync();
        await PostAsync(cliente, "/Organizador/Solicitar", "/Organizador/Solicitar", Solicitud());
        var solicitudId = await ConAsync((_, db) => db.SolicitudesOrganizador.Where(s => s.UsuarioId == id).Select(s => s.Id).SingleAsync());

        var admin = await AdminAsync();
        await PostAsync(admin, "/Solicitudes", "/Solicitudes/RechazarOrganizador", ("id", solicitudId.ToString()), ("motivo", "El RUC no coincide con la empresa."));

        var html = await TextoAsync(await cliente.GetAsync("/Organizador/Solicitar"));
        Assert.Contains("El RUC no coincide con la empresa.", html);
        Assert.Contains("Enviar solicitud", html);
        Assert.Contains(_app.Correos.Para(email), c => c.Asunto == "Tu solicitud para organizar eventos");
        Assert.DoesNotContain(Roles.Organizador, await ConAsync(async (u, _) => await u.GetRolesAsync((await u.FindByIdAsync(id))!)));
    }

    [Fact]
    public async Task Al_vencer_el_plazo_pierde_el_permiso_pero_sus_eventos_siguen()
    {
        var (_, email, id) = await UsuarioAsync();
        await ConAsync(async (u, _) =>
        {
            var x = (await u.FindByIdAsync(id))!;
            await u.AddToRoleAsync(x, Roles.Organizador);
            x.OrganizadorHasta = DateTime.UtcNow.AddMinutes(-1);
            await u.UpdateAsync(x);
            return 0;
        });

        using (var scope = _app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<ISolicitudOrganizadorService>().VencerAsync() >= 1);

        var roles = await ConAsync(async (u, _) => await u.GetRolesAsync((await u.FindByIdAsync(id))!));
        Assert.DoesNotContain(Roles.Organizador, roles);
        Assert.Contains(_app.Correos.Para(email), c => c.Asunto == "Tu periodo como organizador terminó");
    }

    [Fact]
    public async Task El_organizador_pide_personal_y_al_aprobarlo_puede_escanear_solo_ese_evento()
    {
        var organizador = _app.Cliente();
        var correoOrg = Correo();
        await RegistrarAsync(organizador, correoOrg, rol: Roles.Organizador);
        var (_, correoStaff, staffId) = await UsuarioAsync();
        var eventoId = await ConAsync(async (u, db) =>
        {
            var org = (await u.FindByEmailAsync(correoOrg))!;
            var ev = new Evento
            {
                Titulo = $"Festival {Guid.NewGuid():N}"[..17], Descripcion = "Evento para probar el personal.", CategoriaId = 1, Ubicacion = "Arequipa",
                Fecha = DateTime.Now.AddDays(5), Estado = EstadoEvento.Aprobado, OrganizadorId = org.Id, Aforo = 50,
                TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 20, Capacidad = 50 } }
            };
            db.Eventos.Add(ev);
            await db.SaveChangesAsync();
            return ev.Id;
        });

        // El organizador solo escribe los correos: no se le dice si una cuenta existe ni de quién es
        var r = await PostAsync(organizador, "/Personal", "/Personal/Solicitar", ("eventoId", eventoId.ToString()),
            ("correos", $"{correoStaff}, nadie@noexiste.pe"), ("mensaje", "Estará en la Puerta 1"));
        var pagina = await TextoAsync(await organizador.GetAsync(r.Headers.Location!));
        Assert.Contains("Solicitud enviada", pagina);
        Assert.Contains("⏳ Pendiente", pagina);
        Assert.DoesNotContain("Ana Pérez", pagina);

        // El admin ve cada correo, a quién pertenece y cuál no existe, y puede buscarlo en Usuarios
        var admin = await AdminAsync();
        var panel = await TextoAsync(await admin.GetAsync("/Solicitudes"));
        Assert.Contains("Ana Pérez · ", panel);
        Assert.Contains("se le habilitará el control de acceso", panel);
        Assert.Contains("no existe: no se asignará", panel);
        var enlace = System.Text.RegularExpressions.Regex.Match(panel, "<a[^>]*>Ver usuario").Value;
        Assert.True(enlace.Contains("/Usuarios?buscar="), enlace);
        Assert.Contains("Estará en la Puerta 1", panel);
        var solicitudId = await ConAsync((_, db) => db.SolicitudesPersonal.Where(s => s.EventoId == eventoId).Select(s => s.Id).SingleAsync());
        await PostAsync(admin, "/Solicitudes", "/Solicitudes/AprobarPersonal", ("id", solicitudId.ToString()));

        Assert.Contains(Roles.ControlAcceso, await ConAsync(async (u, _) => await u.GetRolesAsync((await u.FindByIdAsync(staffId))!)));
        Assert.True(await ConAsync((_, db) => db.AsignacionesAcceso.AnyAsync(a => a.UsuarioId == staffId && a.EventoId == eventoId)));
        Assert.Contains(_app.Correos.Para(correoStaff), c => c.Asunto.StartsWith("Validarás las entradas"));

        // El organizador comprueba qué correos ya quedaron habilitados (sin ver nombres)
        var estado = await TextoAsync(await organizador.GetAsync("/Personal"));
        Assert.Matches($@"data-correo=""{System.Text.RegularExpressions.Regex.Escape(correoStaff)}"">\s*<span>[^<]+</span>\s*<span class=""chip-asignado"">\s*✅ Habilitado", estado);
        Assert.Matches(@"data-correo=""nadie@noexiste.pe"">\s*<span>[^<]+</span>\s*<span class=""chip-alerta"">\s*No habilitado", estado);
        Assert.DoesNotContain("Ana Pérez", estado);

        var puerta = _app.Cliente();
        await IniciarSesionAsync(puerta, correoStaff, "Clave1234");
        Assert.Equal(HttpStatusCode.OK, (await puerta.GetAsync($"/Acceso/Escanear/{eventoId}")).StatusCode);
    }

    // El organizador no puede buscar cuentas ni ver datos de otros usuarios
    [Fact]
    public async Task El_organizador_no_puede_buscar_cuentas_de_otros_usuarios()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var (_, correoAmigo, _) = await UsuarioAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await organizador.GetAsync($"/Personal/Cuenta?correo={Uri.EscapeDataString(correoAmigo)}")).StatusCode);
        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync($"/Usuarios?buscar={Uri.EscapeDataString(correoAmigo)}")).Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Un_organizador_no_puede_pedir_personal_para_un_evento_ajeno()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var (_, correoStaff, _) = await UsuarioAsync();
        var ajeno = await ConAsync(async (u, db) =>
        {
            var admin = (await u.FindByEmailAsync("admin@test.com"))!;
            var ev = new Evento
            {
                Titulo = "Evento ajeno", Descripcion = "De otro organizador.", CategoriaId = 1, Ubicacion = "Lima", Fecha = DateTime.Now.AddDays(5),
                Estado = EstadoEvento.Aprobado, OrganizadorId = admin.Id, Aforo = 10, TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 1, Capacidad = 10 } }
            };
            db.Eventos.Add(ev);
            await db.SaveChangesAsync();
            return ev.Id;
        });

        var r = await PostAsync(organizador, "/Personal", "/Personal/Solicitar", ("eventoId", ajeno.ToString()), ("correos", correoStaff));

        Assert.Contains("Elige uno de tus eventos", await TextoAsync(await organizador.GetAsync(r.Headers.Location!)));
        Assert.False(await ConAsync((_, db) => db.SolicitudesPersonal.AnyAsync(s => s.EventoId == ajeno)));
    }

    [Fact]
    public async Task La_pagina_de_contacto_ofrece_las_solicitudes_y_el_correo()
    {
        var html = await TextoAsync(await _app.Cliente().GetAsync("/Contacto"));

        Assert.Contains("soporte@activafest.com", html);
        Assert.Contains("/Organizador/Solicitar", html);
        Assert.DoesNotContain("wa.me", html);   // sin número configurado no se muestra WhatsApp
        Assert.Contains("/Contacto", await TextoAsync(await _app.Cliente().GetAsync("/")));
        // Sin cuenta organizadora se explica qué falta para pedir personal, en vez de un botón que no lleva a ningún lado
        Assert.Contains("data-dato=\"pasos-personal\"", html);
        Assert.DoesNotContain("/Personal", html);
    }

    [Fact]
    public async Task El_administrador_no_ve_el_formulario_para_ser_organizador()
    {
        var html = await TextoAsync(await (await AdminAsync()).GetAsync("/Organizador/Solicitar"));

        Assert.Contains("Tu cuenta ya es organizadora", html);
        Assert.Contains("/Personal", html);
        Assert.DoesNotContain("Enviar solicitud", html);
    }

    [Fact]
    public async Task Solo_el_administrador_responde_solicitudes_y_solo_organizadores_piden_personal()
    {
        var (usuario, _, _) = await UsuarioAsync();

        Assert.StartsWith("/Auth/AccesoDenegado", (await usuario.GetAsync("/Solicitudes")).Headers.Location!.PathAndQuery);
        Assert.StartsWith("/Auth/AccesoDenegado", (await usuario.GetAsync("/Personal")).Headers.Location!.PathAndQuery);
        Assert.StartsWith("/Auth/Login", (await _app.Cliente().GetAsync("/Solicitudes")).Headers.Location!.PathAndQuery);
    }
}

// Validación de los datos de la solicitud (sin base de datos)
public class HU31ValidacionTests
{
    private static SolicitudOrganizadorInput Datos(TipoOrganizador tipo, string documento, string telefono = "987654321") => new()
    {
        Tipo = tipo, NombreOrganizador = "X", Documento = documento, Telefono = telefono, DescripcionEventos = new string('x', 40),
        PlazoSolicitado = PlazoOrganizador.Indefinido, AceptaCondiciones = true
    };

    [Theory]
    [InlineData(TipoOrganizador.PersonaNatural, "12345678", null)]
    [InlineData(TipoOrganizador.Empresa, "20123456789", null)]
    [InlineData(TipoOrganizador.Empresa, "12345678901", "El RUC debe tener 11 dígitos y empezar con 10, 15, 17 o 20.")]
    [InlineData(TipoOrganizador.Empresa, "12345678", "El RUC debe tener 11 dígitos y empezar con 10, 15, 17 o 20.")]
    [InlineData(TipoOrganizador.PersonaNatural, "1234567a", "El DNI debe tener 8 dígitos.")]
    public void Valida_DNI_y_RUC(TipoOrganizador tipo, string documento, string? error) =>
        Assert.Equal(error, SolicitudOrganizadorService.Validar(Datos(tipo, documento)));

    [Theory]
    [InlineData("987654321", true)]
    [InlineData("+51 987 654 321", true)]
    [InlineData("051987654321", false)]
    [InlineData("12345", false)]
    public void Valida_el_celular(string telefono, bool valido) =>
        Assert.Equal(valido, SolicitudOrganizadorService.Validar(Datos(TipoOrganizador.PersonaNatural, "12345678", telefono)) == null);

    [Fact]
    public void Sin_aceptar_las_condiciones_no_se_envia()
    {
        var d = Datos(TipoOrganizador.PersonaNatural, "12345678");
        d.AceptaCondiciones = false;
        Assert.Equal("Debes aceptar las condiciones del organizador.", SolicitudOrganizadorService.Validar(d));
        Assert.Equal(new[] { "a@x.pe", "b@x.pe" }, SolicitudPersonalService.SepararCorreos("A@x.pe; b@x.pe,  a@x.pe"));
    }
}
