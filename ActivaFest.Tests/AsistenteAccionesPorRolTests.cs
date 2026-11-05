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
// ⚡ ASISTENTE: acciones por rol (integración por /api/asistente-ia)
// =============================
// Usuario: reservar entradas y avisos de eventos próximos. Organizador: estado de sus publicaciones y dashboard de ventas.
public class AsistenteAccionesPorRolTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public AsistenteAccionesPorRolTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<JsonElement> PreguntarAsync(HttpClient cliente, string mensaje, int? eventoId = null, JsonElement? pendiente = null)
    {
        var r = await cliente.PostAsJsonAsync("/api/asistente-ia", new { mensaje, eventoId, pendiente });
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(HttpClient Cliente, string Id, string Email)> UsuarioAsync(string rol = Roles.Asistente)
    {
        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email, rol: rol);
        var id = Con(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).Single());
        return (cliente, id, email);
    }

    private T Con<T>(Func<ApplicationDbContext, T> accion)
    {
        using var scope = _app.Services.CreateScope();
        return accion(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    // Título con una palabra única para que el asistente lo reconozca sin confundirlo con otros eventos
    private static string Titulo(string nombre) => $"{nombre} {Guid.NewGuid():N}"[..(nombre.Length + 9)];

    private Evento Evento(string titulo, DateTime fecha, EstadoEvento estado = EstadoEvento.Aprobado, string? organizadorId = null) => Con(db =>
    {
        var e = EntradasDePrueba.Evento(db, titulo, PlantillaPlano.CampoAbierto, fecha, estado);
        if (organizadorId != null)
        {
            db.Eventos.Single(x => x.Id == e.Id).OrganizadorId = organizadorId;
            db.SaveChanges();
        }
        return e;
    });

    private static string[] Sugerencias(JsonElement r) => r.GetProperty("sugerencias").EnumerateArray().Select(s => s.GetString()!).ToArray();
    private static string[] Urls(JsonElement r) => r.GetProperty("enlaces").EnumerateArray().Select(e => e.GetProperty("url").GetString()!).ToArray();

    // ---------- Usuario: reservas ----------
    [Fact]
    public async Task Sin_sesion_pide_iniciar_sesion_para_reservar()
    {
        var r = await PreguntarAsync(_app.Cliente(), "Resérvame 2 entradas");

        Assert.Equal(Intenciones.Reservar, r.GetProperty("intencion").GetString());
        Assert.Contains("inicia sesión", r.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task Reserva_la_zona_y_cantidad_pedidas_y_da_el_enlace_para_pagar()
    {
        var (ana, anaId, _) = await UsuarioAsync();
        var titulo = Titulo("Rockfest");
        var evento = Evento(titulo, DateTime.Today.AddDays(5).AddHours(20));

        var resumen = await PreguntarAsync(ana, $"Resérvame dos entradas VIP para {titulo}");
        var r = await PreguntarAsync(ana, "Sí, reservar", pendiente: resumen.GetProperty("pendiente"));

        Assert.Equal(Intenciones.Reservar, r.GetProperty("intencion").GetString());
        Assert.Contains("Reservé 2 entradas VIP", r.GetProperty("reply").GetString());
        var orden = Con(db => db.Ordenes.Include(o => o.Items).ThenInclude(i => i.TipoEntrada).Single(o => o.UsuarioId == anaId && o.EventoId == evento.Id));
        Assert.Equal(EstadoOrden.PendientePago, orden.Estado);
        Assert.Equal(240m, orden.Total);
        Assert.Equal(("VIP", 2), (orden.Items.Single().TipoEntrada!.Nombre, orden.Items.Single().Cantidad));
        Assert.Contains($"/Compras/Checkout?codigo={orden.Codigo}", Urls(r));
    }

    [Fact]
    public async Task Sin_zona_pregunta_cual_y_sugiere_las_disponibles()
    {
        var (ana, anaId, _) = await UsuarioAsync();
        var titulo = Titulo("Feriazo");
        var evento = Evento(titulo, DateTime.Today.AddDays(6).AddHours(18));

        var r = await PreguntarAsync(ana, $"Quiero reservar 3 entradas para {titulo}");

        Assert.Contains("¿Qué tipo de entrada", r.GetProperty("reply").GetString());
        Assert.Contains(Sugerencias(r), s => s.StartsWith("VIP"));
        Assert.False(Con(db => db.Ordenes.Any(o => o.UsuarioId == anaId && o.EventoId == evento.Id)));
    }

    [Theory]
    [InlineData("reservame 2 entradas vip", 2)]
    [InlineData("apartame tres boletos", 3)]
    [InlineData("una entrada general", 1)]
    [InlineData("reserva para el sabado", null)]
    public void Entiende_la_cantidad_pedida(string texto, int? esperado) =>
        Assert.Equal(esperado, AsistenteService.CantidadMencionada(texto));

    // ---------- Usuario: avisos de eventos próximos ----------
    [Fact]
    public async Task Activa_un_aviso_lo_lista_y_lo_envia_por_correo_cuando_falta_poco()
    {
        var (ana, anaId, email) = await UsuarioAsync();
        var pronto = Titulo("Salsodromo");
        var lejos = Titulo("Jazzeando");
        var eventoPronto = Evento(pronto, DateTime.Now.AddHours(10));
        Evento(lejos, DateTime.Now.AddDays(10));

        var r = await PreguntarAsync(ana, $"Recuérdame {pronto}");
        await PreguntarAsync(ana, $"Avísame de {lejos}");

        Assert.Equal(Intenciones.Recordatorio, r.GetProperty("intencion").GetString());
        Assert.Contains("te avisaré", r.GetProperty("reply").GetString());
        Assert.Equal(2, Con(db => db.RecordatoriosEvento.Count(x => x.UsuarioId == anaId)));

        var avisos = await PreguntarAsync(ana, "Mis avisos");
        var texto = avisos.GetProperty("reply").GetString()!;
        Assert.Equal(Intenciones.MisAvisos, avisos.GetProperty("intencion").GetString());
        Assert.True(texto.IndexOf(pronto, StringComparison.Ordinal) < texto.IndexOf(lejos, StringComparison.Ordinal), "el más cercano va primero");
        Assert.Contains("🔔 aviso activo", texto);

        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAccionesAsistente>().EnviarRecordatoriosAsync();

        var correos = _app.Correos.Para(email);
        Assert.Single(correos, c => c.Asunto.Contains(pronto));
        Assert.DoesNotContain(correos, c => c.Asunto.Contains(lejos));
        Assert.NotNull(Con(db => db.RecordatoriosEvento.Single(x => x.UsuarioId == anaId && x.EventoId == eventoPronto.Id).EnviadoEn));
    }

    [Fact]
    public async Task Quita_un_aviso_y_no_duplica_si_se_pide_dos_veces()
    {
        var (ana, anaId, _) = await UsuarioAsync();
        var titulo = Titulo("Cumbiaton");
        Evento(titulo, DateTime.Now.AddDays(3));

        await PreguntarAsync(ana, $"Recuérdame {titulo}");
        var otraVez = await PreguntarAsync(ana, $"Recuérdame {titulo}");
        Assert.Contains("Ya tenías activado", otraVez.GetProperty("reply").GetString());
        Assert.Equal(1, Con(db => db.RecordatoriosEvento.Count(x => x.UsuarioId == anaId)));

        var quitar = await PreguntarAsync(ana, $"Ya no me avises de {titulo}");
        Assert.Contains("Quité el aviso", quitar.GetProperty("reply").GetString());
        Assert.Equal(0, Con(db => db.RecordatoriosEvento.Count(x => x.UsuarioId == anaId)));
    }

    // ---------- Organizador: publicaciones y ventas ----------
    [Fact]
    public async Task El_organizador_ve_que_eventos_estan_publicados_y_cuales_pendientes()
    {
        var (org, orgId, _) = await UsuarioAsync(Roles.Organizador);
        var publicado = Titulo("Publicadito");
        var pendiente = Titulo("Esperandito");
        Evento(publicado, DateTime.Now.AddDays(8), EstadoEvento.Aprobado, orgId);
        Evento(pendiente, DateTime.Now.AddDays(9), EstadoEvento.Pendiente, orgId);

        var r = await PreguntarAsync(org, "¿Qué eventos tengo pendientes de aprobación?");
        var texto = r.GetProperty("reply").GetString()!;

        Assert.Equal(Intenciones.EstadoMisEventos, r.GetProperty("intencion").GetString());
        Assert.Matches($"✅ Publicados \\(1\\):\\n\\s+• {publicado}", texto);
        Assert.Matches($"⏳ Pendientes de aprobación \\(1\\):\\n\\s+• {pendiente}", texto);
        var kpis = r.GetProperty("dashboard").GetProperty("indicadores").EnumerateArray()
            .ToDictionary(k => k.GetProperty("etiqueta").GetString()!, k => k.GetProperty("valor").GetString());
        Assert.Equal(("1", "1"), (kpis["Publicados"], kpis["Pendientes"]));
    }

    [Fact]
    public async Task El_organizador_recibe_su_dashboard_de_ventas_general_y_por_evento()
    {
        var (org, orgId, _) = await UsuarioAsync(Roles.Organizador);
        var (_, compradorId, _) = await UsuarioAsync();
        var titulo = Titulo("Vendidazo");
        var evento = Evento(titulo, DateTime.Now.AddDays(12), EstadoEvento.Aprobado, orgId);
        Con(db =>
        {
            EntradasDePrueba.Entrada(db, compradorId, evento, "VIP");
            EntradasDePrueba.Entrada(db, compradorId, evento, "General");
            // Los contadores de aforo acompañan a las entradas emitidas (como en una compra real)
            foreach (var t in db.TiposEntrada.Where(t => t.EventoId == evento.Id)) t.Vendidas = 1;
            return db.SaveChanges();
        });

        var general = await PreguntarAsync(org, "Muéstrame mi dashboard de ventas");
        Assert.Equal(Intenciones.VentasOrganizador, general.GetProperty("intencion").GetString());
        Assert.Contains("Llevas 2 entradas vendidas en 1 evento", general.GetProperty("reply").GetString());
        var dashboard = general.GetProperty("dashboard");
        Assert.Equal("📊 Resumen de ventas de tus eventos", dashboard.GetProperty("titulo").GetString());
        Assert.Equal(7, dashboard.GetProperty("serie").GetArrayLength());
        Assert.Contains($"¿Cómo van las ventas de {titulo}?", Sugerencias(general));

        var detalle = await PreguntarAsync(org, $"¿Cómo van las ventas de {titulo}?");
        Assert.Contains($"«{titulo}»: vendiste 2 de 350 entradas", detalle.GetProperty("reply").GetString());
        var zonas = detalle.GetProperty("dashboard").GetProperty("barras").EnumerateArray().Select(b => b.GetProperty("etiqueta").GetString()).ToList();
        Assert.Equal(new[] { "General", "VIP" }, zonas.OrderBy(z => z));
        Assert.Contains($"/Panel/Evento/{evento.Id}", Urls(detalle));
    }

    // ---------- Panel de notificaciones (según el rol) ----------
    private async Task ProcesarAsync()
    {
        using var scope = _app.Services.CreateScope();
        var acciones = scope.ServiceProvider.GetRequiredService<IAccionesAsistente>();
        await acciones.EnviarRecordatoriosAsync();
        await acciones.ProcesarAlertasAsync();
    }

    private static async Task<JsonElement> PanelAsync(HttpClient cliente)
    {
        var r = await cliente.GetAsync("/api/notificaciones");
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(string Titulo, string? Url)> Recibidas(JsonElement panel) =>
        panel.GetProperty("notificaciones").EnumerateArray()
            .Select(n => (n.GetProperty("titulo").GetString()!, n.GetProperty("url").GetString())).ToList();

    [Fact]
    public async Task El_recordatorio_del_usuario_llega_a_su_panel_y_se_marca_como_leido()
    {
        var (ana, _, _) = await UsuarioAsync();
        var (beto, _, _) = await UsuarioAsync();
        var titulo = Titulo("Rumbita");
        var evento = Evento(titulo, DateTime.Now.AddHours(8));

        await PreguntarAsync(ana, $"Recuérdame {titulo}");
        var programado = await PanelAsync(ana);
        Assert.Contains(Recibidas(programado), n => n.Titulo == $"Recordatorio de «{titulo}» programado");

        await ProcesarAsync();
        var panel = await PanelAsync(ana);
        Assert.Contains(Recibidas(panel), n => n.Titulo == $"«{titulo}» ya se acerca" && n.Url == $"/Eventos/Detalle/{evento.Id}");
        Assert.Equal(2, panel.GetProperty("noLeidas").GetInt32());
        Assert.Empty(Recibidas(await PanelAsync(beto))); // cada quien ve solo las suyas

        (await ana.PostAsync("/api/notificaciones/leidas", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await PanelAsync(ana)).GetProperty("noLeidas").GetInt32());
    }

    [Fact]
    public async Task Sin_sesion_no_hay_panel_de_notificaciones()
    {
        var r = await _app.Cliente().GetAsync("/api/notificaciones");
        Assert.NotEqual(System.Net.HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task El_organizador_recibe_el_recordatorio_de_su_evento_con_sus_ventas()
    {
        var (org, orgId, _) = await UsuarioAsync(Roles.Organizador);
        var titulo = Titulo("Miconcierto");
        var evento = Evento(titulo, DateTime.Now.AddHours(6), EstadoEvento.Aprobado, orgId);

        await PreguntarAsync(org, $"Recuérdame {titulo}");
        await ProcesarAsync();

        var panel = await PanelAsync(org);
        Assert.Contains(Recibidas(panel), n => n.Titulo == $"Tu evento «{titulo}» ya se acerca" && n.Url == $"/Panel/Evento/{evento.Id}");
        Assert.Contains("entradas vendidas", panel.GetProperty("notificaciones")[0].GetProperty("mensaje").GetString());
    }

    [Fact]
    public async Task El_organizador_recibe_una_notificacion_cuando_aprueban_su_evento()
    {
        var (org, orgId, _) = await UsuarioAsync(Roles.Organizador);
        var titulo = Titulo("Pendientito");
        var evento = Evento(titulo, DateTime.Now.AddDays(20), EstadoEvento.Pendiente, orgId);

        var r = await PreguntarAsync(org, $"Avísame cuando aprueben {titulo}");
        Assert.Contains("apenas el administrador revise", r.GetProperty("reply").GetString());
        Assert.Contains((await PanelAsync(org)).GetProperty("programadas").EnumerateArray(), p => p.GetProperty("texto").GetString()!.Contains(titulo));

        await ProcesarAsync(); // sigue pendiente: todavía no avisa
        Assert.DoesNotContain(Recibidas(await PanelAsync(org)), n => n.Titulo.StartsWith("¡Aprobaron"));

        Con(db => { db.Eventos.Single(e => e.Id == evento.Id).Estado = EstadoEvento.Aprobado; return db.SaveChanges(); });
        await ProcesarAsync();
        Assert.Contains(Recibidas(await PanelAsync(org)), n => n.Titulo == $"¡Aprobaron tu evento «{titulo}»!");
    }

    [Fact]
    public async Task El_administrador_recibe_una_notificacion_cuando_llegan_eventos_por_aprobar()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");

        var r = await PreguntarAsync(admin, "Avísame cuando haya eventos por aprobar");
        Assert.Contains("cada vez que lleguen eventos por aprobar", r.GetProperty("reply").GetString());
        await ProcesarAsync();

        Evento(Titulo("Nuevecito"), DateTime.Now.AddDays(15), EstadoEvento.Pendiente);
        await ProcesarAsync();

        Assert.Contains(Recibidas(await PanelAsync(admin)), n => n.Titulo.Contains("por aprobar") && n.Url == "/Admin");
        await PreguntarAsync(admin, "Ya no me avises de los eventos por aprobar");
        Assert.False(Con(db => db.AlertasAsistente.Any(a => a.Tipo == TipoAlertaAsistente.EventosPorAprobar && a.Activa)));
    }

    [Fact]
    public async Task El_personal_de_control_recibe_el_recordatorio_de_sus_turnos()
    {
        var (puerta, puertaId, _) = await UsuarioAsync(Roles.ControlAcceso);
        var titulo = Titulo("Turnazo");
        var evento = Evento(titulo, DateTime.Now.AddHours(9));
        Con(db => { db.AsignacionesAcceso.Add(new AsignacionAcceso { UsuarioId = puertaId, EventoId = evento.Id }); return db.SaveChanges(); });

        var r = await PreguntarAsync(puerta, "Recuérdame mis turnos");
        Assert.Contains("programé el recordatorio de tus 1 turno", r.GetProperty("reply").GetString());

        await ProcesarAsync();
        Assert.Contains(Recibidas(await PanelAsync(puerta)), n => n.Titulo == $"Tu turno de control en «{titulo}»" && n.Url == "/Acceso");
    }

    [Fact]
    public async Task Un_usuario_sin_rol_de_organizador_no_ve_ventas()
    {
        var (ana, _, _) = await UsuarioAsync();

        var r = await PreguntarAsync(ana, "¿Cuánto he vendido?");

        Assert.Contains("es para organizadores", r.GetProperty("reply").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("dashboard").ValueKind);
    }
}
