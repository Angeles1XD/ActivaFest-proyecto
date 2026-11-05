using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ActivaFest.Models;
using ActivaFest.Services.Analitica;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Entradas;
using ActivaFest.Services.Plano;

namespace ActivaFest.Data
{
    // =============================
    // 🧪 DATOS DE PRUEBA (solo desarrollo)
    // =============================
    // Se generan a pedido, nunca solos:  dotnet run --project ActivaFest --launch-profile http -- --datos-prueba
    // Crea eventos pasados con ventas y asistencia, eventos próximos con ventas en curso y casos de moderación.
    // Las fechas son relativas al día en que se ejecuta. Para empezar de cero, borra app.db (con la app detenida).
    public static class DatosPrueba
    {
        public const string Clave = "Prueba123!";
        public const string CorreoOrganizador = "organizador.prueba@example.com";
        public const string CorreoOrganizador2 = "organizador2.prueba@example.com";
        public const string CorreoPuerta = "puerta.prueba@example.com";
        private const int CantidadClientes = 60;
        private const string Aviso = "Evento de prueba generado automáticamente. ";

        private record Zona(string Nombre, decimal Precio, int Capacidad, int? Filas = null, int? PorFila = null);

        // Ocupacion y Asistencia van de 0 a 1 y describen cómo le fue a cada evento
        private record Plan(string Titulo, int CategoriaId, string Ubicacion, double Lat, double Lon, int Dias, int Hora,
            PlantillaPlano Plantilla, double Ocupacion, double Asistencia, string Historia, Zona[] Zonas,
            EstadoEvento Estado = EstadoEvento.Aprobado, string? Motivo = null);

        private static readonly Plan[] Planes =
        {
            // ---------- Eventos pasados: unos muy buenos, otros regulares y alguno malo ----------
            new("Festival Sonidos del Valle", 3, "Parque Central, Cusco", -13.5170, -71.9785, -74, 16, PlantillaPlano.CampoAbierto, .96, .93,
                "Gran éxito: casi lleno y con muy buena asistencia.",
                new[] { new Zona("VIP", 180, 80), new Zona("Preferencial", 110, 170), new Zona("General", 60, 250) }),
            new("Rock en la Plaza", 1, "Explanada Norte, Lima", -12.0464, -77.0428, -61, 20, PlantillaPlano.CampoAbierto, .88, .90,
                "Buen resultado en ventas y asistencia.",
                new[] { new Zona("Preferencial", 120, 120), new Zona("General", 70, 280) }),
            new("Feria Sabores del Sur", 7, "Campo Ferial, Arequipa", -16.4090, -71.5375, -48, 11, PlantillaPlano.Feria, .72, .85,
                "Resultado correcto, con entradas económicas.",
                new[] { new Zona("Entrada general", 15, 450) }),
            new("Comedia en el Coliseo", 2, "Coliseo Municipal, Trujillo", -8.1116, -79.0288, -39, 19, PlantillaPlano.Coliseo, .55, .88,
                "Vendió a medias, pero quien compró sí asistió.",
                new[] { new Zona("Campo", 90, 100), new Zona("Tribuna", 50, 200) }),
            new("Expo Arte Joven", 5, "Galería Moderna, Lima", -12.1211, -77.0297, -27, 10, PlantillaPlano.Feria, .28, .62,
                "Fracaso: pocas ventas y mucha gente que no llegó.",
                new[] { new Zona("Entrada general", 25, 300) }),
            new("Clásico Regional de Fútbol", 6, "Estadio del Sur, Arequipa", -16.3989, -71.5369, -18, 15, PlantillaPlano.Estadio, .99, .95,
                "Prácticamente agotado.",
                new[] { new Zona("Occidente", 95, 200), new Zona("Oriente", 70, 250), new Zona("Popular", 35, 350) }),
            new("Domingo Familiar en el Parque", 8, "Parque Zonal, Lima", -12.0700, -77.0100, -9, 10, PlantillaPlano.CampoAbierto, .64, .78,
                "Resultado regular, con varias entradas sin usar.",
                new[] { new Zona("Entrada general", 20, 260) }),
            new("Noche de Cumbia", 1, "Arena Sur, Lima", -12.1500, -76.9800, -2, 21, PlantillaPlano.Discoteca, .81, .91,
                "Evento reciente: cuenta en el período «Este mes».",
                new[] { new Zona("Pista", 55, 220), new Zona("VIP", 130, 60) }),

            // ---------- Eventos próximos: con ventas en curso, y uno sin ventas para probar la compra ----------
            new("Concierto Luces de Noviembre", 1, "Explanada Norte, Lima", -12.0464, -77.0428, 5, 20, PlantillaPlano.CampoAbierto, .70, 0,
                "Próximo y vendiendo bien.",
                new[] { new Zona("VIP", 200, 60), new Zona("General", 80, 240) }),
            new("Teatro: La Última Función", 2, "Teatro Principal, Lima", -12.0500, -77.0330, 12, 19, PlantillaPlano.Teatro, 0, 0,
                "Sin ventas: sirve para probar la elección de asientos numerados.",
                new[] { new Zona("Platea", 90, 60, 6, 10), new Zona("Balcón", 50, 48, 4, 12) }),
            new("Festival Costa Viva", 3, "Malecón, Chiclayo", -6.7714, -79.8409, 20, 15, PlantillaPlano.CampoAbierto, .45, 0,
                "Ventas a medio camino.",
                new[] { new Zona("Preferencial", 100, 100), new Zona("General", 55, 300) }),
            new("Copa Ciudad: Gran Final", 6, "Estadio del Sur, Arequipa", -16.3989, -71.5369, 33, 16, PlantillaPlano.Estadio, .20, 0,
                "Recién empieza a vender.",
                new[] { new Zona("Occidente", 85, 150), new Zona("Popular", 30, 300) }),
            new("Feria del Libro Andino", 9, "Centro Cultural, Cusco", -13.5200, -71.9700, 45, 10, PlantillaPlano.Feria, .10, 0,
                "Baja ocupación por ahora.",
                new[] { new Zona("Entrada general", 10, 400) }),

            // ---------- Moderación: un caso de cada estado ----------
            new("Encuentro de Emprendedores", 10, "Centro de Convenciones, Lima", -12.0900, -77.0200, 40, 9, PlantillaPlano.Feria, 0, 0,
                "Pendiente: el administrador puede aprobarlo o rechazarlo.",
                new[] { new Zona("Entrada general", 30, 150) }, EstadoEvento.Pendiente),
            new("Rifa sin datos completos", 10, "Por confirmar, Lima", -12.0464, -77.0428, 30, 18, PlantillaPlano.Feria, 0, 0,
                "Ejemplo de evento rechazado.",
                new[] { new Zona("Entrada general", 10, 100) }, EstadoEvento.Rechazado, "Faltan datos del lugar y del responsable."),
            new("Carrera Nocturna 10K", 6, "Costa Verde, Lima", -12.1300, -77.0400, 25, 20, PlantillaPlano.CampoAbierto, 0, 0,
                "Ejemplo de evento cancelado, sin entradas vendidas.",
                new[] { new Zona("Corredor", 40, 200) }, EstadoEvento.Cancelado, "El municipio no otorgó el permiso."),
        };

        public static async Task GenerarAsync(IServiceProvider services, ILogger logger)
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var comision = services.GetRequiredService<IOptions<ComisionesOptions>>().Value.Porcentaje;

            var organizador = await UsuarioAsync(userManager, CorreoOrganizador, "Organizador Prueba", Roles.Organizador);
            var organizador2 = await UsuarioAsync(userManager, CorreoOrganizador2, "Organizador Prueba 2", Roles.Organizador);
            var puerta = await UsuarioAsync(userManager, CorreoPuerta, "Puerta Prueba", Roles.ControlAcceso);

            if (await db.Eventos.AnyAsync(e => e.OrganizadorId == organizador.Id))
            {
                logger.LogInformation("🧪 Los datos de prueba ya existían: no se creó nada nuevo");
                await RegistrarResumenAsync(services, logger);
                return;
            }

            var clientes = new List<ApplicationUser>();
            for (var i = 1; i <= CantidadClientes; i++)
                clientes.Add(await UsuarioAsync(userManager, $"cliente{i:00}.prueba@example.com", $"Cliente Prueba {i:00}", null, i % 3 != 0));

            // Semilla fija: los resultados son repetibles
            var azar = new Random(2026);
            var ahora = DateTime.Now;

            await using var tx = await db.Database.BeginTransactionAsync();
            for (var i = 0; i < Planes.Length; i++)
            {
                var evento = await CrearEventoAsync(db, Planes[i], i % 3 == 2 ? organizador2.Id : organizador.Id, ahora);
                await CrearVentasAsync(db, evento, Planes[i], clientes, puerta.Id, comision, azar, ahora);
                db.ChangeTracker.Clear();
            }
            await tx.CommitAsync();

            logger.LogInformation("🧪 Datos de prueba creados: {Eventos} eventos y {Clientes} clientes", Planes.Length, clientes.Count);
            await RegistrarResumenAsync(services, logger);
        }

        internal static async Task<ApplicationUser> UsuarioAsync(UserManager<ApplicationUser> usuarios, string correo, string nombre, string? rol, bool promociones = false)
        {
            var usuario = await usuarios.FindByEmailAsync(correo);
            if (usuario != null)
                return usuario;

            usuario = new ApplicationUser
            {
                UserName = correo,
                Email = correo,
                EmailConfirmed = true,
                NombreCompleto = nombre,
                AceptaTratamientoDatos = true,
                AceptaPromociones = promociones,
                FechaRegistro = DateTime.UtcNow.AddMonths(-6)
            };

            var resultado = await usuarios.CreateAsync(usuario, Clave);
            if (!resultado.Succeeded)
                throw new InvalidOperationException($"No se pudo crear {correo}: {string.Join(", ", resultado.Errors.Select(e => e.Description))}");

            await usuarios.AddToRoleAsync(usuario, Roles.Asistente);
            if (rol != null)
                await usuarios.AddToRoleAsync(usuario, rol);
            return usuario;
        }

        private static async Task<Evento> CrearEventoAsync(ApplicationDbContext db, Plan plan, string organizadorId, DateTime ahora)
        {
            var fecha = DateTime.Today.AddDays(plan.Dias).AddHours(plan.Hora);
            var creado = (fecha < ahora ? fecha : ahora).AddDays(-45).ToUniversalTime();

            var zonas = plan.Zonas.Select((z, i) => PlanoService.AplicarZona(
                new TipoEntrada { Nombre = z.Nombre, Precio = z.Precio, Capacidad = z.Capacidad },
                new TipoEntradaInput { Puerta = $"Puerta {i + 1}", Filas = z.Filas, AsientosPorFila = z.PorFila },
                plan.Plantilla, i)).ToList();

            var evento = new Evento
            {
                Titulo = plan.Titulo,
                Descripcion = Aviso + plan.Historia,
                CategoriaId = plan.CategoriaId,
                Ubicacion = plan.Ubicacion,
                Latitud = plan.Lat,
                Longitud = plan.Lon,
                Fecha = fecha,
                Aforo = zonas.Sum(z => z.Capacidad),
                PlantillaPlano = plan.Plantilla,
                TiposEntrada = zonas,
                Estado = plan.Estado,
                MotivoRechazo = plan.Estado == EstadoEvento.Rechazado ? plan.Motivo : null,
                MotivoCancelacion = plan.Estado == EstadoEvento.Cancelado ? plan.Motivo : null,
                FechaCreacion = creado,
                FechaModeracion = plan.Estado == EstadoEvento.Pendiente ? null : creado.AddDays(1),
                OrganizadorId = organizadorId
            };

            db.Eventos.Add(evento);
            if (plan.Plantilla == PlantillaPlano.Teatro)
                db.AddRange(PlanoService.GenerarAsientos(zonas));

            await db.SaveChangesAsync();
            return evento;
        }

        private static async Task CrearVentasAsync(ApplicationDbContext db, Evento evento, Plan plan, List<ApplicationUser> clientes,
            string puertaId, decimal porcentajeComision, Random azar, DateTime ahora)
        {
            var yaOcurrio = evento.YaOcurrio(ahora);
            var ordenes = new List<Orden>();

            foreach (var zona in evento.TiposEntrada)
            {
                var pendientes = (int)Math.Round(zona.Capacidad * plan.Ocupacion);
                zona.Vendidas = pendientes;

                while (pendientes > 0)
                {
                    var cantidad = Math.Min(azar.Next(1, 5), pendientes);
                    pendientes -= cantidad;

                    // Unos pocos clientes compran seguido; así los segmentos RFM salen variados
                    var cliente = clientes[(int)(clientes.Count * Math.Pow(azar.NextDouble(), 2))];
                    var pago = FechaPago(evento, yaOcurrio, azar, ahora);
                    var total = zona.Precio * cantidad;

                    var orden = new Orden
                    {
                        Codigo = CodigoSeguro.Generar(12),
                        UsuarioId = cliente.Id,
                        EventoId = evento.Id,
                        Estado = EstadoOrden.Pagada,
                        Total = total,
                        PorcentajeComision = porcentajeComision,
                        Comision = CompraService.CalcularComision(total, porcentajeComision),
                        FechaCreacion = pago.AddMinutes(-azar.Next(1, 9)),
                        FechaExpiracion = pago.AddMinutes(10),
                        FechaPago = pago,
                        ProveedorPago = "Simulado",
                        ReferenciaPago = "PRUEBA-" + CodigoSeguro.Generar(6)
                    };
                    orden.Items.Add(new OrdenItem { TipoEntradaId = zona.Id, Cantidad = cantidad, PrecioUnitario = zona.Precio });

                    for (var n = 0; n < cantidad; n++)
                    {
                        var asistio = yaOcurrio && azar.NextDouble() < plan.Asistencia;
                        orden.Entradas.Add(new Entrada
                        {
                            Codigo = CodigoSeguro.Generar(),
                            EventoId = evento.Id,
                            TipoEntradaId = zona.Id,
                            UsuarioId = cliente.Id,
                            Estado = asistio ? EstadoEntrada.Usada : EstadoEntrada.Activa,
                            FechaEmision = pago,
                            FechaUso = asistio ? evento.Fecha.AddMinutes(azar.Next(-90, 75)).ToUniversalTime() : null,
                            ValidadoPorId = asistio ? puertaId : null
                        });
                    }

                    ordenes.Add(orden);
                }
            }

            // De cada 100 visitas a la ficha, solo una parte termina en compra (indicador de conversión)
            evento.Visitas = ordenes.Count * azar.Next(4, 12) + azar.Next(40, 200);

            db.Ordenes.AddRange(ordenes);
            await db.SaveChangesAsync();
        }

        // Las ventas se concentran cerca de la fecha del evento; en los próximos, cerca de hoy. Siempre en UTC y nunca en el futuro.
        private static DateTime FechaPago(Evento evento, bool yaOcurrio, Random azar, DateTime ahora)
        {
            var referencia = yaOcurrio ? evento.Fecha : ahora;
            var minutosAntes = (int)(Math.Pow(azar.NextDouble(), 2) * 30 * 24 * 60) + 10;
            return referencia.AddMinutes(-minutosAntes).ToUniversalTime();
        }

        // Pasa los datos por el mismo servicio que usa el dashboard y deja el resultado en el log
        internal static async Task RegistrarResumenAsync(IServiceProvider services, ILogger logger)
        {
            var analitica = services.GetRequiredService<IAnaliticaService>();
            foreach (var rango in new[] { "mes", "30d", "anio" })
            {
                var d = await analitica.DashboardAsync(FiltroAnalitica.DeRango(rango, null, null, null, DateTime.Today).Filtro);
                logger.LogInformation(
                    "🧪 Dashboard [{Rango}] {Desde:yyyy-MM-dd} a {Hasta:yyyy-MM-dd}: ingresos S/ {Ingresos}, entradas {Entradas}, compras {Compras}, comisiones S/ {Comisiones}, ocupación {Ocupacion} %, asistencia {Asistencia} %, conversión {Conversion} %, realizados {Realizados}, categorías {Categorias}",
                    rango, d.Desde, d.Hasta, d.Ingresos, d.EntradasVendidas, d.Compras, d.Comisiones, d.Ocupacion, d.Asistencia, d.Conversion, d.EventosRealizados, d.VentasPorCategoria.Count);
            }
        }
    }
}
