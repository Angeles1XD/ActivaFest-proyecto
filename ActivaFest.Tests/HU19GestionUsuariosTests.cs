using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 👥 HU19: CUENTAS, ROLES Y PERSONAL DE CONTROL (pruebas de integración)
// =============================
public class HU19GestionUsuariosTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU19GestionUsuariosTests(AppDePrueba app)
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
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, "/Usuarios")));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    private async Task<T> ConAsync<T>(Func<UserManager<ApplicationUser>, ApplicationDbContext, Task<T>> accion)
    {
        using var scope = _app.Services.CreateScope();
        return await accion(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
                            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private Task<ApplicationUser> UsuarioAsync(string email) => ConAsync((u, _) => u.FindByEmailAsync(email))!;

    private Task<int> EventoAprobadoAsync() => ConAsync(async (u, db) =>
    {
        var admin = (await u.FindByEmailAsync("admin@test.com"))!;
        var evento = new Evento
        {
            Titulo = "Concierto de prueba", Descripcion = "Evento para probar el control de acceso.", CategoriaId = 1,
            Ubicacion = "Estadio", Fecha = DateTime.Now.AddDays(5), Estado = EstadoEvento.Aprobado, OrganizadorId = admin.Id,
            Aforo = 100, TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 20, Capacidad = 100 } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        return evento.Id;
    });

    [Fact]
    public async Task El_administrador_no_puede_quitarse_a_si_mismo_el_rol_Administrador()
    {
        var admin = await AdminAsync();
        var yo = await UsuarioAsync("admin@test.com");

        await PostAsync(admin, "/Usuarios/CambiarRol", ("usuarioId", yo.Id), ("rol", Roles.Administrador), ("asignar", "false"));

        Assert.True(await ConAsync((u, _) => u.IsInRoleAsync(yo, Roles.Administrador)));
    }

    [Fact]
    public async Task El_administrador_no_puede_desactivar_su_propia_cuenta()
    {
        var admin = await AdminAsync();
        var yo = await UsuarioAsync("admin@test.com");

        await PostAsync(admin, "/Usuarios/CambiarEstado", ("usuarioId", yo.Id), ("activo", "false"));

        Assert.True((await UsuarioAsync("admin@test.com")).Activo);
    }

    [Fact]
    public async Task Puede_dar_y_quitar_roles_a_otros_usuarios_incluido_Administrador()
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);
        var otro = await UsuarioAsync(email);
        var admin = await AdminAsync();

        await PostAsync(admin, "/Usuarios/CambiarRol", ("usuarioId", otro.Id), ("rol", Roles.Administrador), ("asignar", "true"));
        Assert.True(await ConAsync((u, _) => u.IsInRoleAsync(otro, Roles.Administrador)));

        await PostAsync(admin, "/Usuarios/CambiarRol", ("usuarioId", otro.Id), ("rol", Roles.Administrador), ("asignar", "false"));
        Assert.False(await ConAsync((u, _) => u.IsInRoleAsync(otro, Roles.Administrador)));
    }

    [Fact]
    public async Task Un_usuario_desactivado_no_puede_iniciar_sesion_y_su_sesion_se_invalida()
    {
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);
        var antes = await UsuarioAsync(email);
        var admin = await AdminAsync();

        await PostAsync(admin, "/Usuarios/CambiarEstado", ("usuarioId", antes.Id), ("activo", "false"));

        var despues = await UsuarioAsync(email);
        Assert.False(despues.Activo);
        Assert.NotEqual(antes.SecurityStamp, despues.SecurityStamp); // cierra sus sesiones abiertas
        Assert.Equal(HttpStatusCode.OK, (await IniciarSesionAsync(_app.Cliente(), email, "Clave1234")).StatusCode);

        // Al reactivarla vuelve a entrar
        await PostAsync(admin, "/Usuarios/CambiarEstado", ("usuarioId", antes.Id), ("activo", "true"));
        Assert.Equal(HttpStatusCode.Redirect, (await IniciarSesionAsync(_app.Cliente(), email, "Clave1234")).StatusCode);
    }

    [Fact]
    public async Task El_personal_de_control_solo_escanea_los_eventos_que_se_le_asignan()
    {
        var eventoId = await EventoAprobadoAsync();
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);
        var personal = await UsuarioAsync(email);
        var admin = await AdminAsync();
        await PostAsync(admin, "/Usuarios/CambiarRol", ("usuarioId", personal.Id), ("rol", Roles.ControlAcceso), ("asignar", "true"));

        var puerta = _app.Cliente();
        await IniciarSesionAsync(puerta, email, "Clave1234");
        Assert.Equal(HttpStatusCode.NotFound, (await puerta.GetAsync($"/Acceso/Escanear/{eventoId}")).StatusCode);

        await PostAsync(admin, $"/Usuarios/Asignaciones/{personal.Id}", ("eventos", eventoId.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await puerta.GetAsync($"/Acceso/Escanear/{eventoId}")).StatusCode);

        // Al desmarcarlo pierde el acceso
        await PostAsync(admin, $"/Usuarios/Asignaciones/{personal.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await puerta.GetAsync($"/Acceso/Escanear/{eventoId}")).StatusCode);
    }

    // Con muchos eventos se busca por nombre y se asigna o quita de a uno, sin perder las demás asignaciones
    [Fact]
    public async Task Con_muchos_eventos_se_busca_por_nombre_y_se_asigna_sin_perder_los_demas()
    {
        var marca = Guid.NewGuid().ToString("N")[..6];
        var ids = await ConAsync(async (u, db) =>
        {
            var admin = (await u.FindByEmailAsync("admin@test.com"))!;
            var eventos = Enumerable.Range(1, 25).Select(i => new Evento
            {
                Titulo = i == 25 ? $"Gala Única {marca}" : $"Feria {marca} {i:D2}", Descripcion = "Evento para probar el buscador.", CategoriaId = 1,
                Ubicacion = "Arequipa", Fecha = DateTime.Now.AddDays(3 + i), Estado = EstadoEvento.Aprobado, OrganizadorId = admin.Id,
                Aforo = 10, TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 10, Capacidad = 10 } }
            }).ToList();
            db.Eventos.AddRange(eventos);
            await db.SaveChangesAsync();
            return eventos.Select(e => e.Id).ToList();
        });
        var email = Correo();
        await RegistrarAsync(_app.Cliente(), email);
        var personal = await UsuarioAsync(email);
        var admin = await AdminAsync();
        await PostAsync(admin, "/Usuarios/CambiarRol", ("usuarioId", personal.Id), ("rol", Roles.ControlAcceso), ("asignar", "true"));
        await PostAsync(admin, "/Usuarios/Asignar", ("id", personal.Id), ("eventoId", ids[0].ToString()));

        // La búsqueda por nombre encuentra el evento entre todos
        var html = await TextoAsync(await admin.GetAsync($"/Usuarios/Asignaciones/{personal.Id}?buscar={Uri.EscapeDataString("Gala Única " + marca)}"));
        Assert.Contains("Resultados para «Gala Única " + marca + "»: 1 evento", html);
        Assert.Contains($"name=\"eventoId\" value=\"{ids[24]}\"", html);

        // Por nombre parcial hay 25 resultados: 20 por página
        var lista = await TextoAsync(await admin.GetAsync($"/Usuarios/Asignaciones/{personal.Id}?buscar={marca}"));
        Assert.Contains($"«{marca}»: 25 eventos", lista);
        Assert.Contains("Página 1 de 2", lista);

        // Asignar desde la búsqueda no quita el evento que ya tenía
        await PostAsync(admin, "/Usuarios/Asignar", ("id", personal.Id), ("eventoId", ids[24].ToString()));
        Assert.Equal(new[] { ids[0], ids[24] }, (await ConAsync((u, db) => Task.FromResult(db.AsignacionesAcceso.Where(a => a.UsuarioId == personal.Id).Select(a => a.EventoId).OrderBy(x => x).ToList()))));

        await PostAsync(admin, "/Usuarios/Quitar", ("id", personal.Id), ("eventoId", ids[0].ToString()));
        Assert.Equal(new[] { ids[24] }, await ConAsync((u, db) => Task.FromResult(db.AsignacionesAcceso.Where(a => a.UsuarioId == personal.Id).Select(a => a.EventoId).ToList())));
    }

    [Fact]
    public async Task Solo_el_administrador_gestiona_usuarios()
    {
        var usuario = _app.Cliente();
        await RegistrarAsync(usuario, Correo(), rol: Roles.Organizador);

        Assert.StartsWith("/Auth/AccesoDenegado", (await usuario.GetAsync("/Usuarios")).Headers.Location!.PathAndQuery);
    }
}
