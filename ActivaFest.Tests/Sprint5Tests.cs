using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Comisiones;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Publicidad;

namespace ActivaFest.Tests;

// =============================
// 💰 HU16 / OE5: COMISIONES Y LIQUIDACIONES
// =============================
public class ComisionesTests : BaseSprint4
{
    private readonly ComisionService _comisiones;

    public ComisionesTests()
    {
        _comisiones = new ComisionService(Db, NullLogger<ComisionService>.Instance);
    }

    private Orden OrdenDe(Entrada e) => Db.Ordenes.AsNoTracking().Single(o => o.Id == e.OrdenId);

    [Fact]
    public async Task La_comision_se_calcula_y_congela_al_confirmar_el_pago()
    {
        var (evento, general, vip) = EventoDe("org");
        var orden = OrdenDe((await ComprarAsync("ana", evento.Id, new() { [general.Id] = 2, [vip.Id] = 1 }))[0]);

        Assert.Equal(220.5m, orden.Total);       // 2 × 50 + 120.50
        Assert.Equal(8m, orden.PorcentajeComision);
        Assert.Equal(17.64m, orden.Comision);    // 8 % de 220.50
        Assert.Equal(202.86m, orden.Neto);
    }

    [Fact]
    public async Task Cambiar_la_tarifa_no_altera_las_ventas_anteriores()
    {
        var (evento, general, _) = EventoDe("org");
        var antes = OrdenDe((await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 }))[0]);

        var compras = Fabrica.Compras(Db, Email, comision: 10m);
        var nueva = (await compras.CrearOrdenAsync("beto", evento.Id, new Dictionary<int, int> { [general.Id] = 1 })).Orden!;
        await compras.ConfirmarPagoAsync(nueva.Codigo, "Simulado", "SIM", nueva.Total, Url);

        Assert.Equal(4m, Db.Ordenes.AsNoTracking().Single(o => o.Id == antes.Id).Comision);
        Assert.Equal(5m, Db.Ordenes.AsNoTracking().Single(o => o.Codigo == nueva.Codigo).Comision);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 8)]
    [InlineData(10.05, 0.80)]
    [InlineData(0.0625, 0.01)] // 0.005 redondea hacia arriba (comercial)
    public void Redondea_la_comision_a_centimos(decimal total, decimal esperada)
    {
        Assert.Equal(esperada, CompraService.CalcularComision(total, 8m));
    }

    [Fact]
    public async Task El_reporte_suma_ventas_comisiones_publicidad_y_saldo_por_evento()
    {
        var (e1, g1, _) = EventoDe("org");
        var (e2, g2, _) = EventoDe("otroOrg");
        await ComprarAsync("ana", e1.Id, new() { [g1.Id] = 2 });   // 100 → comisión 8
        await ComprarAsync("beto", e2.Id, new() { [g2.Id] = 1 });  // 50 → comisión 4
        Db.ContratosDestacado.Add(new ContratoDestacado
        {
            Codigo = "DST-x", EventoId = e1.Id, PaqueteId = 1, OrganizadorId = "org", Precio = 29.9m, Dias = 3,
            Prioridad = 1, Estado = EstadoContrato.Activo, FechaPago = DateTime.UtcNow
        });
        Db.SaveChanges();

        var r = await _comisiones.ReporteAsync(null, null);

        Assert.Equal(150m, r.Bruto);
        Assert.Equal(12m, r.Comisiones);
        Assert.Equal(29.9m, r.IngresosPublicidad);
        Assert.Equal(41.9m, r.IngresosPlataforma);
        Assert.Equal(138m, r.SaldoPorLiquidar);
        var fila = r.Eventos.Single(f => f.EventoId == e1.Id);
        Assert.Equal((100m, 8m, 92m, 92m), (fila.Bruto, fila.Comision, fila.Neto, fila.Saldo));
    }

    // HU16: por evento se ven las entradas vendidas, el porcentaje y el monto de comisión y el saldo a liquidar
    [Fact]
    public async Task El_reporte_muestra_entradas_vendidas_y_porcentaje_de_comision_por_evento()
    {
        var (evento, general, vip) = EventoDe("org");
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 3 });            // 3 x 50 = 150 al 8 %  → 12
        var compras = Fabrica.Compras(Db, Email, comision: 10m);
        var orden = (await compras.CrearOrdenAsync("beto", evento.Id, new Dictionary<int, int> { [general.Id] = 1 })).Orden!;
        await compras.ConfirmarPagoAsync(orden.Codigo, "Simulado", "SIM", orden.Total, Url); // 1 x 50 = 50 al 10 % → 5

        var fila = (await _comisiones.ReporteAsync(null, null)).Eventos.Single();

        Assert.Equal(4, fila.EntradasVendidas);
        Assert.Equal(2, fila.Ordenes);
        Assert.Equal(17m, fila.Comision);
        Assert.Equal(8.5m, fila.PorcentajeComision); // efectivo: 17 / 200
        Assert.Equal(183m, fila.Saldo);
    }

    [Fact]
    public async Task Un_evento_cancelado_no_genera_comision()
    {
        var (evento, general, _) = EventoDe("org");
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 2 });
        await Db.Ordenes.Where(o => o.EventoId == evento.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Estado, EstadoOrden.RequiereReembolso));

        var r = await _comisiones.ReporteAsync(null, null);

        Assert.Equal(0m, r.Comisiones);
        Assert.Empty(r.Eventos);
    }

    [Fact]
    public async Task Una_liquidacion_reduce_el_saldo_y_queda_registrada_con_quien_la_hizo()
    {
        var (evento, general, _) = EventoDe("org");
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 2 }); // neto 92

        var r = await _comisiones.RegistrarLiquidacionAsync(evento.Id, 50m, "OP-001", "Primer pago", "admin");

        Assert.True(r.Exito);
        var l = Db.Liquidaciones.AsNoTracking().Single();
        Assert.Equal(("org", 50m, "OP-001", "admin"), (l.OrganizadorId, l.Monto, l.Referencia, l.RegistradoPorId));
        Assert.Equal(42m, (await _comisiones.ReporteAsync(null, null)).Eventos.Single().Saldo);
    }

    [Theory]
    [InlineData(92.01, "OP-1")]
    [InlineData(0, "OP-1")]
    [InlineData(-5, "OP-1")]
    [InlineData(10, " ")]
    public async Task Rechaza_liquidaciones_invalidas_o_mayores_al_saldo(decimal monto, string referencia)
    {
        var (evento, general, _) = EventoDe("org");
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 2 }); // neto 92

        var r = await _comisiones.RegistrarLiquidacionAsync(evento.Id, monto, referencia, null, "admin");

        Assert.False(r.Exito);
        Assert.Empty(Db.Liquidaciones);
    }

    [Fact]
    public async Task El_filtro_de_fechas_afecta_las_ventas_pero_no_el_saldo_acumulado()
    {
        var (evento, general, _) = EventoDe("org");
        var vieja = OrdenDe((await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 }))[0]);
        await Db.Ordenes.Where(o => o.Id == vieja.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.FechaPago, DateTime.UtcNow.AddDays(-40)));
        await ComprarAsync("beto", evento.Id, new() { [general.Id] = 1 });

        var r = await _comisiones.ReporteAsync(DateTime.Today.AddDays(-7), DateTime.Today);

        var fila = r.Eventos.Single();
        Assert.Equal(50m, fila.Bruto);
        Assert.Equal(92m, fila.NetoTotal);
    }

    [Fact]
    public async Task El_csv_es_para_excel_y_neutraliza_formulas()
    {
        var (evento, general, _) = EventoDe("org");
        await Db.Eventos.Where(e => e.Id == evento.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Titulo, "=HYPERLINK(\"http://malo\";\"x\")"));
        await ComprarAsync("ana", evento.Id, new() { [general.Id] = 1 });

        var bytes = _comisiones.ExportarCsv(await _comisiones.ReporteAsync(null, null));
        var texto = Encoding.UTF8.GetString(bytes);

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
        Assert.Contains("Organizador;Correo;Evento", texto);
        Assert.Contains("\"'=HYPERLINK(\"\"http://malo\"\";\"\"x\"\")\"", texto);
        Assert.Contains("Entradas vendidas;Ventas brutas;% comisión;Comisión", texto);
        Assert.Contains(";1;50,00;8,00;4,00;46,00;", texto); // 1 entrada, S/ 50, 8 %, S/ 4 de comisión
    }

    [Theory]
    [InlineData("normal", "normal")]
    [InlineData("+51 999", "'+51 999")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("con;punto y coma", "\"con;punto y coma\"")]
    public void Escapa_celdas_del_csv(string valor, string esperado)
    {
        Assert.Equal(esperado, ComisionService.Celda(valor));
    }
}

// =============================
// ⭐ HU12 / OE2: PAQUETES PUBLICITARIOS
// =============================
public class DestacadosTests : BaseSprint4
{
    private readonly IDistributedCache _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly CatalogoService _catalogo;
    private readonly DestacadoService _destacados;

    private const int Impulso = 1, Plus = 2, Premium = 3; // 3, 7 y 15 días

    public DestacadosTests()
    {
        _catalogo = new CatalogoService(Db, _cache, NullLogger<CatalogoService>.Instance);
        _destacados = new DestacadoService(Db, _catalogo, Email, NullLogger<DestacadoService>.Instance);
    }

    private async Task<ContratoDestacado> ContratarYPagarAsync(int eventoId, int paqueteId, string usuario = "org")
    {
        var contrato = (await _destacados.ContratarAsync(usuario, false, eventoId, paqueteId)).Contrato!;
        Assert.Equal(ResultadoActivacion.Activado, await _destacados.ConfirmarPagoAsync(contrato.Codigo, "Simulado", "SIM", contrato.Precio));
        return Db.ContratosDestacado.AsNoTracking().Single(c => c.Id == contrato.Id);
    }

    [Fact]
    public async Task Al_pagar_el_evento_queda_destacado_por_los_dias_del_paquete()
    {
        var (evento, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);

        var c = await ContratarYPagarAsync(evento.Id, Plus);

        Assert.Equal(EstadoContrato.Activo, c.Estado);
        Assert.Equal(59.9m, c.Precio);
        Assert.Equal(7, (c.FechaFin!.Value - c.FechaInicio!.Value).TotalDays, 3);
        Assert.True(c.VigenteEn(DateTime.Now));
        Assert.Contains(Email.Enviados, m => m.Asunto.Contains("destacado"));
    }

    [Fact]
    public async Task Un_segundo_paquete_se_suma_despues_del_vigente()
    {
        var (evento, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);

        var primero = await ContratarYPagarAsync(evento.Id, Impulso);
        var segundo = await ContratarYPagarAsync(evento.Id, Plus);

        Assert.Equal(primero.FechaFin, segundo.FechaInicio);
        Assert.Equal(10, (segundo.FechaFin!.Value - primero.FechaInicio!.Value).TotalDays, 3);
    }

    [Fact]
    public async Task La_vigencia_nunca_pasa_la_fecha_del_evento()
    {
        var (evento, _, _) = EventoDe("org", horasHastaInicio: 24 * 5 + 2);
        var fechaEvento = Db.Eventos.AsNoTracking().Single(e => e.Id == evento.Id).Fecha;

        await ContratarYPagarAsync(evento.Id, Impulso);
        var segundo = await ContratarYPagarAsync(evento.Id, Impulso); // empezaría en el día 3 y terminaría en el 6

        Assert.Equal(fechaEvento, segundo.FechaFin);
    }

    [Fact]
    public async Task No_permite_paquetes_mas_largos_que_los_dias_que_faltan()
    {
        var (evento, _, _) = EventoDe("org", horasHastaInicio: 24 * 5);

        var r = await _destacados.ContratarAsync("org", false, evento.Id, Premium);

        Assert.Contains("hasta", r.Error);
        Assert.Empty(Db.ContratosDestacado);
    }

    [Fact]
    public async Task Solo_el_organizador_destaca_su_evento_aprobado()
    {
        var (ajeno, _, _) = EventoDe("otroOrg", horasHastaInicio: 24 * 30);
        var (pendiente, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);
        await Db.Eventos.Where(e => e.Id == pendiente.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Estado, EstadoEvento.Pendiente));

        Assert.NotNull((await _destacados.ContratarAsync("org", false, ajeno.Id, Impulso)).Error);
        Assert.NotNull((await _destacados.ContratarAsync("org", false, pendiente.Id, Impulso)).Error);
        Assert.Null((await _destacados.ContratarAsync("admin", true, ajeno.Id, Impulso)).Error);
    }

    [Fact]
    public async Task La_confirmacion_es_idempotente_y_valida_el_monto()
    {
        var (evento, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);
        var contrato = (await _destacados.ContratarAsync("org", false, evento.Id, Impulso)).Contrato!;

        Assert.Equal(ResultadoActivacion.MontoNoCoincide, await _destacados.ConfirmarPagoAsync(contrato.Codigo, "MP", "1", 1m));
        Assert.Equal(ResultadoActivacion.Activado, await _destacados.ConfirmarPagoAsync(contrato.Codigo, "MP", "1", 29.9m));
        Assert.Equal(ResultadoActivacion.YaEstabaActivo, await _destacados.ConfirmarPagoAsync(contrato.Codigo, "MP", "1", 29.9m));
    }

    [Fact]
    public async Task Un_contrato_nuevo_descarta_el_pendiente_anterior()
    {
        var (evento, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);
        var primero = (await _destacados.ContratarAsync("org", false, evento.Id, Impulso)).Contrato!;

        await _destacados.ContratarAsync("org", false, evento.Id, Plus);

        Assert.Equal(EstadoContrato.Cancelado, Db.ContratosDestacado.AsNoTracking().Single(c => c.Id == primero.Id).Estado);
    }

    [Fact]
    public async Task La_portada_muestra_solo_destacados_vigentes_ordenados_por_prioridad()
    {
        var (impulso, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);
        var (premium, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);
        var (vencido, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);
        var (normal, _, _) = EventoDe("org", horasHastaInicio: 24 * 30);

        await ContratarYPagarAsync(impulso.Id, Impulso);
        await ContratarYPagarAsync(premium.Id, Premium);
        var c = await ContratarYPagarAsync(vencido.Id, Impulso);
        await Db.ContratosDestacado.Where(x => x.Id == c.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.FechaInicio, DateTime.Now.AddDays(-5)).SetProperty(x => x.FechaFin, DateTime.Now.AddDays(-2)));
        await _catalogo.InvalidarAsync();

        var portada = await _catalogo.DestacadosAsync();

        Assert.Equal(new[] { premium.Id, impulso.Id }, portada.Select(e => e.Id));
        Assert.All(portada, e => Assert.True(e.Destacado));

        var catalogo = await _catalogo.BuscarAsync(new CatalogoFiltro());
        Assert.False(catalogo.Eventos.Single(e => e.Id == normal.Id).Destacado);
        Assert.True(catalogo.Eventos.Single(e => e.Id == premium.Id).Destacado);
    }
}
