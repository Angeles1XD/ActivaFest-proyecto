using System.Net;
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
// 🚫 HU15: AVISO POR CORREO CUANDO EL EVENTO SE CANCELA (pruebas de integración)
// =============================
// El aviso por cambio de fecha/lugar se prueba en Sprint4Tests (EventoEdicionService) y el de compra en HU07.
public class HU15CancelacionTests : IClassFixture<AppDePrueba>
{
    private const string Motivo = "El artista principal sufrió un accidente y no podrá presentarse.";

    private readonly AppDePrueba _app;

    public HU15CancelacionTests(AppDePrueba app)
    {
        _app = app;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string paginaToken, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, paginaToken)));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    private T Con<T>(Func<ApplicationDbContext, T> consulta)
    {
        using var scope = _app.Services.CreateScope();
        return consulta(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    // Un organizador con un evento publicado
    private async Task<(HttpClient Organizador, string Email, int EventoId, int ZonaId)> EventoPublicadoAsync(EstadoEvento estado = EstadoEvento.Aprobado)
    {
        var organizador = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(organizador, email, rol: Roles.Organizador);

        using var scope = _app.Services.CreateScope();
        var usuario = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var evento = new Evento
        {
            Titulo = $"Concierto {Guid.NewGuid():N}", Descripcion = "Concierto que se cancelará en la prueba.", CategoriaId = 1, Ubicacion = "Arequipa",
            Fecha = DateTime.Now.AddDays(12), Estado = estado, OrganizadorId = usuario!.Id, Aforo = 30,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 50m, Capacidad = 30 } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        return (organizador, email, evento.Id, evento.TiposEntrada.First().Id);
    }

    // Compra completa con la pasarela simulada; si pagar = false la deja reservada sin pagar
    private async Task<(string Email, string Codigo)> CompradorAsync(int eventoId, int zonaId, int cantidad, bool pagar = true)
    {
        var cliente = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(cliente, email);

        var checkout = (await PostAsync(cliente, $"/Eventos/Detalle/{eventoId}", "/Compras/Iniciar",
            ("eventoId", eventoId.ToString()), ($"cantidades[{zonaId}]", cantidad.ToString()))).Headers.Location!.OriginalString;
        var codigo = checkout.Split('=')[1];
        if (pagar)
        {
            var pasarela = (await PostAsync(cliente, checkout, "/Compras/Pagar", ("codigo", codigo))).Headers.Location!.OriginalString;
            await PostAsync(cliente, pasarela, "/Compras/PasarelaSimulada", ("codigo", codigo), ("aprobar", "true"));
        }
        return (email, codigo);
    }

    [Fact]
    public async Task Al_cancelar_se_anulan_las_entradas_y_se_avisa_por_correo_a_cada_comprador()
    {
        var (organizador, emailOrganizador, eventoId, zonaId) = await EventoPublicadoAsync();
        var (ana, ordenAna) = await CompradorAsync(eventoId, zonaId, 2);
        var (beto, _) = await CompradorAsync(eventoId, zonaId, 1);
        var (carla, ordenCarla) = await CompradorAsync(eventoId, zonaId, 3, pagar: false);

        var r = await PostAsync(organizador, $"/Eventos/Cancelar/{eventoId}", $"/Eventos/Cancelar/{eventoId}", ("motivo", Motivo));
        Assert.Equal("/Eventos/MisEventos", r.Headers.Location!.OriginalString);

        // Estados según los diagramas: Evento Cancelado, Entradas Anuladas
        Assert.Equal(EstadoEvento.Cancelado, Con(db => db.Eventos.First(e => e.Id == eventoId).Estado));
        Assert.All(Con(db => db.Entradas.Where(e => e.EventoId == eventoId).ToList()), e => Assert.Equal(EstadoEntrada.Anulada, e.Estado));
        Assert.Equal(EstadoOrden.RequiereReembolso, Con(db => db.Ordenes.First(o => o.Codigo == ordenAna).Estado));
        Assert.Equal(EstadoOrden.Cancelada, Con(db => db.Ordenes.First(o => o.Codigo == ordenCarla).Estado));
        Assert.Equal(0, Con(db => db.TiposEntrada.First(t => t.Id == zonaId).Reservadas));

        // Correo con el motivo a cada comprador (no a quien no pagó) y al organizador
        foreach (var email in new[] { ana, beto })
        {
            var correo = Assert.Single(_app.Correos.Para(email), c => c.Asunto.StartsWith("Evento cancelado"));
            Assert.Contains(Motivo, WebUtility.HtmlDecode(correo.Html));
        }
        Assert.DoesNotContain(_app.Correos.Para(carla), c => c.Asunto.StartsWith("Evento cancelado"));
        Assert.Contains(_app.Correos.Para(emailOrganizador), c => c.Asunto.StartsWith("Cancelaste el evento"));

        // Fuera del catálogo; la ficha avisa la cancelación y no vende
        var publico = _app.Cliente();
        Assert.DoesNotContain($"/Eventos/Detalle/{eventoId}\"", await publico.GetStringAsync("/Eventos"));
        var ficha = await TextoAsync(await publico.GetAsync($"/Eventos/Detalle/{eventoId}"));
        Assert.Contains("Este evento fue cancelado", ficha);
        Assert.DoesNotContain("Inicia sesión para comprar", ficha);
    }

    [Fact]
    public async Task Una_entrada_anulada_no_deja_pasar_en_la_puerta()
    {
        var (organizador, _, eventoId, zonaId) = await EventoPublicadoAsync();
        await CompradorAsync(eventoId, zonaId, 1);
        await PostAsync(organizador, $"/Eventos/Cancelar/{eventoId}", $"/Eventos/Cancelar/{eventoId}", ("motivo", Motivo));

        using var scope = _app.Services.CreateScope();
        var codigo = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Entradas.First(e => e.EventoId == eventoId).Codigo;
        var qr = scope.ServiceProvider.GetRequiredService<IQrEntradaService>().CrearContenido(codigo);
        var resultado = await scope.ServiceProvider.GetRequiredService<IAccesoService>().ValidarAsync(eventoId, qr, "puerta", DateTime.Now);

        Assert.False(resultado.PermiteIngreso);
    }

    [Fact]
    public async Task Sin_motivo_no_se_cancela()
    {
        var (organizador, _, eventoId, _) = await EventoPublicadoAsync();

        await PostAsync(organizador, $"/Eventos/Cancelar/{eventoId}", $"/Eventos/Cancelar/{eventoId}", ("motivo", "   "));

        Assert.Equal(EstadoEvento.Aprobado, Con(db => db.Eventos.First(e => e.Id == eventoId).Estado));
    }

    [Fact]
    public async Task Solo_su_organizador_o_un_administrador_pueden_cancelarlo()
    {
        var (_, _, eventoId, _) = await EventoPublicadoAsync();
        var (otroOrganizador, _, _, _) = await EventoPublicadoAsync();

        var r = await PostAsync(otroOrganizador, "/Eventos/MisEventos", $"/Eventos/Cancelar/{eventoId}", ("motivo", Motivo));

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(EstadoEvento.Aprobado, Con(db => db.Eventos.First(e => e.Id == eventoId).Estado));
    }

    [Theory]
    [InlineData(EstadoEvento.Pendiente)]
    [InlineData(EstadoEvento.Rechazado)]
    public async Task Solo_se_cancela_un_evento_publicado(EstadoEvento estado)
    {
        var (organizador, _, eventoId, _) = await EventoPublicadoAsync(estado);

        await PostAsync(organizador, "/Eventos/MisEventos", $"/Eventos/Cancelar/{eventoId}", ("motivo", Motivo));

        Assert.Equal(estado, Con(db => db.Eventos.First(e => e.Id == eventoId).Estado));
    }

    [Fact]
    public async Task Un_evento_cancelado_no_se_puede_editar_ni_volver_a_revision()
    {
        var (organizador, _, eventoId, _) = await EventoPublicadoAsync();
        await PostAsync(organizador, $"/Eventos/Cancelar/{eventoId}", $"/Eventos/Cancelar/{eventoId}", ("motivo", Motivo));

        var r = await organizador.GetAsync($"/Eventos/Editar/{eventoId}");

        Assert.Equal("/Eventos/MisEventos", r.Headers.Location!.OriginalString);
        Assert.Equal(EstadoEvento.Cancelado, Con(db => db.Eventos.First(e => e.Id == eventoId).Estado));
    }

    [Fact]
    public async Task Un_pago_que_llega_despues_de_cancelar_no_emite_entradas()
    {
        var (organizador, _, eventoId, zonaId) = await EventoPublicadoAsync();
        var (_, codigo) = await CompradorAsync(eventoId, zonaId, 2, pagar: false);
        await PostAsync(organizador, $"/Eventos/Cancelar/{eventoId}", $"/Eventos/Cancelar/{eventoId}", ("motivo", Motivo));

        using var scope = _app.Services.CreateScope();
        var compras = scope.ServiceProvider.GetRequiredService<ActivaFest.Services.Compras.ICompraService>();
        var total = Con(db => db.Ordenes.First(o => o.Codigo == codigo).Total);
        await compras.ConfirmarPagoAsync(codigo, "MercadoPago", "999", total, "https://x/Entradas");

        Assert.Equal(EstadoOrden.RequiereReembolso, Con(db => db.Ordenes.First(o => o.Codigo == codigo).Estado));
        Assert.Equal(0, Con(db => db.Entradas.Count(e => e.EventoId == eventoId)));
    }
}
