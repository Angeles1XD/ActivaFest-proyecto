using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Entradas;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 📊 HU13: PANEL DE VENTAS DEL ORGANIZADOR (pruebas de integración)
// =============================
public class HU13PanelVentasTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU13PanelVentasTests(AppDePrueba app)
    {
        _app = app;
    }

    // Evento del organizador con 2 compras pagadas (3 entradas) y 1 entrada reservada sin pagar
    private async Task<(HttpClient Organizador, int EventoId)> EventoConVentasAsync()
    {
        var organizador = _app.Cliente();
        var email = Correo();
        await RegistrarAsync(organizador, email, rol: Roles.Organizador);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var dueño = (await usuarios.FindByEmailAsync(email))!;
        var comprador = new ApplicationUser { UserName = Correo(), Email = Correo(), NombreCompleto = "Comprador" };
        await usuarios.CreateAsync(comprador, "Clave1234");

        var evento = new Evento
        {
            Titulo = $"Festival {Guid.NewGuid():N}", Descripcion = "Festival para el panel de ventas.", CategoriaId = 3, Ubicacion = "Arequipa",
            Fecha = DateTime.Now.AddDays(9), Estado = EstadoEvento.Aprobado, OrganizadorId = dueño.Id, Aforo = 60,
            TiposEntrada =
            {
                new TipoEntrada { Nombre = "General", Precio = 40, Capacidad = 50, Vendidas = 2, Reservadas = 1 },
                new TipoEntrada { Nombre = "VIP", Precio = 100, Capacidad = 10, Vendidas = 1 }
            }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();
        var general = evento.TiposEntrada.First(t => t.Nombre == "General");
        var vip = evento.TiposEntrada.First(t => t.Nombre == "VIP");

        Orden Pagada(params (TipoEntrada Zona, int Cantidad)[] items) => new()
        {
            Codigo = Guid.NewGuid().ToString("N")[..12], UsuarioId = comprador.Id, EventoId = evento.Id, Estado = EstadoOrden.Pagada,
            Total = items.Sum(i => i.Zona.Precio * i.Cantidad), FechaPago = DateTime.UtcNow, FechaExpiracion = DateTime.UtcNow,
            Items = items.Select(i => new OrdenItem { TipoEntradaId = i.Zona.Id, Cantidad = i.Cantidad, PrecioUnitario = i.Zona.Precio }).ToList()
        };

        var ordenes = new[] { Pagada((general, 2)), Pagada((vip, 1)) };
        db.Ordenes.AddRange(ordenes);
        await db.SaveChangesAsync();

        db.Entradas.AddRange(ordenes.SelectMany(o => o.Items.SelectMany(i => Enumerable.Range(0, i.Cantidad).Select(_ => new Entrada
        {
            Codigo = CodigoSeguro.Generar(), OrdenId = o.Id, EventoId = evento.Id, TipoEntradaId = i.TipoEntradaId, UsuarioId = comprador.Id,
            Estado = EstadoEntrada.Activa, FechaEmision = DateTime.UtcNow
        }))));
        await db.SaveChangesAsync();
        return (organizador, evento.Id);
    }

    // Criterio: el panel muestra entradas vendidas, ingresos y aforo restante por evento, y coinciden con la BD (OE4)
    [Fact]
    public async Task El_panel_muestra_vendidas_ingresos_y_aforo_restante_que_coinciden_con_la_base_de_datos()
    {
        var (organizador, eventoId) = await EventoConVentasAsync();

        int vendidasBd, reservadasBd, aforoBd;
        decimal ingresosBd;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            vendidasBd = db.Entradas.Count(e => e.EventoId == eventoId && e.Estado != EstadoEntrada.Anulada);
            ingresosBd = db.Ordenes.Where(o => o.EventoId == eventoId && o.Estado == EstadoOrden.Pagada).AsEnumerable().Sum(o => o.Total);
            reservadasBd = db.TiposEntrada.Where(t => t.EventoId == eventoId).Sum(t => t.Reservadas);
            aforoBd = db.TiposEntrada.Where(t => t.EventoId == eventoId).Sum(t => t.Capacidad);
        }

        var html = await TextoAsync(await organizador.GetAsync("/Panel"));
        var fila = html[html.IndexOf("Aforo restante", StringComparison.Ordinal)..];

        Assert.Equal((3, 180m, 1, 60), (vendidasBd, ingresosBd, reservadasBd, aforoBd));
        Assert.Contains($"{vendidasBd} / {aforoBd}", fila);
        Assert.Matches($@"<td class=""derecha num"">{aforoBd - vendidasBd - reservadasBd}</td>", fila); // 56
        Assert.Matches(@"S/\s?180\.00", fila);
    }

    [Fact]
    public async Task Los_graficos_usan_chart_js_con_los_mismos_datos_del_panel()
    {
        var (organizador, eventoId) = await EventoConVentasAsync();

        var html = await organizador.GetStringAsync($"/Panel/Evento/{eventoId}");

        Assert.Contains("chart.js@4.4.1/dist/chart.umd.min.js", html);
        Assert.Contains("id=\"graficoVentas\"", html);
        Assert.Contains("id=\"graficoZonas\"", html);

        var json = Regex.Match(html, @"<script type=""application/json"" id=""datosPanel"">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        var datos = JsonDocument.Parse(json).RootElement;
        var zonas = datos.GetProperty("zonas").EnumerateArray().ToDictionary(z => z.GetProperty("nombre").GetString()!);
        Assert.Equal((2, 1, 47), (zonas["General"].GetProperty("vendidas").GetInt32(), zonas["General"].GetProperty("reservadas").GetInt32(), zonas["General"].GetProperty("disponibles").GetInt32()));
        Assert.Equal(3, datos.GetProperty("dias").EnumerateArray().Sum(d => d.GetProperty("entradas").GetInt32()));
        Assert.Equal(180m, datos.GetProperty("dias").EnumerateArray().Sum(d => d.GetProperty("ingresos").GetDecimal()));
    }

    [Fact]
    public async Task Un_organizador_no_ve_el_panel_de_eventos_ajenos()
    {
        var (_, eventoId) = await EventoConVentasAsync();
        var otro = _app.Cliente();
        await RegistrarAsync(otro, Correo(), rol: Roles.Organizador);

        var r = await otro.GetAsync($"/Panel/Evento/{eventoId}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, r.StatusCode);
    }
}
