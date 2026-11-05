using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Acceso;
using ActivaFest.Services.Entradas;
using ActivaFest.Services.Organizadores;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// ⭐ HU33: CALIFICACIÓN DEL PERSONAL, APROBACIÓN AUTOMÁTICA Y FIN DEL CONTROL DE ACCESO
// =============================
public class HU33PersonalAutomaticoTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU33PersonalAutomaticoTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<T> ConAsync<T>(Func<IServiceProvider, ApplicationDbContext, Task<T>> accion)
    {
        using var scope = _app.Services.CreateScope();
        return await accion(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static UserManager<ApplicationUser> Usuarios(IServiceProvider sp) => sp.GetRequiredService<UserManager<ApplicationUser>>();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        return admin;
    }

    // Cuenta creada directamente (sin sesión), con rol opcional
    private Task<(string Id, string Email)> CuentaAsync(string? rol = null) => ConAsync(async (sp, _) =>
    {
        var usuario = new ApplicationUser { UserName = Correo(), NombreCompleto = "Persona de prueba" };
        usuario.Email = usuario.UserName;
        await Usuarios(sp).CreateAsync(usuario, "Clave1234");
        if (rol != null)
            await Usuarios(sp).AddToRoleAsync(usuario, rol);
        return (usuario.Id, usuario.Email!);
    });

    private Task<int> EventoAsync(DateTime fecha, string? organizadorId = null) => ConAsync(async (_, db) =>
    {
        var ev = new Evento
        {
            Titulo = $"Evento {Guid.NewGuid():N}"[..16], Descripcion = "Evento para probar el personal.", CategoriaId = 1, Ubicacion = "Arequipa",
            Fecha = fecha, Estado = EstadoEvento.Aprobado, OrganizadorId = organizadorId, Aforo = 100,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 20, Capacidad = 100 } }
        };
        db.Eventos.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    });

    // Entrada ya usada: la escaneó "personalId" hace "horasDesdeUso" horas
    private Task<int> EntradaUsadaAsync(string compradorId, int eventoId, string personalId, double horasDesdeUso = 1) => ConAsync(async (_, db) =>
    {
        var tipo = await db.TiposEntrada.FirstAsync(t => t.EventoId == eventoId);
        var orden = new Orden
        {
            Codigo = Guid.NewGuid().ToString("N")[..12], UsuarioId = compradorId, EventoId = eventoId, Estado = EstadoOrden.Pagada,
            Total = 20, FechaPago = DateTime.UtcNow, FechaExpiracion = DateTime.UtcNow,
            Items = { new OrdenItem { TipoEntradaId = tipo.Id, Cantidad = 1, PrecioUnitario = 20 } }
        };
        db.Ordenes.Add(orden);
        await db.SaveChangesAsync();
        var entrada = new Entrada
        {
            Codigo = CodigoSeguro.Generar(), OrdenId = orden.Id, EventoId = eventoId, TipoEntradaId = tipo.Id, UsuarioId = compradorId,
            Estado = EstadoEntrada.Usada, FechaEmision = DateTime.UtcNow, FechaUso = DateTime.Now.AddHours(-horasDesdeUso), ValidadoPorId = personalId
        };
        db.Entradas.Add(entrada);
        await db.SaveChangesAsync();
        return entrada.Id;
    });

    // Varias calificaciones para armar la reputación de una persona del personal
    private async Task CalificarVariasAsync(string personalId, params int[] puntajes)
    {
        var eventoId = await EventoAsync(DateTime.Now.AddDays(-1));
        foreach (var puntaje in puntajes)
        {
            var (compradorId, _) = await CuentaAsync();
            var entradaId = await EntradaUsadaAsync(compradorId, eventoId, personalId);
            await ConAsync(async (_, db) =>
            {
                db.CalificacionesPersonal.Add(new CalificacionPersonal
                {
                    EntradaId = entradaId, EventoId = eventoId, PersonalId = personalId, UsuarioId = compradorId, Puntaje = puntaje,
                    Comentario = puntaje <= 2 ? "Nos gritó en la fila." : "Muy amable."
                });
                return await db.SaveChangesAsync();
            });
        }
    }

    private Task<int> SolicitudAsync(int eventoId, string organizadorId, params string[] correos) => ConAsync(async (_, db) =>
    {
        var s = new SolicitudPersonal { OrganizadorId = organizadorId, EventoId = eventoId, Correos = string.Join(", ", correos) };
        db.SolicitudesPersonal.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    });

    private Task<SolicitudPersonal> LeerSolicitudAsync(int id) =>
        ConAsync((_, db) => db.SolicitudesPersonal.AsNoTracking().SingleAsync(s => s.Id == id));

    private Task<bool> AsignadoAsync(string usuarioId, int eventoId) =>
        ConAsync((_, db) => db.AsignacionesAcceso.AnyAsync(a => a.UsuarioId == usuarioId && a.EventoId == eventoId && a.FinalizadaEn == null));

    [Theory]
    [InlineData(4, 1.0, 4, false)]   // pocas calificaciones: todavía no se juzga
    [InlineData(5, 2.4, 1, true)]    // promedio bajo
    [InlineData(10, 3.5, 4, true)]   // 40 % de reportes
    [InlineData(10, 4.2, 1, false)]
    public void La_mala_reputacion_requiere_varias_calificaciones_y_promedio_bajo_o_muchos_reportes(int total, double promedio, int reportes, bool mala) =>
        Assert.Equal(mala, ReputacionPersonalService.EsMalaFama(total, promedio, reportes));

    [Fact]
    public async Task El_asistente_califica_una_vez_la_atencion_en_la_puerta_y_de_forma_anonima()
    {
        var asistente = _app.Cliente();
        var correo = Correo();
        await RegistrarAsync(asistente, correo);
        var asistenteId = await ConAsync(async (sp, _) => (await Usuarios(sp).FindByEmailAsync(correo))!.Id);
        var (personalId, _) = await CuentaAsync(Roles.ControlAcceso);
        var eventoId = await EventoAsync(DateTime.Now.AddHours(-3));
        var entradaId = await EntradaUsadaAsync(asistenteId, eventoId, personalId);

        Assert.Contains("data-dato=\"calificar\"", await TextoAsync(await asistente.GetAsync("/Entradas")));

        await PostAsync(asistente, "/Entradas", $"/Entradas/Calificar/{entradaId}", ("puntaje", "5"), ("comentario", "Muy amable y ordenado"));
        var r = await PostAsync(asistente, "/Entradas", $"/Entradas/Calificar/{entradaId}", ("puntaje", "1"));

        var calificaciones = await ConAsync((_, db) => db.CalificacionesPersonal.Where(c => c.EntradaId == entradaId).ToListAsync());
        var unica = Assert.Single(calificaciones);
        Assert.Equal((5, personalId), (unica.Puntaje, unica.PersonalId));
        var pagina = await TextoAsync(await asistente.GetAsync("/Entradas"));
        Assert.Contains("data-dato=\"calificacion-dada\"", pagina);
        Assert.DoesNotContain("data-dato=\"calificar\"", pagina);

        // Otro usuario no puede calificar una entrada ajena
        var intruso = _app.Cliente();
        await RegistrarAsync(intruso, Correo());
        var otraEntrada = await EntradaUsadaAsync(asistenteId, eventoId, personalId);
        await PostAsync(intruso, "/Entradas", $"/Entradas/Calificar/{otraEntrada}", ("puntaje", "1"));
        Assert.False(await ConAsync((_, db) => db.CalificacionesPersonal.AnyAsync(c => c.EntradaId == otraEntrada)));
    }

    [Fact]
    public async Task Solo_se_califica_una_entrada_usada_dentro_de_los_7_dias()
    {
        var (compradorId, _) = await CuentaAsync();
        var (personalId, _) = await CuentaAsync(Roles.ControlAcceso);
        var eventoId = await EventoAsync(DateTime.Now.AddDays(-10));
        var vieja = await EntradaUsadaAsync(compradorId, eventoId, personalId, horasDesdeUso: 24 * 8);

        var r = await ConAsync((sp, _) => sp.GetRequiredService<IReputacionPersonalService>().CalificarAsync(compradorId, vieja, 4, null));

        Assert.False(r.Exito);
    }

    [Fact]
    public async Task Una_hora_antes_del_evento_la_solicitud_se_aprueba_sola_y_rechaza_las_cuentas_con_mala_reputacion()
    {
        var (orgId, orgCorreo) = await CuentaAsync(Roles.Organizador);
        var (buenoId, buenoCorreo) = await CuentaAsync();
        var (maloId, maloCorreo) = await CuentaAsync();
        await CalificarVariasAsync(maloId, 1, 1, 2, 1, 3);

        var proximo = await EventoAsync(DateTime.Now.AddMinutes(40), orgId);
        var lejano = await EventoAsync(DateTime.Now.AddDays(3), orgId);
        var mixta = await SolicitudAsync(proximo, orgId, buenoCorreo, maloCorreo, "nadie@noexiste.pe");
        var soloMala = await SolicitudAsync(proximo, orgId, maloCorreo);
        var aTiempo = await SolicitudAsync(lejano, orgId, buenoCorreo);

        await ConAsync((sp, _) => sp.GetRequiredService<ISolicitudPersonalService>()
            .ResolverAutomaticasAsync(DateTime.Now, "http://test/Acceso", "http://test/Personal"));

        var s = await LeerSolicitudAsync(mixta);
        Assert.Equal((EstadoSolicitud.Aprobada, true, null), (s.Estado, s.RespuestaAutomatica, s.RespondidaPorId));
        Assert.True(await AsignadoAsync(buenoId, proximo));
        Assert.False(await AsignadoAsync(maloId, proximo));
        Assert.Contains("mala reputación", s.Observacion);
        Assert.Contains("no tiene cuenta", s.Observacion);
        Assert.Contains(_app.Correos.Para(orgCorreo), c => c.Asunto.Contains("fue aprobado"));

        s = await LeerSolicitudAsync(soloMala);
        Assert.Equal((EstadoSolicitud.Rechazada, true), (s.Estado, s.RespuestaAutomatica));

        // La de un evento que todavía no está cerca espera la respuesta del administrador
        Assert.Equal(EstadoSolicitud.Pendiente, (await LeerSolicitudAsync(aTiempo)).Estado);
    }

    [Fact]
    public async Task El_administrador_ve_la_reputacion_y_decide_manualmente_que_cuentas_habilitar()
    {
        var (orgId, _) = await CuentaAsync(Roles.Organizador);
        var (buenoId, buenoCorreo) = await CuentaAsync();
        var (maloId, maloCorreo) = await CuentaAsync();
        await CalificarVariasAsync(maloId, 1, 2, 1, 1, 2);
        var eventoId = await EventoAsync(DateTime.Now.AddDays(2), orgId);
        var id = await SolicitudAsync(eventoId, orgId, buenoCorreo, maloCorreo);

        var admin = await AdminAsync();
        var panel = await TextoAsync(await admin.GetAsync("/Solicitudes"));
        Assert.Contains("data-dato=\"limite-automatico\"", panel);
        Assert.Contains("data-dato=\"mala-fama\"", panel);
        Assert.Contains($"/Solicitudes/Reputacion/{maloId}", panel);

        // Ficha de reputación: solo administrador y queda auditada
        var ficha = await TextoAsync(await admin.GetAsync($"/Solicitudes/Reputacion/{maloId}"));
        Assert.Contains("Mala reputación", ficha);
        Assert.Contains("Nos gritó en la fila.", ficha);
        Assert.True(await ConAsync((_, db) => db.AuditoriasAnalitica.AnyAsync(a => a.Consulta.Contains(maloCorreo))));

        // Sin marcar ninguna cuenta no se aprueba
        await PostAsync(admin, "/Solicitudes", "/Solicitudes/AprobarPersonal", ("id", id.ToString()), ("conSeleccion", "true"));
        Assert.Equal(EstadoSolicitud.Pendiente, (await LeerSolicitudAsync(id)).Estado);

        // El administrador decide: habilita solo la cuenta con mala reputación (bajo su criterio)
        await PostAsync(admin, "/Solicitudes", "/Solicitudes/AprobarPersonal", ("id", id.ToString()), ("conSeleccion", "true"), ("correos", maloCorreo));
        var s = await LeerSolicitudAsync(id);
        Assert.Equal((EstadoSolicitud.Aprobada, false), (s.Estado, s.RespuestaAutomatica));
        Assert.True(await AsignadoAsync(maloId, eventoId));
        Assert.False(await AsignadoAsync(buenoId, eventoId));
        Assert.Contains("no elegida", s.Observacion);
    }

    [Fact]
    public async Task Ni_el_organizador_ni_el_personal_pueden_ver_la_reputacion()
    {
        var (personalId, _) = await CuentaAsync(Roles.ControlAcceso);
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);

        var r = await organizador.GetAsync($"/Solicitudes/Reputacion/{personalId}");

        Assert.StartsWith("/Auth/AccesoDenegado", r.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Al_terminar_el_evento_el_control_de_acceso_se_desactiva_solo()
    {
        var (orgId, _) = await CuentaAsync(Roles.Organizador);
        var (soloEseId, soloEseCorreo) = await CuentaAsync(Roles.ControlAcceso);
        var (conOtroId, _) = await CuentaAsync(Roles.ControlAcceso);
        var terminado = await EventoAsync(DateTime.Now.AddHours(-(Evento.HorasValidezEntradas + 1)), orgId);
        var futuro = await EventoAsync(DateTime.Now.AddDays(4), orgId);
        var solicitud = await SolicitudAsync(terminado, orgId, soloEseCorreo);
        await ConAsync(async (_, db) =>
        {
            db.AsignacionesAcceso.AddRange(
                new AsignacionAcceso { UsuarioId = soloEseId, EventoId = terminado },
                new AsignacionAcceso { UsuarioId = conOtroId, EventoId = terminado },
                new AsignacionAcceso { UsuarioId = conOtroId, EventoId = futuro });
            var s = await db.SolicitudesPersonal.SingleAsync(x => x.Id == solicitud);
            s.Estado = EstadoSolicitud.Aprobada;
            return await db.SaveChangesAsync();
        });

        await ConAsync((sp, _) => sp.GetRequiredService<ISolicitudPersonalService>().FinalizarAsignacionesAsync(DateTime.Now));

        Assert.False(await AsignadoAsync(soloEseId, terminado));
        Assert.True(await ConAsync((_, db) => db.AsignacionesAcceso.AnyAsync(a => a.UsuarioId == soloEseId && a.FinalizadaEn != null)));   // queda como historial
        Assert.True(await AsignadoAsync(conOtroId, futuro));
        var roles = await ConAsync(async (sp, _) => (
            Solo: await Usuarios(sp).IsInRoleAsync((await Usuarios(sp).FindByIdAsync(soloEseId))!, Roles.ControlAcceso),
            Otro: await Usuarios(sp).IsInRoleAsync((await Usuarios(sp).FindByIdAsync(conOtroId))!, Roles.ControlAcceso)));
        Assert.Equal((false, true), roles);

        // El organizador ve que el turno terminó
        var habilitados = await ConAsync(async (sp, db) => await sp.GetRequiredService<ISolicitudPersonalService>()
            .HabilitadosAsync(await db.SolicitudesPersonal.Where(s => s.Id == solicitud).ToListAsync()));
        Assert.False(habilitados[(solicitud, soloEseCorreo)]);
    }
}
