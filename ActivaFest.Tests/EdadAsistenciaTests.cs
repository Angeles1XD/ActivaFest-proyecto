using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// 🎂 FECHA DE NACIMIENTO AL REGISTRARSE
// =============================
public class RegistroFechaNacimientoTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public RegistroFechaNacimientoTests(AppDePrueba app) => _app = app;

    private async Task<(HttpResponseMessage Respuesta, ApplicationUser? Usuario)> RegistrarConFechaAsync(string? fecha)
    {
        var cliente = _app.Cliente();
        var email = Correo();
        var datos = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenAsync(cliente, "/Auth/Registro"),
            ["NombreCompleto"] = "Ana Pérez", ["Email"] = email, ["Password"] = "Clave1234", ["ConfirmarPassword"] = "Clave1234",
            ["AceptaTratamientoDatos"] = "true"
        };
        if (fecha != null)
            datos["FechaNacimiento"] = fecha;

        var respuesta = await cliente.PostAsync("/Auth/Registro", new FormUrlEncodedContent(datos));
        using var scope = _app.Services.CreateScope();
        var usuario = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        return (respuesta, usuario);
    }

    [Fact]
    public async Task Guarda_la_fecha_de_nacimiento()
    {
        var (respuesta, usuario) = await RegistrarConFechaAsync("2000-05-20");

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.Equal(new DateTime(2000, 5, 20), usuario!.FechaNacimiento);
    }

    [Fact]
    public async Task Sin_fecha_no_crea_la_cuenta()
    {
        var (respuesta, usuario) = await RegistrarConFechaAsync(null);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("Ingresa tu fecha de nacimiento", await respuesta.Content.ReadAsStringAsync());
        Assert.Null(usuario);
    }

    [Fact]
    public async Task Rechaza_a_quien_no_tiene_la_edad_minima()
    {
        var fecha = DateTime.Today.AddYears(-EdadUsuario.Minima).AddDays(1).ToString("yyyy-MM-dd");
        var (respuesta, usuario) = await RegistrarConFechaAsync(fecha);

        Assert.Contains($"al menos {EdadUsuario.Minima} años", WebUtility.HtmlDecode(await respuesta.Content.ReadAsStringAsync()));
        Assert.Null(usuario);
    }

    [Fact]
    public async Task Acepta_a_quien_cumple_la_edad_minima_hoy()
    {
        var (_, usuario) = await RegistrarConFechaAsync(DateTime.Today.AddYears(-EdadUsuario.Minima).ToString("yyyy-MM-dd"));

        Assert.NotNull(usuario);
    }

    [Theory]
    [InlineData(1)]      // en el futuro
    [InlineData(-47000)] // más de 120 años
    public async Task Rechaza_fechas_imposibles(int dias)
    {
        var (respuesta, usuario) = await RegistrarConFechaAsync(DateTime.Today.AddDays(dias).ToString("yyyy-MM-dd"));

        Assert.Contains("fecha de nacimiento válida", WebUtility.HtmlDecode(await respuesta.Content.ReadAsStringAsync()));
        Assert.Null(usuario);
    }
}

// =============================
// 📊 ASISTENCIA POR RANGO DE EDAD
// =============================
public class EdadAsistenciaTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly AnaliticaService _analitica;
    private readonly DateTime _hoy = DateTime.Today;

    public EdadAsistenciaTests()
    {
        _analitica = new AnaliticaService(_e.Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
    }

    public void Dispose() => _e.Dispose();

    private void Nacio(string usuario, DateTime? fecha)
    {
        _e.Db.Users.Find(usuario)!.FechaNacimiento = fecha;
        _e.Db.SaveChanges();
    }

    private Task<AnalisisVentas> AnalisisAsync(string? organizador = null, int? categoria = null) => _analitica.AnalisisAsync(
        new FiltroAnalitica { Desde = _hoy.AddDays(-60), Hasta = _hoy, OrganizadorId = organizador, CategoriaId = categoria });

    [Theory]
    [InlineData("2000-06-15", "2026-06-14", 25)] // un día antes de cumplir
    [InlineData("2000-06-15", "2026-06-15", 26)] // el día que cumple
    [InlineData("2000-02-29", "2026-02-28", 25)] // nació en año bisiesto
    public void Calcula_la_edad_cumplida(string nacimiento, string fecha, int esperada) =>
        Assert.Equal(esperada, EdadUsuario.En(DateTime.Parse(nacimiento), DateTime.Parse(fecha)));

    [Theory]
    [InlineData(17, RangoEdad.Menores)]
    [InlineData(18, "18 a 24")]
    [InlineData(24, "18 a 24")]
    [InlineData(25, "25 a 34")]
    [InlineData(54, "45 a 54")]
    [InlineData(55, RangoEdad.Mayores)]
    public void Ubica_cada_edad_en_su_rango(int edad, string rango) => Assert.Equal(rango, RangoEdad.De(edad));

    [Fact]
    public async Task Cuenta_compradas_asistentes_y_categoria_favorita_por_rango()
    {
        var fechaEvento = _hoy.AddDays(-10);
        Nacio("ana", fechaEvento.AddYears(-20));
        Nacio("beto", fechaEvento.AddYears(-40));

        var concierto = _e.Evento("Concierto", 1, fechaEvento, 100, 50);
        var teatro = _e.Evento("Teatro", 2, fechaEvento, 100, 50);
        _e.Compra("ana", concierto, 4, fechaEvento.AddDays(-5), usadas: 3);
        _e.Compra("ana", teatro, 2, fechaEvento.AddDays(-5), usadas: 1);
        _e.Compra("beto", teatro, 2, fechaEvento.AddDays(-5), usadas: 2);

        var a = await AnalisisAsync();

        var jovenes = Assert.Single(a.PorEdad, e => e.Rango == "18 a 24");
        Assert.Equal(1, jovenes.Personas);
        Assert.Equal(6, jovenes.Entradas);
        Assert.Equal(4, jovenes.Asistieron);
        Assert.Equal(66.7, jovenes.Asistencia);
        Assert.Equal("Conciertos", jovenes.CategoriaFavorita);

        var adultos = Assert.Single(a.PorEdad, e => e.Rango == "35 a 44");
        Assert.Equal(100, adultos.Asistencia);
        Assert.Equal("Teatro", adultos.CategoriaFavorita);
    }

    [Fact]
    public async Task Usa_la_edad_del_dia_del_evento_y_no_la_de_hoy()
    {
        var fechaEvento = _hoy.AddDays(-10);
        // Tenía 24 años el día del evento y cumplió 25 después
        Nacio("ana", fechaEvento.AddYears(-25).AddDays(5));
        _e.Compra("ana", _e.Evento("Concierto", 1, fechaEvento, 100, 50), 1, fechaEvento.AddDays(-1), usadas: 1);

        Assert.Equal("18 a 24", Assert.Single((await AnalisisAsync()).PorEdad).Rango);
    }

    [Fact]
    public async Task Separa_las_cuentas_sin_fecha_de_nacimiento()
    {
        var fechaEvento = _hoy.AddDays(-10);
        Nacio("ana", null);
        _e.Compra("ana", _e.Evento("Concierto", 1, fechaEvento, 100, 50), 3, fechaEvento.AddDays(-1), usadas: 3);

        var a = await AnalisisAsync();

        Assert.Empty(a.PorEdad);
        Assert.Equal(3, a.EntradasSinEdad);
    }

    [Fact]
    public async Task No_cuenta_eventos_futuros_ni_entradas_anuladas()
    {
        Nacio("ana", _hoy.AddYears(-20));
        _e.Compra("ana", _e.Evento("Próximo", 1, _hoy.AddDays(10), 100, 50), 2, _hoy.AddDays(-1));

        var pasado = _e.Evento("Pasado", 1, _hoy.AddDays(-10), 100, 50);
        _e.Compra("ana", pasado, 2, _hoy.AddDays(-20), usadas: 1);
        var anulada = _e.Db.Entradas.First(x => x.EventoId == pasado.Id && x.Estado == EstadoEntrada.Activa);
        anulada.Estado = EstadoEntrada.Anulada;
        _e.Db.SaveChanges();

        var rango = Assert.Single((await AnalisisAsync()).PorEdad);
        Assert.Equal(1, rango.Entradas);
        Assert.Equal(1, rango.Asistieron);
    }

    [Fact]
    public async Task Respeta_el_filtro_de_categoria()
    {
        var fechaEvento = _hoy.AddDays(-10);
        Nacio("ana", fechaEvento.AddYears(-20));
        _e.Compra("ana", _e.Evento("Concierto", 1, fechaEvento, 100, 50), 2, fechaEvento.AddDays(-1), usadas: 2);
        _e.Compra("ana", _e.Evento("Teatro", 2, fechaEvento, 100, 50), 3, fechaEvento.AddDays(-1), usadas: 3);

        Assert.Equal(3, Assert.Single((await AnalisisAsync(categoria: 2)).PorEdad).Asistieron);
    }

    [Fact]
    public async Task El_organizador_no_recibe_el_analisis_por_edad()
    {
        var fechaEvento = _hoy.AddDays(-10);
        Nacio("ana", fechaEvento.AddYears(-20));
        _e.Compra("ana", _e.Evento("Concierto", 1, fechaEvento, 100, 50), 2, fechaEvento.AddDays(-1), usadas: 2);

        var a = await AnalisisAsync(organizador: "org");

        Assert.Empty(a.PorEdad);
        Assert.Equal(0, a.EntradasSinEdad);
    }
}
