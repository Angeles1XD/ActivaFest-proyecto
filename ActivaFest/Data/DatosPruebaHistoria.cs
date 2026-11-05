using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ActivaFest.Models;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Entradas;

namespace ActivaFest.Data
{
    // =============================
    // 🧪 HISTORIA DE PRUEBA (solo desarrollo, con --datos-prueba)
    // =============================
    // Simula unos 14 meses de uso y llena las demás secciones. Se agrega a lo existente y no borra nada.
    //
    // ⚠️ Los patrones son SIMULADOS y están puestos a propósito para poder probar el análisis; no son conclusiones reales:
    //   - Zona cara: se compra con más anticipación, en grupos chicos y casi todos asisten.
    //   - Zona barata: se compra a última hora, en grupos grandes y falta más gente.
    //   - Fines de semana, julio y diciembre venden más. Cada cliente prefiere ciertas categorías.
    public static class DatosPruebaHistoria
    {
        private const string Marca = "Clientes fieles (prueba)";
        private const int TotalClientes = 250;

        private record Cliente(string Id, int Indice, DateTime Registro, bool Promociones, int Favorita1, int Favorita2);
        private record Sala(string Lugar, double Lat, double Lon);
        private record Plantilla(int CategoriaId, string Nombre, PlantillaPlano Plano, double Ocupacion, (string Zona, decimal Precio, int Capacidad)[] Zonas);

        private static readonly Sala[] Salas =
        {
            new("Explanada Norte, Lima", -12.0464, -77.0428), new("Arena Sur, Lima", -12.1500, -76.9800),
            new("Parque Central, Cusco", -13.5170, -71.9785), new("Campo Ferial, Arequipa", -16.4090, -71.5375),
            new("Coliseo Municipal, Trujillo", -8.1116, -79.0288), new("Malecón, Chiclayo", -6.7714, -79.8409),
            new("Plaza Mayor, Piura", -5.1945, -80.6328), new("Centro Cultural, Huancayo", -12.0651, -75.2049)
        };

        private static readonly Plantilla[] Plantillas =
        {
            new(1, "Noche de Rock", PlantillaPlano.CampoAbierto, .80, new[] { ("VIP", 190m, 60), ("Preferencial", 120m, 120), ("General", 70m, 240) }),
            new(1, "Salsa y Son en Vivo", PlantillaPlano.Discoteca, .78, new[] { ("VIP", 140m, 50), ("Pista", 60m, 200) }),
            new(2, "Comedia de Barrio", PlantillaPlano.Coliseo, .60, new[] { ("Campo", 85m, 90), ("Tribuna", 45m, 180) }),
            new(3, "Festival del Sol", PlantillaPlano.CampoAbierto, .85, new[] { ("VIP", 210m, 80), ("Preferencial", 130m, 180), ("General", 75m, 340) }),
            new(4, "Feria Artesanal", PlantillaPlano.Feria, .65, new[] { ("Entrada general", 12m, 350) }),
            new(5, "Muestra de Fotografía", PlantillaPlano.Feria, .35, new[] { ("Entrada general", 20m, 220) }),
            new(6, "Clásico de la Ciudad", PlantillaPlano.Estadio, .88, new[] { ("Occidente", 90m, 150), ("Oriente", 65m, 200), ("Popular", 30m, 320) }),
            new(7, "Ruta del Ceviche", PlantillaPlano.Feria, .70, new[] { ("Entrada general", 18m, 300) }),
            new(8, "Tarde de Títeres", PlantillaPlano.CampoAbierto, .60, new[] { ("Preferencial", 35m, 80), ("General", 18m, 200) }),
            new(9, "Danzas del Ande", PlantillaPlano.Coliseo, .45, new[] { ("Campo", 55m, 100), ("Tribuna", 25m, 200) })
        };

        public static async Task AmpliarAsync(IServiceProvider services, ILogger logger)
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            if (await db.Promociones.AnyAsync(p => p.Nombre == Marca))
            {
                logger.LogInformation("🧪 La historia de prueba ya existía: no se agregó nada");
                await RegistrarAnalisisAsync(db, logger);
                return;
            }

            var usuarios = services.GetRequiredService<UserManager<ApplicationUser>>();
            var entorno = services.GetRequiredService<IWebHostEnvironment>();
            var comision = services.GetRequiredService<IOptions<ComisionesOptions>>().Value.Porcentaje;
            var azar = new Random(2027);
            var ahora = DateTime.Now;

            var admin = (await usuarios.GetUsersInRoleAsync(Roles.Administrador)).First();

            // ---------- Personas ----------
            var organizadores = new List<ApplicationUser>
            {
                await DatosPrueba.UsuarioAsync(usuarios, DatosPrueba.CorreoOrganizador, "Organizador Prueba", Roles.Organizador),
                await DatosPrueba.UsuarioAsync(usuarios, DatosPrueba.CorreoOrganizador2, "Organizador Prueba 2", Roles.Organizador),
                await DatosPrueba.UsuarioAsync(usuarios, "organizador3.prueba@example.com", "Producciones Prueba SAC", Roles.Organizador),
                await DatosPrueba.UsuarioAsync(usuarios, "organizador4.prueba@example.com", "Asociación Cultural Prueba", Roles.Organizador),
            };
            organizadores[3].OrganizadorHasta = ahora.AddDays(60);

            var personal = new List<ApplicationUser>
            {
                await DatosPrueba.UsuarioAsync(usuarios, DatosPrueba.CorreoPuerta, "Puerta Prueba", Roles.ControlAcceso),
                await DatosPrueba.UsuarioAsync(usuarios, "puerta2.prueba@example.com", "Puerta Prueba 2", Roles.ControlAcceso),
                await DatosPrueba.UsuarioAsync(usuarios, "puerta3.prueba@example.com", "Puerta Prueba 3", Roles.ControlAcceso),
                // Mal calificado a propósito: sirve para probar la reputación del personal (HU33)
                await DatosPrueba.UsuarioAsync(usuarios, "puerta4.prueba@example.com", "Puerta Prueba 4", Roles.ControlAcceso),
            };

            var clientes = new List<Cliente>();
            for (var i = 1; i <= TotalClientes; i++)
            {
                var u = await DatosPrueba.UsuarioAsync(usuarios, $"cliente{i:00}.prueba@example.com", $"Cliente Prueba {i:00}", null, i % 3 != 0);
                // Los primeros llevan más tiempo en la plataforma; los últimos se registraron hace poco
                u.FechaRegistro = DateTime.UtcNow.AddDays(-Math.Max(10, 480 - i * 1.8));
                if (i % 97 == 0)
                    u.Activo = false;   // cuentas desactivadas, para la gestión de usuarios (HU19)
                clientes.Add(new Cliente(u.Id, i, u.FechaRegistro, u.AceptaPromociones, i % 9 + 1, i * 7 % 9 + 1));
            }
            await db.SaveChangesAsync();

            await using var tx = await db.Database.BeginTransactionAsync();

            // ---------- Promociones con cupones personales ----------
            var promociones = new[]
            {
                new Promocion { Nombre = Marca, TipoDescuento = TipoDescuento.Porcentaje, Valor = 15, FechaInicio = ahora.AddDays(-210).Date, FechaFin = ahora.AddDays(-170).Date, CriterioSegmento = "Segmento: fieles", Activa = false, CreadaPorId = admin.Id, FechaCreacion = ahora.AddDays(-211).ToUniversalTime() },
                new Promocion { Nombre = "Vuelve con S/ 20 (prueba)", TipoDescuento = TipoDescuento.Monto, Valor = 20, FechaInicio = ahora.AddDays(-125).Date, FechaFin = ahora.AddDays(-95).Date, CriterioSegmento = "Segmento: en riesgo", Activa = false, CreadaPorId = admin.Id, FechaCreacion = ahora.AddDays(-126).ToUniversalTime() },
                new Promocion { Nombre = "Fin de año 10 % (prueba)", TipoDescuento = TipoDescuento.Porcentaje, Valor = 10, FechaInicio = ahora.AddDays(-5).Date, FechaFin = ahora.AddDays(45).Date, CriterioSegmento = "Clientes que aceptan promociones", LimiteUsos = 80, CreadaPorId = admin.Id, FechaCreacion = ahora.AddDays(-6).ToUniversalTime() }
            };
            db.Promociones.AddRange(promociones);
            await db.SaveChangesAsync();

            var cupones = new List<Cupon>();
            foreach (var promo in promociones)
                foreach (var c in clientes.Where(c => c.Promociones && c.Indice % 2 == 0))
                    cupones.Add(new Cupon { PromocionId = promo.Id, UsuarioId = c.Id, Codigo = "PR-" + CodigoSeguro.Generar(9) });
            db.Cupones.AddRange(cupones);
            await db.SaveChangesAsync();
            var cuponesLibres = cupones.ToDictionary(c => (c.PromocionId, c.UsuarioId));

            var titulosUsados = new HashSet<string>();
            string Titulo(Plantilla p, DateTime f)
            {
                var t = $"{p.Nombre} · {f.ToString("MMMM yyyy", new System.Globalization.CultureInfo("es-PE"))}";
                for (var n = 2; !titulosUsados.Add(t); n++) t = $"{p.Nombre} · {f.ToString("MMMM yyyy", new System.Globalization.CultureInfo("es-PE"))} ({n})";
                return t;
            }

            // ---------- Eventos pasados: uno cada 8 días, desde hace unos 14 meses ----------
            var creados = new List<(int Id, DateTime Fecha, string Organizador)>();
            var indice = 0;
            for (var dias = 430; dias > 80; dias -= 8, indice++)
            {
                var fecha = FechaEvento(-dias, azar);
                var p = Plantillas[azar.Next(Plantillas.Length)];
                var org = organizadores[indice % organizadores.Count].Id;
                var ev = await CrearEventoAsync(db, p, Titulo(p, fecha), Salas[azar.Next(Salas.Length)], fecha, org, azar);
                await CrearVentasAsync(db, ev, Ocupacion(p, fecha, azar), clientes, personal, comision, promociones, cuponesLibres, azar, ahora);
                creados.Add((ev.Id, fecha, org));
                db.ChangeTracker.Clear();
            }

            // ---------- Eventos próximos: venden solo lo que ya se habría comprado a hoy ----------
            foreach (var d in new[] { 3, 8, 10, 15, 17, 22, 28, 36, 50, 75 })
            {
                var fecha = FechaEvento(d, azar);
                var p = Plantillas[azar.Next(Plantillas.Length)];
                var org = organizadores[indice++ % organizadores.Count].Id;
                var ev = await CrearEventoAsync(db, p, Titulo(p, fecha), Salas[azar.Next(Salas.Length)], fecha, org, azar);
                await CrearVentasAsync(db, ev, Ocupacion(p, fecha, azar), clientes, personal, comision, promociones, cuponesLibres, azar, ahora);
                creados.Add((ev.Id, fecha, org));
                db.ChangeTracker.Clear();
            }

            foreach (var promo in promociones)
                await db.Promociones.Where(x => x.Id == promo.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Usos, db.Cupones.Count(c => c.PromocionId == promo.Id && c.Usado)));

            await CrearCanceladoConReembolsosAsync(db, organizadores[2].Id, clientes, comision, azar, ahora);
            await CrearGratuitosAsync(db, organizadores, clientes, azar, ahora);
            db.ChangeTracker.Clear();

            await CrearDestacadosAsync(db, azar, ahora);
            await CrearLiquidacionesAsync(db, admin.Id, ahora);
            await CrearAsignacionesAsync(db, personal, ahora);
            await CrearCalificacionesAsync(db, personal, azar);
            await CrearSolicitudesAsync(db, admin.Id, organizadores, personal, clientes, ahora);
            await CrearActividadAsync(db, admin.Id, organizadores, personal, clientes, azar, ahora);
            await CopiarImagenesAsync(db, entorno);

            await tx.CommitAsync();
            logger.LogInformation("🧪 Historia de prueba creada: {Eventos} eventos con ventas y {Clientes} clientes", creados.Count, clientes.Count);

            await DatosPrueba.RegistrarResumenAsync(services, logger);
            await RegistrarAnalisisAsync(db, logger);
        }

        // Edades SIMULADAS según la categoría favorita de cada cliente, para probar el análisis por edad.
        // Solo toca cuentas de prueba sin fecha. Algunas quedan sin fecha, como las cuentas antiguas.
        public static async Task AsignarEdadesAsync(IServiceProvider services, ILogger logger)
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var cuentas = await db.Users
                .Where(u => u.Email!.EndsWith(".prueba@example.com") && u.FechaNacimiento == null)
                .ToListAsync();

            var asignadas = 0;
            foreach (var cuenta in cuentas)
            {
                var coincide = System.Text.RegularExpressions.Regex.Match(cuenta.Email!, @"^cliente(\d+)\.");
                var indice = coincide.Success ? int.Parse(coincide.Groups[1].Value) : 0;
                if (indice % 13 == 1)
                    continue;

                var azar = new Random(indice + cuenta.Email!.Length);
                var (minima, maxima) = indice == 0 ? (28, 50) : indice % 17 == 0 ? (15, 17) : (indice % 9 + 1) switch
                {
                    1 => (18, 30), 3 => (18, 28), 6 => (22, 45), 2 => (35, 60), 5 => (28, 55),
                    9 => (40, 68), 8 => (30, 45), 4 => (25, 60), _ => (28, 55)
                };
                cuenta.FechaNacimiento = DateTime.Today.AddYears(-azar.Next(minima, maxima + 1)).AddDays(-azar.Next(0, 365));
                asignadas++;
            }

            await db.SaveChangesAsync();
            logger.LogInformation("🧪 Fechas de nacimiento de prueba asignadas: {Cantidad}", asignadas);

            var hoy = DateTime.Today;
            var analisis = await services.GetRequiredService<Services.Analitica.IAnaliticaService>()
                .AnalisisAsync(new Services.Analitica.FiltroAnalitica { Desde = new DateTime(hoy.Year, 1, 1), Hasta = hoy });
            foreach (var e in analisis.PorEdad)
                logger.LogInformation("🧪 Edad {Rango}: {Personas} personas, {Entradas} entradas, {Asistieron} asistieron ({Asistencia} %), favorita {Favorita} | {Detalle}",
                    e.Rango, e.Personas, e.Entradas, e.Asistieron, e.Asistencia, e.CategoriaFavorita,
                    string.Join(", ", e.PorCategoria.Select(c => $"{c.Categoria}={c.Asistieron}")));
            logger.LogInformation("🧪 Entradas sin edad: {Cantidad}", analisis.EntradasSinEdad);
        }

        // 7 de cada 10 eventos caen en fin de semana
        private static DateTime FechaEvento(int dias, Random azar)
        {
            var fecha = DateTime.Today.AddDays(dias);
            if (azar.NextDouble() < .7)
                while (fecha.DayOfWeek != DayOfWeek.Saturday && fecha.DayOfWeek != DayOfWeek.Sunday)
                    fecha = fecha.AddDays(dias < 0 ? -1 : 1);
            return fecha.AddHours(azar.Next(2) == 0 ? 20 : 16);
        }

        private static double Ocupacion(Plantilla p, DateTime fecha, Random azar)
        {
            var finDeSemana = fecha.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var temporada = fecha.Month is 7 or 12 ? .10 : 0;
            var valor = p.Ocupacion + (finDeSemana ? .08 : -.12) + temporada + (azar.NextDouble() - .5) * .2;
            return Math.Clamp(valor, .12, 1);
        }

        private static async Task<Evento> CrearEventoAsync(ApplicationDbContext db, Plantilla p, string titulo, Sala sala, DateTime fecha, string organizadorId, Random azar)
        {
            var creado = (fecha < DateTime.Now ? fecha : DateTime.Now).AddDays(-70).ToUniversalTime();
            var zonas = p.Zonas.Select((z, i) => Services.Plano.PlanoService.AplicarZona(
                new TipoEntrada { Nombre = z.Zona, Precio = z.Precio, Capacidad = z.Capacidad },
                new TipoEntradaInput { Puerta = $"Puerta {i + 1}" }, p.Plano, i)).ToList();

            var evento = new Evento
            {
                Titulo = titulo,
                Descripcion = "Evento de prueba generado automáticamente para simular el uso de la plataforma.",
                CategoriaId = p.CategoriaId,
                Ubicacion = sala.Lugar,
                Latitud = sala.Lat,
                Longitud = sala.Lon,
                Fecha = fecha,
                Aforo = zonas.Sum(z => z.Capacidad),
                PlantillaPlano = p.Plano,
                TiposEntrada = zonas,
                Estado = EstadoEvento.Aprobado,
                FechaCreacion = creado,
                FechaModeracion = creado.AddDays(1),
                OrganizadorId = organizadorId
            };
            db.Eventos.Add(evento);
            await db.SaveChangesAsync();
            return evento;
        }

        private static async Task CrearVentasAsync(ApplicationDbContext db, Evento evento, double ocupacion, List<Cliente> clientes, List<ApplicationUser> personal,
            decimal porcentaje, Promocion[] promociones, Dictionary<(int, string), Cupon> cuponesLibres, Random azar, DateTime ahora)
        {
            var yaOcurrio = evento.YaOcurrio(ahora);
            var ordenes = new List<Orden>();
            var usados = new List<(Cupon Cupon, Orden Orden, DateTime Fecha)>();
            var zonas = evento.TiposEntrada.OrderByDescending(z => z.Precio).ToList();

            for (var r = 0; r < zonas.Count; r++)
            {
                var zona = zonas[r];
                // Posición de la zona por precio: 0 = la más cara, 1 = intermedia, 2 = la más barata
                var nivel = zonas.Count == 1 ? 2 : r == zonas.Count - 1 ? 2 : r == 0 ? 0 : 1;
                var (factor, antesMin, antesMax, grupoMin, grupoMax, asistencia) = nivel switch
                {
                    0 => (1.05, 12, 60, 1, 2, .97),
                    1 => (.98, 4, 30, 1, 3, .92),
                    _ => (.90, 0, 14, 2, 5, .84)
                };

                var porVender = (int)Math.Round(zona.Capacidad * Math.Min(1, ocupacion * factor));
                var vendidas = 0;
                while (porVender > 0)
                {
                    var cantidad = Math.Min(azar.Next(grupoMin, grupoMax + 1), porVender);
                    porVender -= cantidad;

                    var pagoLocal = HoraDeCompra(evento.Fecha, azar.Next(antesMin, antesMax + 1), azar);
                    if (pagoLocal > ahora)
                        continue;   // en eventos próximos, esta compra todavía no ocurrió

                    var pago = pagoLocal.ToUniversalTime();
                    var cliente = ElegirCliente(clientes, evento.CategoriaId, pago, ahora, azar);
                    if (cliente == null)
                        continue;

                    var subtotal = zona.Precio * cantidad;
                    var descuento = 0m;
                    Cupon? cupon = null;
                    // Por fechas y no con Vigente(): las promociones antiguas ya están inactivas, pero entonces sí se usaban
                    var promo = promociones.FirstOrDefault(x => pagoLocal >= x.FechaInicio && pagoLocal < x.FechaFin.AddDays(1));
                    if (promo != null && azar.NextDouble() < .6 && cuponesLibres.TryGetValue((promo.Id, cliente.Id), out cupon))
                    {
                        descuento = promo.DescuentoPara(subtotal);
                        cuponesLibres.Remove((promo.Id, cliente.Id));
                    }

                    var total = subtotal - descuento;
                    var orden = new Orden
                    {
                        Codigo = CodigoSeguro.Generar(12),
                        UsuarioId = cliente.Id,
                        EventoId = evento.Id,
                        Estado = EstadoOrden.Pagada,
                        Total = total,
                        Descuento = descuento,
                        CuponId = cupon?.Id,
                        PorcentajeComision = porcentaje,
                        Comision = CompraService.CalcularComision(total, porcentaje),
                        FechaCreacion = pago.AddMinutes(-azar.Next(1, 9)),
                        FechaExpiracion = pago.AddMinutes(10),
                        FechaPago = pago,
                        ProveedorPago = "Simulado",
                        ReferenciaPago = "PRUEBA-" + CodigoSeguro.Generar(6)
                    };
                    orden.Items.Add(new OrdenItem { TipoEntradaId = zona.Id, Cantidad = cantidad, PrecioUnitario = zona.Precio });

                    var validador = personal[azar.Next(personal.Count)].Id;
                    for (var n = 0; n < cantidad; n++)
                    {
                        var asistio = yaOcurrio && azar.NextDouble() < asistencia;
                        orden.Entradas.Add(new Entrada
                        {
                            Codigo = CodigoSeguro.Generar(),
                            EventoId = evento.Id,
                            TipoEntradaId = zona.Id,
                            UsuarioId = cliente.Id,
                            Estado = asistio ? EstadoEntrada.Usada : EstadoEntrada.Activa,
                            FechaEmision = pago,
                            FechaUso = asistio ? evento.Fecha.AddMinutes(azar.Next(-90, 75)).ToUniversalTime() : null,
                            ValidadoPorId = asistio ? validador : null
                        });
                    }

                    vendidas += cantidad;
                    ordenes.Add(orden);
                    if (cupon != null)
                        usados.Add((cupon, orden, pago));

                    // Algunas compras no se completan: pago rechazado, reserva expirada o cancelada (sin entradas)
                    if (azar.NextDouble() < .08)
                        ordenes.Add(OrdenFallida(evento.Id, zona, cliente.Id, pago, azar));
                }
                zona.Vendidas = vendidas;
            }

            evento.Visitas = ordenes.Count * azar.Next(5, 14) + azar.Next(40, 300);
            db.Ordenes.AddRange(ordenes);
            await db.SaveChangesAsync();

            foreach (var (cupon, orden, fecha) in usados)
                await db.Cupones.Where(c => c.Id == cupon.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Usado, true)
                    .SetProperty(c => c.OrdenId, orden.Id)
                    .SetProperty(c => c.FechaUso, fecha));
        }

        private static Orden OrdenFallida(int eventoId, TipoEntrada zona, string usuarioId, DateTime fecha, Random azar)
        {
            var orden = new Orden
            {
                Codigo = CodigoSeguro.Generar(12),
                UsuarioId = usuarioId,
                EventoId = eventoId,
                Estado = azar.Next(3) switch { 0 => EstadoOrden.Rechazada, 1 => EstadoOrden.Expirada, _ => EstadoOrden.Cancelada },
                Total = zona.Precio,
                FechaCreacion = fecha.AddMinutes(-azar.Next(15, 90)),
                FechaExpiracion = fecha.AddMinutes(-5)
            };
            orden.Items.Add(new OrdenItem { TipoEntradaId = zona.Id, Cantidad = 1, PrecioUnitario = zona.Precio });
            return orden;
        }

        // Compras sobre todo de noche y al mediodía
        private static DateTime HoraDeCompra(DateTime fechaEvento, int diasAntes, Random azar)
        {
            var tipo = azar.NextDouble();
            var hora = tipo < .6 ? azar.Next(18, 24) : tipo < .85 ? azar.Next(12, 15) : azar.Next(7, 24);
            var pago = fechaEvento.Date.AddDays(-diasAntes).AddHours(hora).AddMinutes(azar.Next(60));
            return pago < fechaEvento ? pago : fechaEvento.AddMinutes(-azar.Next(30, 180));
        }

        // Prefiere clientes a los que les gusta la categoría; algunos dejaron de comprar hace meses (segmentos «en riesgo» y «perdidos»)
        private static Cliente? ElegirCliente(List<Cliente> clientes, int categoriaId, DateTime pagoUtc, DateTime ahora, Random azar)
        {
            for (var intento = 0; intento < 40; intento++)
            {
                var c = clientes[(int)(clientes.Count * Math.Pow(azar.NextDouble(), 1.7))];
                if (c.Registro > pagoUtc)
                    continue;
                if (c.Indice % 5 == 0 && pagoUtc > ahora.AddDays(-150).ToUniversalTime())
                    continue;
                if (intento < 25 && c.Favorita1 != categoriaId && c.Favorita2 != categoriaId && azar.NextDouble() < .7)
                    continue;
                return c;
            }
            return clientes[0];
        }

        // Cancelado como lo hace CancelacionEventoService: entradas anuladas y compras pendientes de reembolso
        private static async Task CrearCanceladoConReembolsosAsync(ApplicationDbContext db, string organizadorId, List<Cliente> clientes, decimal porcentaje, Random azar, DateTime ahora)
        {
            var fecha = DateTime.Today.AddDays(19).AddHours(20);
            var p = Plantillas[0];
            var evento = await CrearEventoAsync(db, p, "Gira Cancelada por Lluvias", Salas[3], fecha, organizadorId, azar);
            evento.Estado = EstadoEvento.Cancelado;
            evento.MotivoCancelacion = "Evento de prueba: cancelado por mal clima en la zona.";
            evento.Visitas = 340;

            foreach (var zona in evento.TiposEntrada)
            {
                for (var i = 0; i < 12; i++)
                {
                    var cliente = clientes[azar.Next(clientes.Count / 2)];
                    var pago = ahora.AddDays(-azar.Next(2, 25)).ToUniversalTime();
                    var orden = new Orden
                    {
                        Codigo = CodigoSeguro.Generar(12), UsuarioId = cliente.Id, EventoId = evento.Id, Estado = EstadoOrden.RequiereReembolso,
                        Total = zona.Precio, PorcentajeComision = porcentaje, Comision = CompraService.CalcularComision(zona.Precio, porcentaje),
                        FechaCreacion = pago.AddMinutes(-4), FechaExpiracion = pago.AddMinutes(10), FechaPago = pago, ProveedorPago = "Simulado", ReferenciaPago = "PRUEBA-" + CodigoSeguro.Generar(6)
                    };
                    orden.Items.Add(new OrdenItem { TipoEntradaId = zona.Id, Cantidad = 1, PrecioUnitario = zona.Precio });
                    orden.Entradas.Add(new Entrada { Codigo = CodigoSeguro.Generar(), EventoId = evento.Id, TipoEntradaId = zona.Id, UsuarioId = cliente.Id, Estado = EstadoEntrada.Anulada, FechaEmision = pago });
                    db.Ordenes.Add(orden);
                }
                zona.Vendidas = 12;
            }
            await db.SaveChangesAsync();
        }

        private static async Task CrearGratuitosAsync(ApplicationDbContext db, List<ApplicationUser> organizadores, List<Cliente> clientes, Random azar, DateTime ahora)
        {
            var planes = new (string Titulo, int Categoria, int Dias, int Aforo, decimal Tarifa, bool Pagado)[]
            {
                ("Cine al Aire Libre", 9, -160, 400, 29, true), ("Concierto Solidario", 1, -95, 800, 59, true),
                ("Feria Escolar de Ciencias", 4, -40, 90, 0, true), ("Yoga en el Parque", 8, -12, 80, 0, true),
                ("Maratón de Lectura", 9, 9, 300, 29, true), ("Festival de Cometas", 8, 24, 600, 59, true),
                ("Noche de Museos", 5, 40, 450, 29, false)
            };

            for (var i = 0; i < planes.Length; i++)
            {
                var g = planes[i];
                var fecha = DateTime.Today.AddDays(g.Dias).AddHours(11);
                var sala = Salas[i % Salas.Length];
                var org = organizadores[i % organizadores.Count].Id;
                var creado = (fecha < ahora ? fecha : ahora).AddDays(-40).ToUniversalTime();
                var evento = new Evento
                {
                    Titulo = g.Titulo, Descripcion = "Evento gratuito de prueba generado automáticamente.", CategoriaId = g.Categoria,
                    Ubicacion = sala.Lugar, Latitud = sala.Lat, Longitud = sala.Lon, Fecha = fecha, Aforo = g.Aforo, PlantillaPlano = PlantillaPlano.Feria,
                    EsGratuito = true, PendientePagoPublicacion = !g.Pagado, Estado = g.Pagado ? EstadoEvento.Aprobado : EstadoEvento.Pendiente,
                    FechaCreacion = creado, FechaModeracion = g.Pagado ? creado.AddDays(1) : null, OrganizadorId = org,
                    Visitas = azar.Next(150, 1400),
                    TiposEntrada = new List<TipoEntrada> { new() { Nombre = "Entrada libre", Precio = 0, Capacidad = g.Aforo, Color = "#22c55e" } }
                };
                db.Eventos.Add(evento);
                await db.SaveChangesAsync();

                db.PagosPublicacion.Add(new PagoPublicacion
                {
                    Codigo = PagoPublicacion.PrefijoCodigo + CodigoSeguro.Generar(8), EventoId = evento.Id, OrganizadorId = org, Monto = g.Tarifa, Aforo = g.Aforo,
                    Estado = g.Pagado ? EstadoPagoPublicacion.Pagado : EstadoPagoPublicacion.PendientePago, Proveedor = g.Pagado ? "Simulado" : null,
                    Referencia = g.Pagado ? "PRUEBA-" + CodigoSeguro.Generar(6) : null, FechaCreacion = creado, FechaPago = g.Pagado ? creado.AddHours(2) : null
                });

                if (g.Pagado)
                    foreach (var c in clientes.OrderBy(_ => azar.Next()).Take(azar.Next(25, 110)))
                        db.InteresesEvento.Add(new InteresEvento { EventoId = evento.Id, UsuarioId = c.Id, Fecha = creado.AddDays(azar.Next(1, 35)) });
                await db.SaveChangesAsync();
            }
        }

        private static async Task CrearDestacadosAsync(ApplicationDbContext db, Random azar, DateTime ahora)
        {
            var paquetes = await db.PaquetesDestacado.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
            var eventos = await db.Eventos.AsNoTracking().Where(e => e.Estado == EstadoEvento.Aprobado && !e.EsGratuito && e.OrganizadorId != null && e.Descripcion.Contains("de prueba"))
                .Select(e => new { e.Id, e.Fecha, e.OrganizadorId }).ToListAsync();

            var pasados = eventos.Where(e => e.Fecha <= ahora).OrderBy(_ => azar.Next()).Take(18);
            var proximos = eventos.Where(e => e.Fecha > ahora).OrderBy(e => e.Fecha).Take(6);

            foreach (var e in pasados.Concat(proximos))
            {
                var paquete = paquetes[azar.Next(paquetes.Count)];
                var inicio = e.Fecha > ahora ? ahora.AddDays(-1) : e.Fecha.AddDays(-paquete.Dias - azar.Next(1, 10));
                var fin = inicio.AddDays(paquete.Dias) < e.Fecha ? inicio.AddDays(paquete.Dias) : e.Fecha;
                db.ContratosDestacado.Add(new ContratoDestacado
                {
                    Codigo = ContratoDestacado.PrefijoCodigo + CodigoSeguro.Generar(9), EventoId = e.Id, PaqueteId = paquete.Id, OrganizadorId = e.OrganizadorId!,
                    Precio = paquete.Precio, Dias = paquete.Dias, Prioridad = paquete.Prioridad, Estado = EstadoContrato.Activo,
                    FechaCreacion = inicio.ToUniversalTime(), FechaPago = inicio.ToUniversalTime(), FechaInicio = inicio, FechaFin = fin,
                    ProveedorPago = "Simulado", ReferenciaPago = "PRUEBA-" + CodigoSeguro.Generar(6)
                });
            }

            // Uno sin pagar, para ver ese estado
            var pendiente = eventos.Where(e => e.Fecha > ahora).OrderByDescending(e => e.Fecha).First();
            db.ContratosDestacado.Add(new ContratoDestacado
            {
                Codigo = ContratoDestacado.PrefijoCodigo + CodigoSeguro.Generar(9), EventoId = pendiente.Id, PaqueteId = paquetes[0].Id, OrganizadorId = pendiente.OrganizadorId!,
                Precio = paquetes[0].Precio, Dias = paquetes[0].Dias, Prioridad = paquetes[0].Prioridad, Estado = EstadoContrato.PendientePago, FechaCreacion = DateTime.UtcNow.AddHours(-3)
            });
            await db.SaveChangesAsync();
        }

        // Pagos al organizador de lo vendido, salvo los eventos de los últimos 15 días (quedan por liquidar)
        private static async Task CrearLiquidacionesAsync(ApplicationDbContext db, string adminId, DateTime ahora)
        {
            var limite = ahora.AddDays(-15);
            var filas = await db.Ordenes.AsNoTracking()
                .Where(o => o.Estado == EstadoOrden.Pagada && o.Evento!.Fecha < limite && o.Evento.OrganizadorId != null && o.Evento.Descripcion.Contains("de prueba"))
                .Select(o => new { o.EventoId, o.Evento!.OrganizadorId, o.Evento.Fecha, o.Total, o.Comision })
                .ToListAsync();

            foreach (var g in filas.GroupBy(f => new { f.EventoId, f.OrganizadorId, f.Fecha }))
                db.Liquidaciones.Add(new Liquidacion
                {
                    OrganizadorId = g.Key.OrganizadorId!, EventoId = g.Key.EventoId, Monto = g.Sum(f => f.Total - f.Comision),
                    Referencia = "OP-PRUEBA-" + g.Key.EventoId, Nota = "Liquidación de prueba", Fecha = g.Key.Fecha.AddDays(7).ToUniversalTime(), RegistradoPorId = adminId
                });
            await db.SaveChangesAsync();
        }

        private static async Task CrearAsignacionesAsync(ApplicationDbContext db, List<ApplicationUser> personal, DateTime ahora)
        {
            var validaciones = await db.Entradas.AsNoTracking().Where(e => e.ValidadoPorId != null)
                .Select(e => new { e.EventoId, e.ValidadoPorId }).Distinct().ToListAsync();
            var fechas = await db.Eventos.AsNoTracking().Select(e => new { e.Id, e.Fecha, e.Estado, e.EsGratuito }).ToListAsync();
            var fechaDe = fechas.ToDictionary(e => e.Id, e => e.Fecha);

            foreach (var v in validaciones)
                db.AsignacionesAcceso.Add(new AsignacionAcceso
                {
                    UsuarioId = v.ValidadoPorId!, EventoId = v.EventoId,
                    FechaAsignacion = fechaDe[v.EventoId].AddDays(-3).ToUniversalTime(),
                    FinalizadaEn = fechaDe[v.EventoId].AddHours(Evento.HorasValidezEntradas)
                });

            // La cuenta de puerta queda asignada a los próximos eventos, para poder probar el escáner
            foreach (var e in fechas.Where(e => e.Fecha > ahora && e.Estado == EstadoEvento.Aprobado && !e.EsGratuito))
                db.AsignacionesAcceso.Add(new AsignacionAcceso { UsuarioId = personal[0].Id, EventoId = e.Id, FechaAsignacion = DateTime.UtcNow.AddDays(-1) });

            await db.SaveChangesAsync();
        }

        private static async Task CrearCalificacionesAsync(ApplicationDbContext db, List<ApplicationUser> personal, Random azar)
        {
            var malo = personal[3].Id;
            var comentariosBuenos = new[] { "Muy amable", "Ingreso rápido", "Buena atención", null, null };
            var comentariosMalos = new[] { "Hubo mucha demora", "Trato poco amable", "No sabía indicar la zona", null };

            var entradas = await db.Entradas.AsNoTracking().Where(e => e.Estado == EstadoEntrada.Usada && e.ValidadoPorId != null)
                .Select(e => new { e.Id, e.EventoId, e.ValidadoPorId, e.UsuarioId, e.FechaUso }).ToListAsync();

            foreach (var e in entradas.Where(_ => azar.NextDouble() < .07))
            {
                var esMalo = e.ValidadoPorId == malo;
                var puntaje = esMalo ? azar.Next(1, 4) : azar.NextDouble() < .75 ? 5 : azar.Next(3, 5);
                var comentarios = esMalo ? comentariosMalos : comentariosBuenos;
                db.CalificacionesPersonal.Add(new CalificacionPersonal
                {
                    EntradaId = e.Id, EventoId = e.EventoId, PersonalId = e.ValidadoPorId!, UsuarioId = e.UsuarioId, Puntaje = puntaje,
                    Comentario = comentarios[azar.Next(comentarios.Length)], Fecha = e.FechaUso!.Value.AddDays(azar.Next(1, 6))
                });
            }
            await db.SaveChangesAsync();
        }

        // Documentos y teléfonos de relleno, que no corresponden a nadie
        private static async Task CrearSolicitudesAsync(ApplicationDbContext db, string adminId, List<ApplicationUser> organizadores, List<ApplicationUser> personal, List<Cliente> clientes, DateTime ahora)
        {
            var hoy = DateTime.UtcNow;
            SolicitudOrganizador Solicitud(string usuarioId, string nombre, int n, TipoOrganizador tipo, EstadoSolicitud estado, int diasAtras, string? motivo = null) => new()
            {
                UsuarioId = usuarioId, Tipo = tipo, NombreOrganizador = nombre,
                Documento = tipo == TipoOrganizador.Empresa ? $"2000000000{n}" : $"0000000{n}", Telefono = $"90000000{n}",
                DescripcionEventos = "Solicitud de prueba: conciertos y ferias locales para público general.", WebORedes = "https://example.com/organizador",
                PlazoSolicitado = PlazoOrganizador.Indefinido, Estado = estado, FechaSolicitud = hoy.AddDays(-diasAtras),
                FechaRespuesta = estado == EstadoSolicitud.Pendiente ? null : hoy.AddDays(-diasAtras + 2),
                RespondidaPorId = estado == EstadoSolicitud.Pendiente ? null : adminId,
                PlazoAprobado = estado == EstadoSolicitud.Aprobada ? PlazoOrganizador.Indefinido : null, MotivoRechazo = motivo
            };

            db.SolicitudesOrganizador.AddRange(
                Solicitud(organizadores[0].Id, "Organizador Prueba", 1, TipoOrganizador.PersonaNatural, EstadoSolicitud.Aprobada, 440),
                Solicitud(organizadores[1].Id, "Organizador Prueba 2", 2, TipoOrganizador.PersonaNatural, EstadoSolicitud.Aprobada, 430),
                Solicitud(organizadores[2].Id, "Producciones Prueba SAC", 3, TipoOrganizador.Empresa, EstadoSolicitud.Aprobada, 420),
                Solicitud(organizadores[3].Id, "Asociación Cultural Prueba", 4, TipoOrganizador.Empresa, EstadoSolicitud.Aprobada, 300),
                Solicitud(clientes[10].Id, "Cliente Prueba 11", 5, TipoOrganizador.PersonaNatural, EstadoSolicitud.Rechazada, 60, "Faltan datos del tipo de eventos."),
                Solicitud(clientes[20].Id, "Cliente Prueba 21", 6, TipoOrganizador.PersonaNatural, EstadoSolicitud.Pendiente, 3),
                Solicitud(clientes[30].Id, "Eventos Prueba EIRL", 7, TipoOrganizador.Empresa, EstadoSolicitud.Pendiente, 1));

            var proximos = await db.Eventos.AsNoTracking()
                .Where(e => e.Estado == EstadoEvento.Aprobado && !e.EsGratuito && e.OrganizadorId != null && e.Fecha > ahora.AddDays(3) && e.Descripcion.Contains("de prueba"))
                .OrderBy(e => e.Fecha).Take(3).Select(e => new { e.Id, e.OrganizadorId }).ToListAsync();

            for (var i = 0; i < proximos.Count; i++)
            {
                var estado = i switch { 0 => EstadoSolicitud.Pendiente, 1 => EstadoSolicitud.Aprobada, _ => EstadoSolicitud.Rechazada };
                db.SolicitudesPersonal.Add(new SolicitudPersonal
                {
                    OrganizadorId = proximos[i].OrganizadorId!, EventoId = proximos[i].Id, Correos = $"{personal[1].Email}, {personal[2].Email}",
                    Mensaje = "Solicitud de prueba para el control de puerta.", Estado = estado, FechaSolicitud = hoy.AddDays(-2 - i),
                    FechaRespuesta = estado == EstadoSolicitud.Pendiente ? null : hoy.AddDays(-1),
                    RespondidaPorId = estado == EstadoSolicitud.Pendiente ? null : adminId,
                    MotivoRechazo = estado == EstadoSolicitud.Rechazada ? "Cuentas sin experiencia registrada." : null
                });
            }
            await db.SaveChangesAsync();
        }

        private static async Task CrearActividadAsync(ApplicationDbContext db, string adminId, List<ApplicationUser> organizadores, List<ApplicationUser> personal, List<Cliente> clientes, Random azar, DateTime ahora)
        {
            var eventos = await db.Eventos.AsNoTracking().Where(e => e.Estado == EstadoEvento.Aprobado)
                .Select(e => new { e.Id, e.Titulo, e.Fecha, e.CategoriaId, Categoria = e.Categoria!.Nombre }).ToListAsync();
            var proximos = eventos.Where(e => e.Fecha > ahora).OrderBy(e => e.Fecha).ToList();
            var hoy = DateTime.UtcNow;

            // ---------- Búsquedas y fichas vistas: alimentan las recomendaciones ----------
            foreach (var c in clientes)
            {
                var preferidos = eventos.Where(e => e.CategoriaId == c.Favorita1 || e.CategoriaId == c.Favorita2).ToList();
                if (preferidos.Count == 0) preferidos = eventos;
                for (var i = azar.Next(4, 14); i > 0; i--)
                {
                    var e = preferidos[azar.Next(preferidos.Count)];
                    var fecha = hoy.AddDays(-azar.Next(1, 180));
                    db.VisualizacionesEvento.Add(new VisualizacionEvento { EventoId = e.Id, UsuarioId = c.Id, Fecha = fecha });
                    if (i % 3 == 0)
                        db.HistorialBusquedas.Add(new HistorialBusqueda { UsuarioId = c.Id, Termino = e.Categoria, Fecha = fecha });
                }
            }

            // ---------- Recordatorios: enviados y por enviar ----------
            foreach (var c in clientes.Take(30))
            {
                var e = proximos[azar.Next(proximos.Count)];
                db.RecordatoriosEvento.Add(new RecordatorioEvento { EventoId = e.Id, UsuarioId = c.Id, FechaCreacion = hoy.AddDays(-azar.Next(1, 10)) });
            }

            // ---------- Notificaciones de la campana ----------
            void Avisar(string usuarioId, string icono, string titulo, string mensaje, string url, int horas, bool leida) =>
                db.Notificaciones.Add(new Notificacion { UsuarioId = usuarioId, Icono = icono, Titulo = titulo, Mensaje = mensaje, Url = url, Fecha = hoy.AddHours(-horas), Leida = leida });

            Avisar(adminId, "📝", "Eventos por aprobar", "Hay eventos esperando tu revisión.", "/Admin", 2, false);
            Avisar(adminId, "🧑‍💼", "Nueva solicitud de organizador", "Un usuario pidió organizar eventos.", "/Solicitudes", 20, false);
            Avisar(adminId, "🚪", "Solicitud de personal", "Un organizador pidió personal de puerta.", "/Admin", 50, true);
            Avisar(organizadores[0].Id, "✅", "Evento aprobado", "Tu evento ya está publicado.", "/Eventos/MisEventos", 5, false);
            Avisar(organizadores[0].Id, "🎟️", "Buenas ventas", "Tu evento superó el 50 % de ocupación.", "/Panel", 30, true);
            Avisar(personal[0].Id, "🚪", "Nuevo evento asignado", "Te asignaron el control de puerta de un evento.", "/Acceso", 8, false);
            foreach (var c in clientes.Take(10))
            {
                Avisar(c.Id, "🔔", "Tu evento es pronto", "Revisa tu entrada y cómo llegar.", "/Entradas", azar.Next(1, 40), false);
                Avisar(c.Id, "🎁", "Tienes un cupón", "Usa tu descuento antes de que venza.", "/Perfil", azar.Next(40, 200), true);
            }

            db.AlertasAsistente.Add(new AlertaAsistente { UsuarioId = adminId, Tipo = TipoAlertaAsistente.EventosPorAprobar, FechaCreacion = hoy.AddDays(-20), UltimaRevision = hoy.AddHours(-1) });

            // ---------- Preguntas al asistente: métrica del 80 % de resueltas ----------
            var resueltas = new[]
            {
                ("¿Dónde queda el evento?", "Ubicacion"), ("¿A qué hora empieza?", "Horario"), ("¿Cuánto cuesta la entrada VIP?", "Precio"),
                ("¿Quedan entradas disponibles?", "Precio"), ("¿Cómo compro una entrada?", "Compra"), ("¿Dónde veo mi código QR?", "Entradas"),
                ("¿Qué eventos hay este fin de semana?", "Busqueda"), ("Recomiéndame un concierto", "Recomendacion")
            };
            var sinResolver = new[]
            {
                "¿Hay estacionamiento para motos?", "¿Puedo llevar a mi mascota?", "¿Dan factura con RUC?", "¿Aceptan pago en cuotas?", "¿Hay descuento para estudiantes?"
            };
            for (var i = 0; i < 700; i++)
            {
                var resuelta = azar.NextDouble() < .84;
                var (pregunta, intencion) = resuelta ? resueltas[azar.Next(resueltas.Length)] : (sinResolver[azar.Next(sinResolver.Length)], "SinRespuesta");
                db.ConsultasAsistente.Add(new ConsultaAsistente
                {
                    Pregunta = pregunta, Intencion = intencion, Resuelta = resuelta, ConSesion = azar.Next(2) == 0, Fecha = hoy.AddMinutes(-azar.Next(30, 90 * 24 * 60))
                });
            }

            // ---------- Auditoría de consultas analíticas ----------
            for (var i = 0; i < 40; i++)
                db.AuditoriasAnalitica.Add(new AuditoriaAnalitica
                {
                    AdministradorId = adminId, Consulta = i % 2 == 0 ? "Filtro de clientes (prueba)" : "Variabilidad y atípicos (prueba)",
                    FuenteDatos = "Ordenes, Eventos", Fecha = hoy.AddDays(-azar.Next(1, 200))
                });

            await db.SaveChangesAsync();
        }

        // Una copia por evento en uploads: al borrar un evento de prueba no se toca la imagen original de la categoría
        private static async Task CopiarImagenesAsync(ApplicationDbContext db, IWebHostEnvironment entorno)
        {
            var eventos = await db.Eventos.AsNoTracking()
                .Where(e => !e.Imagenes.Any() && e.Categoria!.Imagen != null && e.Descripcion.Contains("de prueba"))
                .Select(e => new { e.Id, Imagen = e.Categoria!.Imagen! }).ToListAsync();

            var carpeta = Path.Combine(entorno.WebRootPath, "uploads", "prueba");
            Directory.CreateDirectory(carpeta);

            foreach (var e in eventos)
            {
                var origen = Path.Combine(entorno.WebRootPath, e.Imagen.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(origen))
                    continue;
                var nombre = $"evento-{e.Id}{Path.GetExtension(origen)}";
                File.Copy(origen, Path.Combine(carpeta, nombre), overwrite: true);
                db.EventoImagenes.Add(new EventoImagen { EventoId = e.Id, Ruta = $"/uploads/prueba/{nombre}", Orden = 0 });
            }
            await db.SaveChangesAsync();
        }

        // Comprobación en el log: lo que debería poder mostrar un análisis por tipo de zona
        private static async Task RegistrarAnalisisAsync(ApplicationDbContext db, ILogger logger)
        {
            var items = await db.OrdenItems.AsNoTracking()
                .Where(i => i.Orden!.Estado == EstadoOrden.Pagada && i.Orden.FechaPago != null)
                .Select(i => new { Zona = i.TipoEntrada!.Nombre, i.Cantidad, i.PrecioUnitario, i.Orden!.FechaPago, i.Orden.Evento!.Fecha, i.TipoEntradaId })
                .ToListAsync();
            var entradas = await db.Entradas.AsNoTracking().Where(e => e.Evento!.Fecha <= DateTime.Now)
                .Select(e => new { e.TipoEntrada!.Nombre, e.Estado }).ToListAsync();

            foreach (var g in items.GroupBy(i => i.Zona).OrderByDescending(g => g.Sum(i => i.Cantidad * i.PrecioUnitario)).Take(10))
            {
                var deZona = entradas.Where(e => e.Nombre == g.Key).ToList();
                var asistencia = deZona.Count == 0 ? 0 : 100.0 * deZona.Count(e => e.Estado == EstadoEntrada.Usada) / deZona.Count;
                logger.LogInformation("🧪 Zona {Zona}: {Entradas} entradas, S/ {Ingresos}, {PorCompra:0.0} por compra, {Anticipacion:0} días de anticipación, asistencia {Asistencia:0.0} %",
                    g.Key, g.Sum(i => i.Cantidad), g.Sum(i => i.Cantidad * i.PrecioUnitario), g.Average(i => (double)i.Cantidad),
                    g.Average(i => (i.Fecha - i.FechaPago!.Value.ToLocalTime()).TotalDays), asistencia);
            }
        }
    }
}
