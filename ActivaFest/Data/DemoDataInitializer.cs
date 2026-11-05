using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Models;
using ActivaFest.Services.Plano;

namespace ActivaFest.Data;

public static class DemoDataInitializer
{
    private const string PrefijoEventoHistorico = "Histórico demo · ";
    private const string ProveedorDemostracion = "Demo académico";
    private static readonly DateTime InicioPeriodo = new(2026, 7, 1);
    private static readonly DateTime FinPeriodoEventosNuevos = new(2027, 12, 31);

    private sealed record EventoNuevo(
        string Categoria, string Titulo, string Descripcion, string Ubicacion, string Hora,
        int Aforo, decimal Precio, PlantillaPlano Plano);

    public static async Task<string> SeedOrganizadorDemoAsync(
        UserManager<ApplicationUser> userManager, ILogger logger)
    {
        const string email = "organizador@demo.activafest.test";
        var organizador = await userManager.FindByEmailAsync(email);
        if (organizador == null)
        {
            organizador = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                NombreCompleto = "Organizador de demostración"
            };
            var creado = await userManager.CreateAsync(organizador, $"Demo{Guid.NewGuid():N}A1");
            if (!creado.Succeeded)
                throw new InvalidOperationException($"No se pudo crear el organizador de demostración: {string.Join(", ", creado.Errors.Select(e => e.Description))}");
        }

        if (!await userManager.IsInRoleAsync(organizador, Roles.Organizador))
        {
            var asignado = await userManager.AddToRoleAsync(organizador, Roles.Organizador);
            if (!asignado.Succeeded)
                throw new InvalidOperationException($"No se pudo asignar el rol de organizador de demostración: {string.Join(", ", asignado.Errors.Select(e => e.Description))}");
        }

        logger.LogInformation("Cuenta organizadora de demostración preparada.");
        return organizador.Id;
    }

    private static readonly EventoNuevo[] EventosNuevos =
    {
        new("Conciertos", "Guitarras del Misti: Noche de Rock", "Una noche de rock peruano con guitarras potentes, bandas invitadas y visuales inspiradas en el volcán. Disfruta clásicos, composiciones nuevas y una producción sonora envolvente.", "Estadio Melgar", "20:00", 3200, 65, PlantillaPlano.Estadio),
        new("Conciertos", "Pop al Atardecer Arequipa", "Voces emergentes y éxitos pop llenan una tarde al aire libre con músicos en vivo, luces cálidas y espacios para bailar. Habrá zona gastronómica y acceso familiar.", "Parque Selva Alegre", "17:30", 2400, 48, PlantillaPlano.CampoAbierto),
        new("Conciertos", "Salsa Brava bajo las Estrellas", "Orquestas de salsa clásica y contemporánea convierten la plaza en una pista abierta. Aprende pasos básicos, disfruta percusión en vivo y comparte una celebración bailable hasta medianoche.", "Plaza de Yanahuara", "19:00", 1800, 55, PlantillaPlano.CampoAbierto),

        new("Cultura", "Arequipa en Piedra: Ruta del Sillar", "Recorre canteras y talleres acompañados por maestros talladores que explican técnicas, herramientas y memoria local. La visita incluye demostraciones prácticas y transporte desde el centro histórico.", "Canteras de Añashuayco", "09:00", 180, 35, PlantillaPlano.CampoAbierto),
        new("Cultura", "Encuentro de Danzas del Sur", "Elencos de Arequipa, Puno y Cusco presentan danzas tradicionales con música en directo y vestuario regional. Una mediación breve contextualiza cada expresión para todo público.", "Teatro Municipal de Arequipa", "19:30", 500, 42, PlantillaPlano.Teatro),
        new("Cultura", "Cine Arequipeño: Historias de Barrio", "Proyección de cortometrajes locales seguida por una conversación cercana con realizadores. La selección explora barrios, oficios y paisajes urbanos con entrada accesible para estudiantes y familias.", "Centro Cultural Peruano Norteamericano", "18:30", 300, 24, PlantillaPlano.Teatro),

        new("Deportes", "Desafío Trail: Cumbres de Arequipa", "Corre por senderos señalizados con circuitos de cinco y doce kilómetros, puestos de hidratación y asistencia médica. La jornada incluye calentamiento guiado y reconocimiento para todas las categorías.", "Cerro Colorado, sector Añashuayco", "07:00", 900, 38, PlantillaPlano.CampoAbierto),
        new("Deportes", "Copa Mistiana de Básquet 3x3", "Equipos mixtos compiten en partidos rápidos de básquet urbano, con música, clínicas para jóvenes y finales bajo iluminación profesional. Inscripción recreativa y graderías disponibles para espectadores.", "Coliseo Arequipa", "10:00", 1500, 28, PlantillaPlano.Coliseo),
        new("Deportes", "Pedalea Arequipa: Ruta del Chili", "Una rodada guiada para distintos niveles recorre ciclovías y miradores junto al río Chili. Incluye revisión básica de bicicletas, puntos de descanso y acompañamiento de seguridad vial.", "Puente Grau, Cercado", "08:00", 650, 22, PlantillaPlano.CampoAbierto),

        new("Exposiciones", "Luz Volcánica: Fotografía del Misti", "Fotógrafos locales presentan paisajes del Misti en distintos climas y horas del día. Conversatorios y estaciones interactivas revelan cómo se construye una imagen documental del territorio.", "Palacio Metropolitano de Bellas Artes", "10:00", 420, 18, PlantillaPlano.Feria),
        new("Exposiciones", "Textiles del Colca: Tramas Vivas", "Una muestra curada reúne tejidos, tintes naturales y relatos de artesanas del Colca. Demostraciones programadas permiten conocer símbolos, procesos comunitarios y el valor del comercio justo.", "Casa Museo Mario Vargas Llosa", "11:00", 300, 20, PlantillaPlano.Feria),
        new("Exposiciones", "Arequipa Futura: Ciencia e Innovación", "Proyectos universitarios de energía, robótica y cuidado del agua se exhiben mediante prototipos que el público puede probar. Charlas breves conectan investigación, emprendimiento y desafíos regionales.", "Centro de Convenciones Cerro Juli", "09:30", 1600, 30, PlantillaPlano.Feria),

        new("Familiar", "Pequeños Científicos del Misti", "Niñas y niños experimentan con volcanes seguros, burbujas y materiales cotidianos junto a mediadores científicos. Cada estación propone aprender jugando y llevar a casa una actividad sencilla.", "Parque Selva Alegre", "10:30", 700, 25, PlantillaPlano.CampoAbierto),
        new("Familiar", "Títeres del Sur: La Llama Viajera", "Una aventura de títeres acompaña a una llama curiosa por pueblos, música y paisajes andinos. La función incorpora canciones participativas y un encuentro breve con las familias.", "Teatro Arequepay", "16:00", 300, 28, PlantillaPlano.Teatro),
        new("Familiar", "Circo de Colores en Familia", "Acrobacias, malabares y humor blanco forman un espectáculo cercano para distintas edades. La carpa ofrece funciones accesibles, zona de descanso y actividades creativas antes de comenzar.", "Campo Ferial Cerro Juli", "15:30", 2100, 32, PlantillaPlano.CampoAbierto),

        new("Ferias", "Hecho en Arequipa: Feria Emprende", "Emprendimientos locales presentan diseño, decoración y productos sostenibles en puestos seleccionados. Conversaciones con creadores, talleres abiertos y música acústica hacen de la visita una experiencia completa.", "Fundo El Fierro", "10:00", 1200, 12, PlantillaPlano.Feria),
        new("Ferias", "Feria del Libro y la Palabra Mistiana", "Librerías independientes, editoriales y autores comparten novedades, lecturas y actividades para lectores jóvenes. La programación incluye presentaciones, trueque de libros y talleres gratuitos de escritura.", "Plaza San Francisco", "10:00", 1800, 10, PlantillaPlano.Feria),
        new("Ferias", "Manos del Colca: Artesanía y Oficios", "Artesanas y maestros de la región exhiben cerámica, fibras y piezas talladas con información sobre su procedencia. Demostraciones en vivo y venta directa impulsan el trabajo local.", "Centro de Convenciones Cerro Juli", "09:00", 950, 15, PlantillaPlano.Feria),

        new("Festivales", "Festival de Luces del Misti", "Instalaciones luminosas y música instrumental transforman un recorrido peatonal en una celebración nocturna. Artistas locales presentan piezas originales mientras el público descubre rincones patrimoniales.", "Plaza de Armas de Arequipa", "18:00", 2800, 36, PlantillaPlano.CampoAbierto),
        new("Festivales", "Indie Sur: Música sin Fronteras", "Bandas independientes del sur comparten dos escenarios con propuestas de rock alternativo, electrónica y pop. El festival suma emprendimientos, zonas de descanso y horarios para disfrutar cada presentación.", "Campo Ferial Cerro Juli", "16:00", 4200, 72, PlantillaPlano.CampoAbierto),
        new("Festivales", "Danza Viva: Festival de Movimiento", "Compañías contemporáneas y colectivos de danza urbana presentan piezas breves en una programación diversa. Conversatorios y talleres participativos invitan a explorar el movimiento sin experiencia previa.", "Palacio Metropolitano de Bellas Artes", "17:00", 800, 30, PlantillaPlano.Teatro),

        new("Gastronomía", "Picanterías de Arequipa: Mesa Compartida", "Picanteras invitadas preparan recetas emblemáticas y cuentan el origen de sus ingredientes. La experiencia incluye degustaciones por estaciones, maridajes sin alcohol y recomendaciones para visitar productores locales.", "Fundo El Fierro", "12:00", 850, 58, PlantillaPlano.Feria),
        new("Gastronomía", "Chocolate Andino: Cacao y Café", "Productores de cacao y café del sur guían una degustación comparativa desde el grano hasta la taza. Aprende sobre tostado, aromas y comercio responsable en talleres de aforo reducido.", "Casa Andina Select Arequipa", "16:30", 320, 45, PlantillaPlano.Feria),
        new("Gastronomía", "Sabores de Loncco: Cocina de Temporada", "Cocineras locales reinterpretan productos de temporada en pequeños platos para compartir. Cada preparación destaca ingredientes regionales y ofrece una breve explicación sobre su vínculo con la tradición.", "Patio del Ekeko, Cercado", "13:00", 480, 62, PlantillaPlano.Feria),

        new("Teatro", "La Casa de los Vientos", "Una obra original sigue a tres generaciones que regresan a una casona familiar frente al Misti. Humor y memoria se encuentran en una puesta íntima con elenco arequipeño.", "Teatro Municipal de Arequipa", "20:00", 600, 50, PlantillaPlano.Teatro),
        new("Teatro", "Impro Mistiana: Historias al Instante", "Actores de improvisación crean escenas a partir de ideas del público, con música en vivo y humor cotidiano. Cada función es irrepetible y ofrece una noche ágil para compartir.", "Teatro Arequepay", "19:00", 400, 38, PlantillaPlano.Teatro),
        new("Teatro", "El Viaje de una Estrella", "Una puesta musical familiar combina canciones originales, movimiento y proyecciones para contar la aventura de una estrella que busca su lugar en el cielo andino.", "Auditorio del Colegio de Abogados", "17:30", 500, 44, PlantillaPlano.Teatro),

        new("Otros", "Noche de Observación: Cielos del Sur", "Especialistas en astronomía orientan la observación de la Luna y planetas con telescopios compartidos. Una charla introductoria y mapas celestes facilitan la experiencia para quienes empiezan.", "Observatorio de Characato", "19:30", 220, 32, PlantillaPlano.CampoAbierto),
        new("Otros", "Adopta con Amor: Encuentro de Mascotas", "Refugios y veterinarios organizan una jornada responsable de adopción con orientación sobre cuidados, identificación y convivencia. Habrá actividades para familias y espacios tranquilos para los animales.", "Parque Libertad de Expresión", "09:30", 500, 8, PlantillaPlano.CampoAbierto),
        new("Otros", "Conecta Sur: Encuentro de Ideas", "Profesionales y emprendedores intercambian experiencias en conversaciones breves, mentorías y mesas temáticas. La agenda favorece contactos útiles y proyectos colaborativos entre iniciativas de Arequipa.", "Centro de Convenciones Cerro Juli", "09:00", 700, 40, PlantillaPlano.Feria)
    };

    private sealed record EventoHistorico(string Categoria, string Titulo, string Ubicacion, int Aforo, decimal General, decimal Vip);

    public static async Task SeedEventosAsync(
        ApplicationDbContext db, string? organizadorId, ILogger logger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(organizadorId))
            throw new InvalidOperationException("No se encontró una cuenta para asignar la organización de los eventos de ejemplo.");

        var categorias = await db.Categorias.AsNoTracking().ToDictionaryAsync(c => c.Nombre, ct);
        var existentes = (await db.Eventos.AsNoTracking().Select(e => e.Titulo).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var nuevos = EventosNuevos.Where(e => !existentes.Contains(e.Titulo)).ToList();
        if (nuevos.Count == 0)
            return;

        var hoy = DateTime.Today;
        var primeraFecha = hoy.AddDays(14);
        var fechaMinima = new DateTime(2026, 11, 1);
        if (primeraFecha < fechaMinima)
            primeraFecha = fechaMinima.AddDays(14);

        if (primeraFecha.AddDays((EventosNuevos.Length - 1) * 13) > FinPeriodoEventosNuevos)
            throw new InvalidOperationException("No es posible ubicar los eventos de ejemplo dentro del período futuro solicitado.");

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var creados = new List<Evento>();
        foreach (var (dato, indice) in EventosNuevos.Select((evento, indice) => (evento, indice)))
        {
            if (existentes.Contains(dato.Titulo))
                continue;

            var categoria = categorias[dato.Categoria];
            var fecha = primeraFecha.AddDays(indice * 13).Date + TimeSpan.Parse(dato.Hora, CultureInfo.InvariantCulture);
            var aforoVip = dato.Aforo / 5;
            var aforoGeneral = dato.Aforo - aforoVip;
            var numerado = dato.Plano == PlantillaPlano.Teatro;
            TipoEntrada CrearZona(string nombre, decimal precio, int aforo, string color, string puerta) => new()
            {
                Nombre = nombre,
                Precio = precio,
                Capacidad = aforo,
                Color = color,
                PuertaIngreso = puerta,
                AsientosNumerados = numerado,
                Filas = numerado ? aforo / 20 : null,
                AsientosPorFila = numerado ? 20 : null
            };

            var evento = new Evento
            {
                Titulo = dato.Titulo,
                Descripcion = dato.Descripcion,
                CategoriaId = categoria.Id,
                Ubicacion = dato.Ubicacion,
                Fecha = fecha,
                Aforo = dato.Aforo,
                PlantillaPlano = dato.Plano,
                Estado = EstadoEvento.Aprobado,
                OrganizadorId = organizadorId,
                FechaCreacion = DateTime.UtcNow,
                FechaModeracion = DateTime.UtcNow,
                EsGratuito = false,
                PendientePagoPublicacion = false,
                TiposEntrada =
                {
                    CrearZona("General", dato.Precio, aforoGeneral, "#a855f7", "Puerta General"),
                    CrearZona("VIP", Math.Round(dato.Precio * 2.25m, 2), aforoVip, "#f59e0b", "Puerta VIP")
                },
                Imagenes =
                {
                    new EventoImagen { Ruta = categoria.Imagen ?? "/img/categorias/otros.jpg", Orden = 0 }
                }
            };
            db.Eventos.Add(evento);
            creados.Add(evento);
            existentes.Add(dato.Titulo);
        }

        await db.SaveChangesAsync(ct);
        foreach (var evento in creados.Where(e => e.PlantillaPlano == PlantillaPlano.Teatro))
        {
            var zonas = evento.TiposEntrada.OrderBy(z => z.Id).ToList();
            db.Asientos.AddRange(PlanoService.GenerarAsientos(zonas));
        }
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);

        logger.LogInformation("Se agregaron {Cantidad} eventos de ejemplo, con entradas e imágenes locales.", creados.Count);
    }

    public static async Task AsignarEventosDemoAsync(
        ApplicationDbContext db, string organizadorId, CancellationToken ct = default)
    {
        var titulos = EventosNuevos.Select(e => e.Titulo).ToArray();
        var eventos = await db.Eventos
            .Where(e => titulos.Contains(e.Titulo) || e.Titulo.StartsWith(PrefijoEventoHistorico))
            .ToListAsync(ct);
        foreach (var evento in eventos)
            evento.OrganizadorId = organizadorId;
        await db.SaveChangesAsync(ct);
    }

    public static async Task SeedVentasAsync(
        ApplicationDbContext db, string organizadorId, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Ordenes.AsNoTracking().AnyAsync(o => o.ProveedorPago == ProveedorDemostracion, ct))
            return;

        var hoy = DateTime.Today;
        if (hoy < InicioPeriodo)
            throw new InvalidOperationException("La fecha actual es anterior al inicio del período de demostración (julio de 2026).");

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var historicos = await ObtenerEventosHistoricosAsync(db, organizadorId, hoy, ct);
        var usuarios = await ObtenerClientesDemoAsync(db, hoy, ct);
        var porcentajeComision = config.GetValue<decimal>("Comisiones:Porcentaje", 8m);
        var codigoOrden = 0;
        var totalCompras = 0;

        for (var dia = InicioPeriodo; dia <= hoy; dia = dia.AddDays(1))
        {
            var cantidadDiaria = 1 + (dia.Day + (int)dia.DayOfWeek) % 3;
            for (var indiceDia = 0; indiceDia < cantidadDiaria; indiceDia++)
            {
                var evento = historicos.First(e => e.Fecha.Date >= dia.Date);
                var vip = (dia.Day + indiceDia * 3) % 5 == 0;
                var zona = evento.TiposEntrada.Single(t => t.Nombre == (vip ? "VIP" : "General"));
                var cantidad = 1 + (dia.DayOfYear + indiceDia * 2) % 3;
                zona.Vendidas += cantidad;

                var usuario = usuarios[(dia.DayOfYear * 11 + indiceDia * 7 + dia.Month * 3) % usuarios.Count];
                var fechaPago = dia.Date == hoy
                    ? DateTime.Now.AddMinutes(-(indiceDia + 1) * 7)
                    : dia.AddHours(9 + ((dia.Day + indiceDia * 3) % 8))
                        .AddMinutes((dia.Day * 7 + indiceDia * 13) % 60);
                var fechaPagoUtc = DateTime.SpecifyKind(fechaPago, DateTimeKind.Local).ToUniversalTime();
                var total = zona.Precio * cantidad;
                var orden = new Orden
                {
                    Codigo = $"DEMO-{++codigoOrden:D8}",
                    UsuarioId = usuario.Id,
                    EventoId = evento.Id,
                    Estado = EstadoOrden.Pagada,
                    Total = total,
                    Descuento = 0,
                    PorcentajeComision = porcentajeComision,
                    Comision = Math.Round(total * porcentajeComision / 100m, 2),
                    FechaCreacion = fechaPagoUtc,
                    FechaExpiracion = fechaPagoUtc,
                    FechaPago = fechaPagoUtc,
                    ProveedorPago = ProveedorDemostracion
                };
                orden.Items.Add(new OrdenItem
                {
                    TipoEntradaId = zona.Id,
                    Cantidad = cantidad,
                    PrecioUnitario = zona.Precio
                });

                for (var entrada = 0; entrada < cantidad; entrada++)
                {
                    var usada = evento.Fecha.Date < hoy && (codigoOrden + entrada) % 2 == 0;
                    orden.Entradas.Add(new Entrada
                    {
                        Codigo = Guid.NewGuid().ToString("N"),
                        Orden = orden,
                        EventoId = evento.Id,
                        TipoEntradaId = zona.Id,
                        UsuarioId = usuario.Id,
                        Estado = usada ? EstadoEntrada.Usada : EstadoEntrada.Activa,
                        FechaEmision = fechaPagoUtc,
                        FechaUso = usada ? DateTime.SpecifyKind(evento.Fecha.AddHours(2), DateTimeKind.Local).ToUniversalTime() : null
                    });
                }

                db.Ordenes.Add(orden);
                totalCompras++;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        logger.LogInformation("Se generaron {Compras} compras de demostración entre julio de 2026 y {Fecha}.", totalCompras, hoy);
    }

    private static async Task<List<Evento>> ObtenerEventosHistoricosAsync(
        ApplicationDbContext db, string organizadorId, DateTime hoy, CancellationToken ct)
    {
        var existentes = await db.Eventos
            .Include(e => e.TiposEntrada)
            .Where(e => e.Titulo.StartsWith(PrefijoEventoHistorico))
            .ToListAsync(ct);
        if (existentes.Count > 0)
            return existentes.OrderBy(e => e.Fecha).ToList();

        var categorias = await db.Categorias.AsNoTracking().ToDictionaryAsync(c => c.Nombre, ct);
        var muestras = new[]
        {
            ("Conciertos", "Noche de guitarra en el Misti", "Estadio Melgar", 1800, 55m, 125m),
            ("Cultura", "Memoria viva de Arequipa", "Teatro Municipal de Arequipa", 620, 35m, 78m),
            ("Deportes", "Carrera urbana Ciudad Blanca", "Parque Selva Alegre", 1400, 28m, 65m),
            ("Gastronomía", "Sabores de picantería", "Fundo El Fierro", 900, 48m, 105m),
            ("Festivales", "Encuentro musical del Sur", "Campo Ferial Cerro Juli", 2400, 42m, 98m)
        };
        var eventos = new List<Evento>();
        var primerMes = new DateTime(InicioPeriodo.Year, InicioPeriodo.Month, 1);
        var ultimoMes = new DateTime(hoy.Year, hoy.Month, 1);
        for (var mes = primerMes; mes <= ultimoMes; mes = mes.AddMonths(1))
        {
            var (categoriaNombre, nombre, ubicacion, aforo, precioGeneral, precioVip) =
                muestras[((mes.Year - InicioPeriodo.Year) * 12 + mes.Month - InicioPeriodo.Month) % muestras.Length];
            var finMes = mes.AddMonths(1).AddDays(-1);
            var diaEvento = mes.Year == hoy.Year && mes.Month == hoy.Month && finMes < hoy
                ? hoy.AddDays(1)
                : finMes;
            var categoria = categorias[categoriaNombre];
            var evento = new Evento
            {
                Titulo = $"{PrefijoEventoHistorico}{nombre} {mes:yyyy-MM}",
                Descripcion = "Evento histórico de demostración para relacionar ventas, entradas y asistencia sin usar las actividades futuras.",
                CategoriaId = categoria.Id,
                Ubicacion = ubicacion,
                Fecha = diaEvento.Date.AddHours(19),
                Aforo = aforo,
                Estado = EstadoEvento.Aprobado,
                OrganizadorId = organizadorId,
                FechaCreacion = DateTime.SpecifyKind(mes, DateTimeKind.Local).ToUniversalTime(),
                FechaModeracion = DateTime.SpecifyKind(mes, DateTimeKind.Local).ToUniversalTime(),
                TiposEntrada =
                {
                    new TipoEntrada { Nombre = "General", Precio = precioGeneral, Capacidad = aforo * 4 / 5, Color = "#a855f7", PuertaIngreso = "Puerta General" },
                    new TipoEntrada { Nombre = "VIP", Precio = precioVip, Capacidad = aforo - aforo * 4 / 5, Color = "#f59e0b", PuertaIngreso = "Puerta VIP" }
                }
            };
            db.Eventos.Add(evento);
            eventos.Add(evento);
        }
        await db.SaveChangesAsync(ct);
        return eventos;
    }

    private static async Task<List<ApplicationUser>> ObtenerClientesDemoAsync(
        ApplicationDbContext db, DateTime hoy, CancellationToken ct)
    {
        var diasDisponibles = Math.Max(1, (hoy - InicioPeriodo).Days + 1);
        var clientes = new List<ApplicationUser>();
        for (var i = 1; i <= 36; i++)
        {
            var email = $"cliente{i:D2}@demo.activafest.test";
            var cliente = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
            if (cliente == null)
            {
                cliente = new ApplicationUser
                {
                    Id = $"demo-cliente-{i:D2}",
                    UserName = email,
                    NormalizedUserName = email.ToUpperInvariant(),
                    Email = email,
                    NormalizedEmail = email.ToUpperInvariant(),
                    EmailConfirmed = true,
                    NombreCompleto = $"Cliente de demostración {i:D2}",
                    FechaRegistro = DateTime.SpecifyKind(InicioPeriodo.AddDays((i * 13) % diasDisponibles), DateTimeKind.Local).ToUniversalTime(),
                    Activo = true,
                    SecurityStamp = Guid.NewGuid().ToString("N")
                };
                db.Users.Add(cliente);
            }
            clientes.Add(cliente);
        }
        await db.SaveChangesAsync(ct);
        return clientes;
    }
}
