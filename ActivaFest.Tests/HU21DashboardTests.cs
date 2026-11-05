using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using ActivaFest.Services.Entradas;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// Escenario de ventas controlado para la analítica (HU21, HU22, HU29)
public class EscenarioVentas : IDisposable
{
    public readonly SqliteConnection Conexion;
    public readonly ApplicationDbContext Db;
    public readonly DateTime Desde = new(2026, 11, 1), Hasta = new(2026, 11, 30);

    public EscenarioVentas()
    {
        Conexion = new SqliteConnection("Data Source=:memory:");
        Conexion.Open();
        Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(Conexion).Options);
        Db.Database.EnsureCreated();
        CompraServiceTests.CrearUsuarios(Db, "org", "ana", "beto", "carla");
    }

    public void Dispose()
    {
        Db.Dispose();
        Conexion.Dispose();
    }

    public Evento Evento(string titulo, int categoria, DateTime fecha, int capacidad, decimal precio, int visitas = 0)
    {
        var e = new Evento
        {
            Titulo = titulo, Descripcion = "Evento de analítica.", CategoriaId = categoria, Ubicacion = "Arequipa", Fecha = fecha,
            Estado = EstadoEvento.Aprobado, OrganizadorId = "org", Aforo = capacidad, Visitas = visitas,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = precio, Capacidad = capacidad } }
        };
        Db.Eventos.Add(e);
        Db.SaveChanges();
        return e;
    }

    // Compra pagada en una fecha local, con entradas emitidas (las primeras "usadas" quedan validadas en puerta)
    public Orden Compra(string usuario, Evento evento, int cantidad, DateTime pagoLocal, int usadas = 0, EstadoOrden estado = EstadoOrden.Pagada)
    {
        var zona = evento.TiposEntrada.First();
        var orden = new Orden
        {
            Codigo = Guid.NewGuid().ToString("N")[..12], UsuarioId = usuario, EventoId = evento.Id, Estado = estado,
            Total = zona.Precio * cantidad, PorcentajeComision = 8, Comision = Math.Round(zona.Precio * cantidad * 0.08m, 2),
            FechaPago = pagoLocal.ToUniversalTime(), FechaCreacion = pagoLocal.ToUniversalTime(), FechaExpiracion = pagoLocal.ToUniversalTime(),
            Items = { new OrdenItem { TipoEntradaId = zona.Id, Cantidad = cantidad, PrecioUnitario = zona.Precio } }
        };
        Db.Ordenes.Add(orden);
        Db.SaveChanges();
        if (estado == EstadoOrden.Pagada)
        {
            for (var i = 0; i < cantidad; i++)
                Db.Entradas.Add(new Entrada
                {
                    Codigo = CodigoSeguro.Generar(), OrdenId = orden.Id, EventoId = evento.Id, TipoEntradaId = zona.Id, UsuarioId = usuario,
                    Estado = i < usadas ? EstadoEntrada.Usada : EstadoEntrada.Activa, FechaEmision = pagoLocal.ToUniversalTime()
                });
            Db.SaveChanges();
        }
        return orden;
    }
}

// =============================
// 📊 HU21: DASHBOARD DE INDICADORES (pruebas del servicio)
// =============================
public class HU21DashboardTests : IDisposable
{
    private readonly EscenarioVentas _e = new();
    private readonly AnaliticaService _analitica;

    public HU21DashboardTests()
    {
        _analitica = new AnaliticaService(_e.Db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
    }

    public void Dispose() => _e.Dispose();

    private void Escenario()
    {
        // Noviembre: un concierto ya realizado (con asistencia) y una obra; octubre: ventas del período anterior
        var concierto = _e.Evento("Rock", 1, new DateTime(2026, 11, 10, 20, 0, 0), capacidad: 100, precio: 50, visitas: 40);
        var obra = _e.Evento("Hamlet", 2, new DateTime(2026, 11, 25, 20, 0, 0), capacidad: 50, precio: 80, visitas: 10);
        _e.Compra("ana", concierto, 4, new DateTime(2026, 11, 2, 12, 0, 0), usadas: 3);   // 200
        _e.Compra("beto", concierto, 2, new DateTime(2026, 11, 5, 9, 0, 0), usadas: 2);   // 100
        _e.Compra("carla", obra, 3, new DateTime(2026, 11, 20, 18, 0, 0));               // 240
        _e.Compra("ana", obra, 1, new DateTime(2026, 10, 15, 10, 0, 0));                 // octubre: 80
        _e.Compra("beto", concierto, 5, new DateTime(2026, 11, 3, 10, 0, 0), estado: EstadoOrden.Expirada); // no cuenta
    }

    // Criterio: muestra ingresos, entradas vendidas, ticket promedio, ocupación, asistencia y comisiones,
    // y las cifras coinciden con consultas de control sobre la base de datos
    [Fact]
    public async Task Los_indicadores_coinciden_con_las_consultas_de_control()
    {
        Escenario();
        var d = await _analitica.DashboardAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta });

        // Consultas de control (independientes del servicio)
        var pagadas = _e.Db.Ordenes.AsNoTracking().Where(o => o.Estado == EstadoOrden.Pagada).AsEnumerable()
            .Where(o => o.FechaPago!.Value.ToLocalTime() >= _e.Desde && o.FechaPago.Value.ToLocalTime() < _e.Hasta.AddDays(1)).ToList();
        Assert.Equal(pagadas.Sum(o => o.Total), d.Ingresos);                                    // 540
        Assert.Equal(_e.Db.OrdenItems.AsEnumerable().Where(i => pagadas.Any(o => o.Id == i.OrdenId)).Sum(i => i.Cantidad), d.EntradasVendidas); // 9
        Assert.Equal(Math.Round(pagadas.Sum(o => o.Total) / pagadas.Count, 2), d.TicketPromedio); // 180
        Assert.Equal(pagadas.Sum(o => o.Comision), d.Comisiones);                                // 43.2
        Assert.Equal((540m, 9, 180m, 43.2m), (d.Ingresos, d.EntradasVendidas, d.TicketPromedio, d.Comisiones));

        // Ocupación: vendidas / aforo de los eventos de noviembre = 10 / 150
        Assert.Equal(Math.Round(100.0 * 10 / 150, 1), d.Ocupacion);
        // Asistencia: solo eventos ya realizados (el concierto del 10/11, si ya pasó): 5 usadas de 6
        if (new DateTime(2026, 11, 10, 20, 0, 0) <= DateTime.Now)
            Assert.Equal(Math.Round(100.0 * 5 / 6, 1), d.Asistencia);
        // Conversión: compras pagadas / visitas a las fichas = 4 / 50
        Assert.Equal(8.0, d.Conversion);

        // Período anterior (octubre): 80 soles, 1 entrada
        Assert.Equal((80m, 1), (d.IngresosAnterior, d.EntradasAnterior));
        Assert.Equal(575.0, DashboardAnalitica.Variacion(d.Ingresos, d.IngresosAnterior));
    }

    [Fact]
    public async Task Se_filtra_por_categoria()
    {
        Escenario();

        var teatro = await _analitica.DashboardAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta, CategoriaId = 2 });

        Assert.Equal((240m, 3, 1), (teatro.Ingresos, teatro.EntradasVendidas, teatro.Compras));
        Assert.Equal(8.0, teatro.Ocupacion); // 4 vendidas (3 en noviembre + 1 en octubre) / 50 lugares
    }

    [Fact]
    public async Task Ventas_por_categoria_e_ingresos_por_semana_suman_lo_mismo_que_el_total()
    {
        Escenario();

        var d = await _analitica.DashboardAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta });

        Assert.Equal(new[] { ("Conciertos", 6, 300m), ("Teatro", 3, 240m) }, d.VentasPorCategoria.Select(c => (c.Categoria, c.Entradas, c.Ingresos)));
        Assert.Equal(AnaliticaService.SemanasTendencia, d.IngresosPorSemana.Count);
        Assert.Equal(540m + 80m, d.IngresosPorSemana.Sum(s => s.Ingresos)); // las 10 semanas incluyen la venta de octubre
        Assert.Equal(3, d.UsuariosActivos);
    }

    // Caché de 5 minutos mientras los datos no cambian; una compra nueva se ve al instante (tiempo real)
    [Fact]
    public async Task El_dashboard_usa_cache_pero_una_compra_nueva_se_ve_al_instante()
    {
        Escenario();
        var filtro = new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta };
        var primero = await _analitica.DashboardAsync(filtro);

        var repetido = await _analitica.DashboardAsync(filtro);
        Assert.Equal(primero.CalculadoEn, repetido.CalculadoEn);   // sin cambios: sale de la caché

        _e.Compra("carla", _e.Db.Eventos.First(), 1, new DateTime(2026, 11, 6, 10, 0, 0));
        var despues = await _analitica.DashboardAsync(filtro);

        Assert.Equal(primero.Ingresos + 50m, despues.Ingresos);
        Assert.Contains(despues.UltimasCompras, c => c.Fecha == new DateTime(2026, 11, 6, 10, 0, 0) && c.Evento == "Rock");
    }

    [Fact]
    public async Task Muestra_las_ultimas_compras_del_periodo_con_codigo_anonimo()
    {
        Escenario();

        var d = await _analitica.DashboardAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta });

        Assert.Equal(new[] { ("Hamlet", 3, 240m), ("Rock", 2, 100m), ("Rock", 4, 200m) }, d.UltimasCompras.Select(c => (c.Evento, c.Entradas, c.Total)));
        Assert.Equal(ClienteAnalitica.CodigoDe("carla"), d.UltimasCompras[0].Cliente);
        Assert.DoesNotContain(d.UltimasCompras, c => c.Cliente.Contains("carla"));
    }

    [Fact]
    public async Task La_ocupacion_incluye_eventos_futuros_que_vendieron_en_el_periodo()
    {
        // Compra en noviembre para un evento de enero: antes no contaba en la ocupación de noviembre
        var futuro = _e.Evento("Año Nuevo", 1, new DateTime(2027, 1, 10, 22, 0, 0), capacidad: 20, precio: 40);
        _e.Compra("ana", futuro, 5, new DateTime(2026, 11, 8, 12, 0, 0));

        var d = await _analitica.DashboardAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta });

        Assert.Equal(1, d.EventosConsiderados);
        Assert.Equal(25.0, d.Ocupacion);   // 5 de 20 lugares
        Assert.Equal(0, d.EventosRealizados);
        Assert.Null(d.Asistencia);
    }

    [Theory]
    [InlineData("hoy", "2026-10-27", "2026-10-27")]
    [InlineData("7d", "2026-10-21", "2026-10-27")]
    [InlineData("30d", "2026-09-28", "2026-10-27")]
    [InlineData("mes", "2026-10-01", "2026-10-27")]
    [InlineData("mespasado", "2026-09-01", "2026-09-30")]
    [InlineData("anio", "2026-01-01", "2026-10-27")]
    [InlineData(null, "2026-10-01", "2026-10-27")]
    public void Los_periodos_rapidos_dan_fechas_claras(string? rango, string desde, string hasta)
    {
        var (f, r) = FiltroAnalitica.DeRango(rango, null, null, null, new DateTime(2026, 10, 27));
        Assert.Equal((DateTime.Parse(desde), DateTime.Parse(hasta)), (f.Desde, f.Hasta));
        Assert.Equal(rango ?? "mes", r);

        var (p, rp) = FiltroAnalitica.DeRango(null, new DateTime(2026, 5, 1), new DateTime(2026, 5, 15), 2, new DateTime(2026, 10, 27));
        Assert.Equal(("personalizado", new DateTime(2026, 5, 1), 2), (rp, p.Desde, p.CategoriaId));
    }

    // RNF02: carga en menos de 3 segundos (sin caché, con miles de compras)
    [Fact]
    public async Task Carga_en_menos_de_3_segundos_con_miles_de_compras()
    {
        var evento = _e.Evento("Masivo", 1, new DateTime(2026, 11, 20, 20, 0, 0), capacidad: 10000, precio: 30, visitas: 50000);
        var zona = evento.TiposEntrada.First();
        _e.Db.Ordenes.AddRange(Enumerable.Range(0, 3000).Select(i => new Orden
        {
            Codigo = $"M{i:D6}", UsuarioId = new[] { "ana", "beto", "carla" }[i % 3], EventoId = evento.Id, Estado = EstadoOrden.Pagada,
            Total = 30, Comision = 2.4m, FechaPago = new DateTime(2026, 11, 1 + i % 28, 12, 0, 0).ToUniversalTime(), FechaExpiracion = DateTime.UtcNow,
            Items = { new OrdenItem { TipoEntradaId = zona.Id, Cantidad = 1, PrecioUnitario = 30 } }
        }));
        _e.Db.SaveChanges();

        var reloj = Stopwatch.StartNew();
        var d = await _analitica.DashboardAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta });
        reloj.Stop();

        Assert.Equal(90000m, d.Ingresos);
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(3), $"Tardó {reloj.Elapsed.TotalMilliseconds:N0} ms");
    }

    private async Task<AnalisisVentas> AnalisisAsync(int? categoria = null) =>
        await _analitica.AnalisisAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta, CategoriaId = categoria });

    // Los gráficos y la IA salen de esta misma fuente: sus cifras coinciden con los indicadores del dashboard
    [Fact]
    public async Task El_analisis_coincide_con_las_compras_pagadas()
    {
        Escenario();
        var a = await AnalisisAsync();
        var d = await _analitica.DashboardAsync(new FiltroAnalitica { Desde = _e.Desde, Hasta = _e.Hasta });

        Assert.Equal((d.Ingresos, d.Comisiones, d.Compras), (a.Ingresos, a.Comisiones, a.Compras));

        // Acumulado: todo el historial, con la compra de octubre y sin la orden expirada
        Assert.Equal(620, a.Acumulado.Ingresos);
        Assert.Equal(4, a.Acumulado.Compras);
        Assert.Equal(10, a.Acumulado.Entradas);
        Assert.Equal(3, a.Acumulado.Compradores);

        // En noviembre cada comprador tiene una compra; la compra anterior de Ana fue en octubre.
        Assert.Equal((3, 0, 3), (a.Compradores, a.CompradoresRecurrentes, a.CompradoresNuevos));

        Assert.Equal(new[] { "Rock", "Hamlet" }, a.TopEventos.Select(e => e.Evento));
        Assert.Equal((300m, 24m, 6), (a.TopEventos[0].Ingresos, a.TopEventos[0].Comision, a.TopEventos[0].Entradas));
        Assert.Equal(a.Comisiones, a.PorCategoria.Sum(c => c.Comisiones));

        var inicioSerie = AnaliticaService.InicioSerieHistorica;
        var hoy = DateTime.Today;
        var finMes = new DateTime(hoy.Year, hoy.Month, 1);
        var cantidadMeses = (finMes.Year - inicioSerie.Year) * 12 + finMes.Month - inicioSerie.Month + 1;
        var pagosEnSerie = _e.Db.Ordenes.AsNoTracking().Where(o => o.Estado == EstadoOrden.Pagada)
            .ToList().Where(o => o.FechaPago!.Value.ToLocalTime().Date >= inicioSerie && o.FechaPago.Value.ToLocalTime().Date <= hoy).ToList();
        Assert.Equal(cantidadMeses, a.Meses.Count);
        Assert.Equal(inicioSerie.ToString("MMM yy", System.Globalization.CultureInfo.GetCultureInfo("es-PE")).Replace(".", ""), a.Meses[0].Etiqueta);
        Assert.Equal(pagosEnSerie.Sum(o => o.Total), a.Meses.Sum(m => m.Ingresos));
        Assert.Equal(pagosEnSerie.Sum(o => o.Comision), a.Meses.Sum(m => m.Comisiones));

        Assert.Equal(new[] { "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo" }, a.DiasSemana.Select(x => x.Dia));
        Assert.Equal(pagosEnSerie.Count, a.DiasSemana.Sum(x => x.Compras));
        Assert.Equal(pagosEnSerie.Sum(o => o.Total), a.DiasSemana.Sum(x => x.Ingresos));

        Assert.Equal(ClienteAnalitica.CodigoDe("carla"), a.TopCompradores[0].Cliente);
        Assert.All(a.TopCompradores, c => Assert.StartsWith("#", c.Cliente));
        Assert.NotEmpty(a.Recomendaciones);
    }

    [Fact]
    public async Task Las_distribuciones_cuentan_eventos_zonas_organizadores_y_ordenes()
    {
        Escenario();
        var a = await AnalisisAsync();

        Assert.Equal(new[] { "Publicados", "Realizados", "Pendientes", "Rechazados", "Cancelados" }, a.EventosPorEstado.Select(e => e.Etiqueta));
        Assert.Equal(2, a.EventosPorEstado.Sum(e => e.Total));

        Assert.Equal(new[] { new ZonaAnalisis("General", 9, 540) }, a.PorZona);
        Assert.Equal(540, Assert.Single(a.TopOrganizadores).Ingresos);

        // La orden expirada del escenario también cuenta: se creó en el período pero no terminó en compra
        Assert.Equal(3, a.OrdenesPorEstado.Single(o => o.Etiqueta == "Pagadas").Total);
        Assert.Equal(1, a.OrdenesPorEstado.Single(o => o.Etiqueta == "Vencidas").Total);
    }

    [Fact]
    public async Task El_analisis_respeta_la_categoria_y_sale_igual_desde_la_cache()
    {
        Escenario();
        var teatro = await AnalisisAsync(2);
        var repetido = await AnalisisAsync(2);

        Assert.Equal(240, teatro.Ingresos);
        Assert.Equal(320, teatro.Acumulado.Ingresos);
        Assert.Equal(new[] { "Hamlet" }, teatro.TopEventos.Select(e => e.Evento));
        Assert.Equal(teatro.Recomendaciones, repetido.Recomendaciones);
        Assert.Equal(teatro.TopEventos, repetido.TopEventos);
        Assert.Equal(teatro.Segmentos, repetido.Segmentos);
        Assert.Equal(teatro.PorZona, repetido.PorZona);
        Assert.Equal(teatro.EventosPorEstado, repetido.EventosPorEstado);
        Assert.Equal(1, teatro.EventosPorEstado.Sum(e => e.Total));
    }

    [Fact]
    public async Task Los_destacados_miden_solo_lo_vendido_durante_su_vigencia()
    {
        Escenario();
        var hamlet = _e.Db.Eventos.Single(e => e.Titulo == "Hamlet");
        var rock = _e.Db.Eventos.Single(e => e.Titulo == "Rock");
        var paquete = new PaqueteDestacado { Nombre = "Oro", Dias = 7, Precio = 100, Prioridad = 1 };
        _e.Db.PaquetesDestacado.Add(paquete);
        _e.Db.SaveChanges();
        _e.Db.ContratosDestacado.AddRange(
            new ContratoDestacado { Codigo = "DST-1", EventoId = hamlet.Id, PaqueteId = paquete.Id, OrganizadorId = "org", Precio = 100, Dias = 7, Prioridad = 1, Estado = EstadoContrato.Activo, FechaInicio = new DateTime(2026, 11, 18), FechaFin = new DateTime(2026, 11, 25) },
            new ContratoDestacado { Codigo = "DST-2", EventoId = rock.Id, PaqueteId = paquete.Id, OrganizadorId = "org", Precio = 100, Dias = 7, Prioridad = 1, Estado = EstadoContrato.Activo, FechaInicio = new DateTime(2026, 11, 6), FechaFin = new DateTime(2026, 11, 9) },
            new ContratoDestacado { Codigo = "DST-3", EventoId = rock.Id, PaqueteId = paquete.Id, OrganizadorId = "org", Precio = 100, Dias = 7, Prioridad = 1, Estado = EstadoContrato.PendientePago });
        _e.Db.SaveChanges();

        var a = await AnalisisAsync();

        Assert.Equal(2, a.Destacados.Count);
        Assert.Equal(("Hamlet", 3, 240m), (a.Destacados[0].Evento, a.Destacados[0].Entradas, a.Destacados[0].Ingresos));
        Assert.Equal(("Rock", 0, 0m), (a.Destacados[1].Evento, a.Destacados[1].Entradas, a.Destacados[1].Ingresos));
        Assert.All(a.TopEventos, e => Assert.True(e.Destacado));
    }

    private static AnalisisVentas Base(decimal ingresos = 1000, decimal anterior = 1000, int compras = 2) => new()
    {
        Ingresos = ingresos, IngresosAnterior = anterior, Compras = compras, CalculadoEn = new DateTime(2026, 11, 4)
    };

    // Cada recomendación lleva el dato real que la justifica
    [Fact]
    public void Recomienda_actuar_cuando_las_ventas_caen_o_no_hay_ventas()
    {
        var caida = Recomendador.De(Base(ingresos: 700));
        Assert.Equal(Recomendador.Alta, caida[0].Prioridad);
        Assert.Contains("bajaron 30 %", caida[0].Hallazgo);
        Assert.Contains(Formato.Monto(700), caida[0].Dato);

        Assert.Contains("No hubo ventas", Recomendador.De(Base(ingresos: 0, compras: 0))[0].Hallazgo);
        Assert.Contains("crecieron 50 %", Recomendador.De(Base(ingresos: 1500)).Single().Hallazgo);
    }

    [Fact]
    public void Avisa_de_eventos_proximos_con_baja_ocupacion_segun_su_urgencia()
    {
        var a = Base();
        a.BajaOcupacion.Add(new EventoOcupacion(1, "Rock", "Conciertos", new DateTime(2026, 11, 10), 10, 100));
        var cercano = Recomendador.De(a)[0];
        Assert.Equal(Recomendador.Alta, cercano.Prioridad);
        Assert.Contains("«Rock» va al 10 %", cercano.Dato);

        a.BajaOcupacion[0] = a.BajaOcupacion[0] with { Fecha = new DateTime(2026, 12, 1) };
        Assert.Equal(Recomendador.Media, Recomendador.De(a)[0].Prioridad);
    }

    [Fact]
    public void Detecta_concentracion_clientes_en_riesgo_y_poca_recompra()
    {
        var a = Base();
        a.TopEventos.Add(new EventoAnalisis(1, "Rock", "Conciertos", 2, 10, 800, 64, 100, false));
        a.Segmentos.Add(new ResumenSegmento(SegmentoRfm.EnRiesgo, 3, 900, 2));
        a.Compradores = 10;
        a.CompradoresRecurrentes = 1;

        var r = Recomendador.De(a);

        Assert.Contains(r, x => x.Dato.Contains("«Rock» aporta el 80 %"));
        Assert.Contains(r, x => x.Hallazgo.Contains("3 clientes dejaron de comprar") && x.Dato.Contains("2 aceptan promociones"));
        Assert.Contains(r, x => x.Hallazgo == "Pocos compradores repiten" && x.Dato.Contains("1 de 10"));
    }

    [Fact]
    public void Con_pocos_datos_no_saca_conclusiones_de_porcentajes_ni_se_queda_vacio()
    {
        var a = Base();
        a.Compradores = 2;
        a.DiasSemana.Add(new PuntoDiaSemana("Lunes", 2, 1000));

        var r = Recomendador.De(a);

        Assert.Equal("No hay alertas en este período", Assert.Single(r).Hallazgo);
    }

    // Ventas por período: límites del calendario
    [Theory]
    [InlineData("semana", "2026-11-04", "2026-11-02", "2026-11-08")]     // miércoles
    [InlineData("semana", "2026-11-08", "2026-11-02", "2026-11-08")]     // domingo: sigue en la misma semana
    [InlineData("semana", "2027-01-01", "2026-12-28", "2027-01-03")]     // cruza el año
    [InlineData("quincena", "2026-11-15", "2026-11-01", "2026-11-15")]
    [InlineData("quincena", "2026-11-16", "2026-11-16", "2026-11-30")]
    [InlineData("quincena", "2028-02-20", "2028-02-16", "2028-02-29")]   // febrero bisiesto
    [InlineData("mes", "2026-12-31", "2026-12-01", "2026-12-31")]
    [InlineData("trimestre", "2026-03-31", "2026-01-01", "2026-03-31")]
    [InlineData("trimestre", "2026-11-04", "2026-10-01", "2026-12-31")]
    public void Cada_periodo_respeta_los_limites_del_calendario(string tipo, string fecha, string desde, string hasta)
    {
        var p = PeriodoVentas.De(tipo, DateTime.Parse(fecha));
        Assert.Equal(DateTime.Parse(desde), p.Desde);
        Assert.Equal(DateTime.Parse(hasta), p.Hasta);
    }

    [Fact]
    public void Los_periodos_contiguos_no_se_solapan_ni_dejan_huecos()
    {
        foreach (var (tipo, _) in PeriodoVentas.Tipos)
        {
            var p = PeriodoVentas.De(tipo, new DateTime(2026, 11, 20));
            Assert.Equal(p.Desde, p.Anterior().Hasta.AddDays(1));
            Assert.Equal(p.Hasta, p.Siguiente().Desde.AddDays(-1));
        }
        Assert.Equal("2.ª quincena de octubre de 2026", PeriodoVentas.De("quincena", new DateTime(2026, 11, 1)).Anterior().Etiqueta);
    }

    [Fact]
    public async Task Las_ventas_de_cada_periodo_coinciden_con_sus_compras_pagadas()
    {
        Escenario();
        var hoy = new DateTime(2026, 11, 20);

        // Semana del 16 al 22: solo la compra de carla; la semana anterior no tuvo ventas
        var semana = await _analitica.VentasPeriodoAsync(PeriodoVentas.De("semana", hoy), null, 1, hoy);
        Assert.Equal(new TotalesVentas(240, 1, 3, 19.20m), semana.Totales);
        Assert.Equal(0, semana.IngresosAnterior);
        var venta = Assert.Single(semana.Ventas);
        Assert.Equal("Hamlet", venta.Evento);
        Assert.Equal(ClienteAnalitica.CodigoDe("carla"), venta.Cliente);

        // 1.ª quincena de noviembre: 200 + 100 (la orden expirada no cuenta), de la más reciente a la más antigua
        var quincena = await _analitica.VentasPeriodoAsync(PeriodoVentas.De("quincena", new DateTime(2026, 11, 3)), null, 1, hoy);
        Assert.Equal(300, quincena.Totales.Ingresos);
        Assert.Equal(new[] { 100m, 200m }, quincena.Ventas.Select(v => v.Total));

        var mes = await _analitica.VentasPeriodoAsync(PeriodoVentas.De("mes", hoy), null, 1, hoy);
        Assert.Equal(540, mes.Totales.Ingresos);
        Assert.Equal(80, mes.IngresosAnterior);

        // Las tarjetas siempre son las del período en curso, aunque se consulte uno pasado
        var trimestre = await _analitica.VentasPeriodoAsync(PeriodoVentas.De("trimestre", new DateTime(2026, 2, 1)), null, 1, hoy);
        Assert.Equal(0, trimestre.Totales.Compras);
        Assert.Equal(new[] { 240m, 240m, 540m, 620m }, trimestre.Resumenes.Select(r => r.Totales.Ingresos));

        var teatro = await _analitica.VentasPeriodoAsync(PeriodoVentas.De("mes", hoy), 2, 1, hoy);
        Assert.Equal(240, teatro.Totales.Ingresos);
    }

    [Fact]
    public async Task Las_ventas_del_periodo_se_paginan_sin_perder_ninguna()
    {
        var evento = _e.Evento("Rock", 1, new DateTime(2026, 12, 10, 20, 0, 0), capacidad: 100, precio: 10);
        for (var i = 0; i < AnaliticaService.VentasPorPagina + 5; i++)
            _e.Compra("ana", evento, 1, new DateTime(2026, 11, 3, 8, i, 0));
        var hoy = new DateTime(2026, 11, 4);
        var periodo = PeriodoVentas.De("semana", hoy);

        var primera = await _analitica.VentasPeriodoAsync(periodo, null, 1, hoy);
        var segunda = await _analitica.VentasPeriodoAsync(periodo, null, 2, hoy);
        var fueraDeRango = await _analitica.VentasPeriodoAsync(periodo, null, 99, hoy);

        Assert.Equal(2, primera.Paginas);
        Assert.Equal(AnaliticaService.VentasPorPagina, primera.Ventas.Count);
        Assert.Equal(5, segunda.Ventas.Count);
        Assert.True(primera.Ventas.Last().Fecha > segunda.Ventas.First().Fecha);
        Assert.Equal(2, fueraDeRango.Pagina);
    }

    [Fact]
    public async Task Una_compra_nueva_aparece_al_instante_en_las_ventas_del_periodo()
    {
        Escenario();
        var hoy = new DateTime(2026, 11, 20);
        var periodo = PeriodoVentas.De("mes", hoy);
        var antes = await _analitica.VentasPeriodoAsync(periodo, null, 1, hoy);
        Assert.Equal(antes.Totales, (await _analitica.VentasPeriodoAsync(periodo, null, 1, hoy)).Totales);   // la segunda vez viene de la caché

        _e.Compra("beto", _e.Db.Eventos.Include(e => e.TiposEntrada).First(), 1, new DateTime(2026, 11, 19, 9, 0, 0));
        var despues = await _analitica.VentasPeriodoAsync(periodo, null, 1, hoy);

        Assert.Equal(antes.Totales.Compras + 1, despues.Totales.Compras);
    }
}

// =============================
// 📊 HU21: ACCESO Y VISTA DEL DASHBOARD (pruebas de integración)
// =============================
public class HU21DashboardVistaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU21DashboardVistaTests(AppDePrueba app)
    {
        _app = app;
    }

    [Fact]
    public async Task Solo_el_administrador_accede_al_dashboard()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Analitica")).Headers.Location!.PathAndQuery);
        Assert.StartsWith("/Auth/Login", (await _app.Cliente().GetAsync("/Analitica")).Headers.Location!.PathAndQuery);

        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        await admin.GetAsync("/Analitica");   // calentamiento: la primera visita compila la vista (no es lo que ve el usuario después)
        var reloj = Stopwatch.StartNew();
        var html = await TextoAsync(await admin.GetAsync("/Analitica?desde=2026-11-01&hasta=2026-11-30"));
        reloj.Stop();

        foreach (var indicador in new[] { "Ingresos", "Entradas vendidas", "Ticket promedio", "Ocupación", "Asistencia", "Comisiones" })
            Assert.Contains($"<span class=\"kpi-etiqueta\">{indicador}</span>", html);
        Assert.Contains("chart.js@4.4.1", html);
        Assert.Contains("🧾 Últimas compras", html);
        Assert.Contains("🗓️ Ventas por período", html);
        foreach (var tipo in new[] { "semana", "quincena", "mes", "trimestre" })
            Assert.Contains($"data-tipo=\"{tipo}\"", html);
        Assert.Contains("Mostrando <b>personalizado</b>: compras pagadas del <b>01/11/2026</b> al <b>30/11/2026</b>", html);

        // Datos en vivo (los pide la página cada 20 s): solo el administrador
        var datos = await admin.GetAsync("/Analitica/Datos?rango=mes");
        Assert.Equal("application/json", datos.Content.Headers.ContentType!.MediaType);
        var json = System.Text.Json.JsonDocument.Parse(await datos.Content.ReadAsStringAsync()).RootElement;
        foreach (var campo in new[] { "ingresos", "entradas", "ticket", "ocupacion", "asistencia", "comisiones", "semanas", "categorias", "ultimas" })
            Assert.True(json.TryGetProperty(campo, out _), campo);
        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Analitica/Datos")).Headers.Location!.PathAndQuery);

        // Ventas por período: una fecha futura se toma como el período en curso (y no hay "siguiente")
        var ventas = await admin.GetAsync("/Analitica/VentasPeriodo?tipo=trimestre&fecha=2999-01-01");
        var reporte = System.Text.Json.JsonDocument.Parse(await ventas.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("trimestre", reporte.GetProperty("tipo").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, reporte.GetProperty("siguiente").ValueKind);
        Assert.Equal(4, reporte.GetProperty("resumenes").GetArrayLength());
        foreach (var campo in new[] { "ingresos", "compras", "entradas", "comisiones", "ingresosAnterior", "ventas", "pagina", "paginas", "anterior" })
            Assert.True(reporte.TryGetProperty(campo, out _), campo);
        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Analitica/VentasPeriodo")).Headers.Location!.PathAndQuery);

        // Análisis: gráficos y recomendaciones, solo para el administrador y sin identificadores de usuario
        foreach (var seccion in new[] { "🏦 Total acumulado", "🥧 Ingresos por categoría", "🍩 Eventos por estado", "🏆 Eventos con más ventas", "⭐ Eventos destacados", "👥 Compradores", "💡 Recomendaciones", "Analizar con IA" })
            Assert.Contains(seccion, html);
        var analisis = await TextoAsync(await admin.GetAsync("/Analitica/Analisis?rango=mes"));
        var a = System.Text.Json.JsonDocument.Parse(analisis).RootElement;
        foreach (var campo in new[] { "acumulado", "meses", "diasSemana", "topEventos", "porCategoria", "topCompradores", "segmentos", "destacados", "recomendaciones", "eventosPorEstado", "porZona", "topOrganizadores", "ordenesPorEstado" })
            Assert.True(a.TryGetProperty(campo, out _), campo);
        Assert.DoesNotContain("usuarioId", analisis);
        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Analitica/Analisis")).Headers.Location!.PathAndQuery);
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(3));

        var csv = await admin.GetAsync("/Analitica/Exportar?desde=2026-11-01&hasta=2026-11-30");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
    }
}
