using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Recomendaciones;

namespace ActivaFest.Services.Asistente;

// =============================
// 🧩 FUNCIONES DEL AGENTE DE IA (solo lectura, por rol)
// =============================
// La IA no consulta la BD por su cuenta: solo puede llamar a estas funciones. El usuario va fijado al crear cada
// plugin con la sesión del servidor, así que nunca puede pedir datos de otra persona ni de otro rol.
// Cada función agrega a "Enlaces" las páginas de la plataforma relacionadas con su respuesta.
public abstract class FuncionesBase
{
    public List<EnlaceChat> Enlaces { get; } = new();
    public int? EventoId { get; protected set; }

    // Tarjeta de indicadores que el chat dibuja junto a la respuesta (dashboard de ventas)
    public DashboardChat? Dashboard { get; protected set; }

    protected void Enlazar(string texto, string url)
    {
        if (Enlaces.All(e => e.Url != url))
            Enlaces.Add(new EnlaceChat(texto, url));
    }

    protected static string Fecha(DateTime f) => f.ToString("dddd dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("es-PE"));

    // Evento aprobado por su nombre (coincidencia aproximada sin tildes) o por su id
    protected static async Task<Evento?> BuscarEventoAsync(ApplicationDbContext db, string? nombre, int? id, IQueryable<Evento>? fuente, CancellationToken ct)
    {
        var eventos = fuente ?? db.Eventos.Where(e => e.Estado == EstadoEvento.Aprobado);
        if (id is > 0)
            return await eventos.AsNoTracking().Include(e => e.TiposEntrada).FirstOrDefaultAsync(e => e.Id == id, ct);

        // El modelo a veces manda "el evento de cumbia peruana" o solo "evento": el reconocedor separa el nombre real
        var buscado = BaseConocimiento.Normalizar(nombre ?? "");
        if (buscado.Length == 0)
            return null;

        var candidatos = await eventos.AsNoTracking().Select(e => new EventoCandidato(e.Id, e.Titulo, e.Fecha)).ToListAsync(ct);
        var reconocido = ReconocedorEventos.Reconocer(buscado, candidatos, comoNombre: true);
        var mejor = reconocido.Encontrado ?? reconocido.Opciones.FirstOrDefault();

        return mejor == null ? null
            : await eventos.AsNoTracking().Include(e => e.TiposEntrada).FirstOrDefaultAsync(e => e.Id == mejor.Id, ct);
    }
}

// ---------- Cualquier visitante (con o sin sesión) ----------
public class FuncionesPublicas : FuncionesBase
{
    public const string NombrePlugin = "Plataforma";

    private readonly ApplicationDbContext _db;
    private readonly ICatalogoService _catalogo;
    private readonly IRecomendacionService _recomendaciones;
    private readonly IAsistenteService _asistente;
    private readonly string? _usuarioId;
    private readonly Ubicacion? _ubicacion;

    public FuncionesPublicas(ApplicationDbContext db, ICatalogoService catalogo, IRecomendacionService recomendaciones,
        IAsistenteService asistente, string? usuarioId, Ubicacion? ubicacion)
    {
        _db = db;
        _catalogo = catalogo;
        _recomendaciones = recomendaciones;
        _asistente = asistente;
        _usuarioId = usuarioId;
        _ubicacion = ubicacion;
    }

    [KernelFunction("BuscarEventos")]
    [Description("Busca eventos próximos publicados en ActivaFest. Entiende categorías (concierto, teatro, festival, feria, exposición, deporte, gastronomía, familiar, cultura), fechas (hoy, mañana, fin de semana, esta semana, este mes), 'gratis', 'barato' y una ciudad.")]
    public async Task<object> BuscarEventosAsync(
        [Description("Lo que busca el usuario en lenguaje natural, por ejemplo 'conciertos este fin de semana en Lima'")] string consulta,
        [Description("Precio máximo en soles (opcional)")] decimal? precioMaximo = null,
        CancellationToken ct = default)
    {
        var filtro = AsistenteService.InterpretarBusqueda(BaseConocimiento.Normalizar(consulta ?? ""), DateTime.Today);
        if (precioMaximo is > 0)
            filtro.PrecioMax = precioMaximo;

        var resultado = await _catalogo.BuscarAsync(filtro, ct);
        if (resultado.Total == 0 && filtro.Buscar != null)
        {
            filtro.Buscar = null;
            resultado = await _catalogo.BuscarAsync(filtro, ct);
        }

        var top = resultado.Eventos.Take(5).ToList();
        top.ForEach(e => Enlazar(e.Titulo, $"/Eventos/Detalle/{e.Id}"));
        Enlazar("Ver catálogo", "/Eventos");
        if (top.Count == 1)
            EventoId = top[0].Id;

        return new
        {
            total = resultado.Total,
            eventos = top.Select(e => new { e.Id, e.Titulo, e.Categoria, e.Ubicacion, fecha = Fecha(e.Fecha), precioDesde = Formato.PrecioDesde(e.PrecioDesde) })
        };
    }

    [KernelFunction("InformacionDeEvento")]
    [Description("Datos de un evento concreto: fecha, hora, lugar, descripción, zonas de entrada con precio y disponibilidad.")]
    public async Task<object> InformacionDeEventoAsync(
        [Description("Nombre (o parte del nombre) del evento")] string? nombre = null,
        [Description("Id del evento, si se conoce")] int? eventoId = null,
        CancellationToken ct = default)
    {
        var evento = await BuscarEventoAsync(_db, nombre, eventoId, null, ct);
        if (evento == null)
            return new { encontrado = false, mensaje = "No hay un evento publicado con ese nombre." };

        EventoId = evento.Id;
        Enlazar($"Ver {evento.Titulo}", $"/Eventos/Detalle/{evento.Id}");
        var descripcion = evento.Descripcion.Length > 300 ? evento.Descripcion[..300] + "…" : evento.Descripcion;

        return new
        {
            encontrado = true,
            evento.Id,
            evento.Titulo,
            evento.Ubicacion,
            fecha = Fecha(evento.Fecha),
            yaOcurrio = evento.YaOcurrio(DateTime.Now),
            ingresoDesde = Fecha(evento.InicioIngreso),
            gratuito = evento.EsGratuito,
            descripcion,
            zonas = evento.TiposEntrada.OrderBy(t => t.Precio)
                .Select(t => new { t.Nombre, precio = Formato.Precio(t.Precio), t.Disponibles, agotada = t.Disponibles == 0, t.PuertaIngreso })
        };
    }

    [KernelFunction("RecomendarEntradas")]
    [Description("Para un evento, ordena las zonas de entrada según el presupuesto y la cantidad de personas, indicando cuáles alcanzan y tienen disponibilidad. Úsala cuando el usuario pida consejo sobre qué entrada comprar.")]
    public async Task<object> RecomendarEntradasAsync(
        [Description("Nombre del evento")] string? nombre = null,
        [Description("Id del evento, si se conoce")] int? eventoId = null,
        [Description("Presupuesto máximo por persona en soles (opcional)")] decimal? presupuestoPorPersona = null,
        [Description("Cuántas entradas necesita (por defecto 1)")] int cantidad = 1,
        CancellationToken ct = default)
    {
        var evento = await BuscarEventoAsync(_db, nombre, eventoId, null, ct);
        if (evento == null)
            return new { encontrado = false, mensaje = "No hay un evento publicado con ese nombre." };

        EventoId = evento.Id;
        Enlazar("Comprar entradas", $"/Eventos/Detalle/{evento.Id}#comprar");
        cantidad = Math.Clamp(cantidad, 1, 10);

        var zonas = evento.TiposEntrada.OrderBy(t => t.Precio).Select(t => new
        {
            t.Nombre,
            precio = Formato.Precio(t.Precio),
            totalParaElGrupo = Formato.Precio(t.Precio * cantidad),
            dentroDelPresupuesto = presupuestoPorPersona == null || t.Precio <= presupuestoPorPersona,
            hayDisponibilidad = t.Disponibles >= cantidad,
            t.Disponibles,
            asientosNumerados = t.AsientosNumerados
        }).ToList();

        return new
        {
            encontrado = true,
            evento.Titulo,
            fecha = Fecha(evento.Fecha),
            cantidad,
            presupuestoPorPersona = presupuestoPorPersona.HasValue ? Formato.Precio(presupuestoPorPersona.Value) : null,
            zonas,
            opcionesQueCumplen = zonas.Where(z => z.dentroDelPresupuesto && z.hayDisponibilidad).Select(z => z.Nombre)
        };
    }

    [KernelFunction("RecomendarEventos")]
    [Description("Recomienda eventos próximos según los gustos e historial del usuario (o los más populares si no inició sesión).")]
    public async Task<object> RecomendarEventosAsync(CancellationToken ct = default)
    {
        var recomendados = await _recomendaciones.RecomendarAsync(_usuarioId, _ubicacion, 4, ct);
        recomendados.ForEach(e => Enlazar(e.Titulo, $"/Eventos/Detalle/{e.Id}"));
        return new
        {
            personalizadas = _usuarioId != null,
            eventos = recomendados.Select(e => new { e.Id, e.Titulo, e.Categoria, fecha = Fecha(e.Fecha), precioDesde = Formato.PrecioDesde(e.PrecioDesde), motivo = e.Motivo })
        };
    }

    [KernelFunction("AyudaDeLaPlataforma")]
    [Description("Respuesta oficial de ActivaFest a dudas generales: cómo comprar, medios de pago, reembolsos, transferir entradas, ingreso con QR, problemas de compra, publicar o destacar eventos, comisiones, cuenta, edad mínima y contacto.")]
    public object AyudaDeLaPlataforma([Description("La duda del usuario")] string pregunta)
    {
        var intencion = _asistente.DetectarIntencion(pregunta ?? "");
        var entrada = BaseConocimiento.Buscar(intencion);
        if (entrada?.Respuesta == null)
            return new { encontrada = false, contacto = "soporte@activafest.com" };

        foreach (var e in entrada.Enlaces ?? Array.Empty<string>())
        {
            var p = e.Split('|');
            Enlazar(p[0], p[1]);
        }
        return new { encontrada = true, tema = intencion, respuesta = entrada.Respuesta };
    }
}

// ---------- Usuario con sesión iniciada ----------
public class FuncionesUsuario : FuncionesBase
{
    public const string NombrePlugin = "MiCuenta";

    private readonly ApplicationDbContext _db;
    private readonly string _usuarioId;
    private readonly IAccionesAsistente _acciones;

    public FuncionesUsuario(ApplicationDbContext db, string usuarioId, IAccionesAsistente acciones)
    {
        _db = db;
        _usuarioId = usuarioId;
        _acciones = acciones;
    }

    // La IA nunca reserva por su cuenta: prepara el pedido y el asistente le pide la confirmación al usuario
    public BorradorCompra? Borrador { get; private set; }

    [KernelFunction("PrepararReserva")]
    [Description("Empieza una reserva o compra de entradas cuando el usuario la pide. NO reserva nada: el asistente le pregunta lo que falte y le pide confirmación. Llama a esta función aunque no sepas el evento o la zona.")]
    public async Task<object> PrepararReservaAsync(
        [Description("Nombre del evento, si el usuario lo dijo")] string? nombre = null,
        [Description("Id del evento, si se conoce")] int? eventoId = null,
        [Description("Zona de entrada (por ejemplo General o VIP), si el usuario la indicó")] string? zona = null,
        [Description("Cuántas entradas, si el usuario lo dijo")] int? cantidad = null,
        [Description("true si quiere comprar y pagar ahora; false si solo quiere reservar")] bool comprar = false,
        CancellationToken ct = default)
    {
        var evento = await BuscarEventoAsync(_db, nombre, eventoId, null, ct);
        Borrador = new BorradorCompra
        {
            Modo = comprar ? BorradorCompra.Comprar : BorradorCompra.Reservar,
            EventoId = evento?.Id,
            Zona = zona,
            Cantidad = cantidad,
            Nuevo = true
        };
        return new { preparada = true };
    }

    [KernelFunction("MiCarrito")]
    [Description("Las entradas que el usuario tiene reservadas y faltan pagar (su carrito), con el total y cuándo vence cada reserva.")]
    public async Task<object> MiCarritoAsync(CancellationToken ct = default)
    {
        var carrito = await _acciones.CarritoAsync(_usuarioId, ct);
        Enlazar("🛒 Ir a mi carrito", "/Carrito");
        return new
        {
            total = Formato.Monto(carrito.Total),
            entradas = carrito.Entradas,
            reservas = carrito.PorPagar.Select(i => new
            {
                evento = i.Evento,
                zonas = i.Lineas.Select(l => $"{l.Cantidad} {l.Zona}"),
                total = Formato.Monto(i.Total),
                minutosParaVencer = Math.Max(1, (int)Math.Ceiling((i.Vence - DateTime.UtcNow).TotalMinutes))
            })
        };
    }

    [KernelFunction("ActivarAvisoDeEvento")]
    [Description("Programa un aviso (recordatorio por correo 24 horas antes) para un evento que aún no ocurre. Úsala cuando el usuario pida que le recuerden, avisen o notifiquen de un evento.")]
    public async Task<object> ActivarAvisoDeEventoAsync(
        [Description("Nombre del evento")] string? nombre = null,
        [Description("Id del evento, si se conoce")] int? eventoId = null,
        CancellationToken ct = default)
    {
        var evento = await BuscarEventoAsync(_db, nombre, eventoId, null, ct);
        if (evento == null)
            return new { activo = false, mensaje = "No hay un evento publicado con ese nombre." };

        EventoId = evento.Id;
        Enlazar($"Ver {evento.Titulo}", $"/Eventos/Detalle/{evento.Id}");
        var r = await _acciones.ActivarRecordatorioAsync(_usuarioId, evento.Id, ct);
        return new { activo = r.Activo, mensaje = r.Mensaje };
    }

    [KernelFunction("QuitarAvisoDeEvento")]
    [Description("Quita el aviso (recordatorio) que el usuario tenía para un evento.")]
    public async Task<object> QuitarAvisoDeEventoAsync(
        [Description("Nombre del evento")] string? nombre = null,
        [Description("Id del evento, si se conoce")] int? eventoId = null,
        CancellationToken ct = default)
    {
        var evento = await BuscarEventoAsync(_db, nombre, eventoId, null, ct);
        if (evento == null)
            return new { quitado = false, mensaje = "No hay un evento publicado con ese nombre." };

        var r = await _acciones.QuitarRecordatorioAsync(_usuarioId, evento.Id, ct);
        return new { quitado = !r.Activo && r.EventoId != null, mensaje = r.Mensaje };
    }

    [KernelFunction("RecordarMisTurnosDeControl")]
    [Description("Solo para personal de control de acceso: programa un recordatorio (en sus notificaciones y por correo) de cada evento que tiene asignado para escanear entradas.")]
    public async Task<object> RecordarMisTurnosDeControlAsync(CancellationToken ct = default)
    {
        var r = await _acciones.RecordarMisTurnosAsync(_usuarioId, ct);
        Enlazar("🔔 Mis notificaciones", "/Notificaciones");
        return new { programados = r.Activo, mensaje = r.Mensaje };
    }

    [KernelFunction("MisAvisosYProximosEventos")]
    [Description("Notificaciones del usuario: sus próximos eventos (con avisos activos o con entradas compradas) y cuánto falta para cada uno.")]
    public async Task<object> MisAvisosYProximosEventosAsync(CancellationToken ct = default)
    {
        var avisos = await _acciones.AvisosAsync(_usuarioId, ct);
        var ahora = DateTime.Now;
        avisos.Take(3).ToList().ForEach(a => Enlazar(a.Evento, $"/Eventos/Detalle/{a.EventoId}"));
        return avisos.Take(10).Select(a => new
        {
            evento = a.Evento,
            fecha = Fecha(a.Fecha),
            lugar = a.Ubicacion,
            falta = a.Falta(ahora),
            tieneEntradas = a.TieneEntradas,
            avisoActivo = a.Recordatorio
        }).ToList();
    }

    [KernelFunction("ResumenDeMisCompras")]
    [Description("Resumen de las compras del usuario autenticado: cuántas compras pagadas, cuántos eventos distintos, cuántas entradas (activas, usadas, anuladas), cuánto gastó y sus próximos eventos.")]
    public async Task<object> ResumenDeMisComprasAsync(CancellationToken ct = default)
    {
        var ordenes = await _db.Ordenes.AsNoTracking()
            .Where(o => o.UsuarioId == _usuarioId && o.Estado == EstadoOrden.Pagada)
            .Select(o => new { o.EventoId, o.Total, o.FechaPago, Evento = o.Evento!.Titulo, FechaEvento = o.Evento.Fecha })
            .ToListAsync(ct);

        var entradas = await _db.Entradas.AsNoTracking()
            .Where(e => e.UsuarioId == _usuarioId)
            .GroupBy(e => e.Estado)
            .Select(g => new { Estado = g.Key, Total = g.Count() })
            .ToListAsync(ct);

        int Contar(EstadoEntrada estado) => entradas.FirstOrDefault(e => e.Estado == estado)?.Total ?? 0;

        Enlazar("Mis entradas", "/Entradas");
        var ahora = DateTime.Now;

        return new
        {
            comprasPagadas = ordenes.Count,
            eventosDistintos = ordenes.Select(o => o.EventoId).Distinct().Count(),
            totalGastado = Formato.Monto(ordenes.Sum(o => o.Total)),
            entradasActivas = Contar(EstadoEntrada.Activa),
            entradasUsadas = Contar(EstadoEntrada.Usada),
            entradasAnuladas = Contar(EstadoEntrada.Anulada),
            proximosEventos = ordenes.Where(o => o.FechaEvento >= ahora).GroupBy(o => o.EventoId)
                .OrderBy(g => g.First().FechaEvento).Take(5)
                .Select(g => new { evento = g.First().Evento, fecha = Fecha(g.First().FechaEvento) }),
            ultimaCompra = ordenes.Where(o => o.FechaPago != null).OrderByDescending(o => o.FechaPago)
                .Select(o => new { o.Evento, fecha = o.FechaPago!.Value.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) })
                .FirstOrDefault()
        };
    }

    [KernelFunction("MisEntradas")]
    [Description("Lista las entradas del usuario autenticado agrupadas por evento, con zona, asiento y estado.")]
    public async Task<object> MisEntradasAsync(
        [Description("true para ver solo las de eventos que aún no ocurren")] bool soloProximas = true,
        CancellationToken ct = default)
    {
        var desde = DateTime.Now.AddHours(-Evento.HorasValidezEntradas);
        var entradas = await _db.Entradas.AsNoTracking()
            .Where(e => e.UsuarioId == _usuarioId && (!soloProximas || e.Evento!.Fecha >= desde))
            .Select(e => new
            {
                e.EventoId,
                Evento = e.Evento!.Titulo,
                Fecha = e.Evento.Fecha,
                Lugar = e.Evento.Ubicacion,
                Zona = e.TipoEntrada!.Nombre,
                Fila = e.Asiento != null ? e.Asiento.Fila : null,
                Numero = e.Asiento != null ? (int?)e.Asiento.Numero : null,
                e.Estado
            })
            .ToListAsync(ct);

        Enlazar("Mis entradas", "/Entradas");
        return entradas.GroupBy(e => e.EventoId).OrderBy(g => g.First().Fecha).Take(10).Select(g => new
        {
            evento = g.First().Evento,
            fecha = Fecha(g.First().Fecha),
            lugar = g.First().Lugar,
            entradas = g.Select(e => new { zona = e.Zona, asiento = e.Numero != null ? $"fila {e.Fila}, asiento {e.Numero}" : null, estado = e.Estado.ToString() })
        }).ToList();
    }
}

// ---------- Organizador (y administrador) ----------
public class FuncionesOrganizador : FuncionesBase
{
    public const string NombrePlugin = "MisEventosOrganizados";

    private readonly ApplicationDbContext _db;
    private readonly string _usuarioId;
    private readonly IAccionesAsistente _acciones;

    public FuncionesOrganizador(ApplicationDbContext db, string usuarioId, IAccionesAsistente acciones)
    {
        _db = db;
        _usuarioId = usuarioId;
        _acciones = acciones;
    }

    [KernelFunction("EstadoDeMisPublicaciones")]
    [Description("Qué eventos del organizador están publicados, cuáles siguen pendientes de aprobación del administrador (y hace cuánto se enviaron), cuáles falta pagar, cuáles fueron rechazados (con motivo), finalizados y cancelados.")]
    public async Task<object> EstadoDeMisPublicacionesAsync(CancellationToken ct = default)
    {
        var estado = await _acciones.EstadoPublicacionesAsync(_usuarioId, ct);
        Enlazar("Mis eventos", "/Eventos/MisEventos");

        static object Lista(List<EventoPublicacion> eventos) =>
            eventos.Take(10).Select(e => new { e.Id, e.Titulo, fecha = Fecha(e.Fecha), detalle = e.Detalle });

        return new
        {
            total = estado.Total,
            publicados = Lista(estado.Publicados),
            pendientesDeAprobacion = Lista(estado.Pendientes),
            faltaPagarPublicacion = Lista(estado.PorPagar),
            rechazados = Lista(estado.Rechazados),
            finalizados = estado.Finalizados.Count,
            cancelados = estado.Cancelados.Count
        };
    }

    [KernelFunction("AvisarmeCuandoApruebenMiEvento")]
    [Description("Programa una notificación para el organizador apenas el administrador apruebe o rechace uno de sus eventos pendientes.")]
    public async Task<object> AvisarmeCuandoApruebenMiEventoAsync(
        [Description("Nombre del evento pendiente")] string? nombre = null,
        [Description("Id del evento, si se conoce")] int? eventoId = null,
        CancellationToken ct = default)
    {
        var evento = await BuscarEventoAsync(_db, nombre, eventoId, _db.Eventos.Where(e => e.OrganizadorId == _usuarioId), ct);
        if (evento == null)
            return new { activo = false, mensaje = "No encontré un evento tuyo con ese nombre." };

        EventoId = evento.Id;
        Enlazar("🔔 Mis notificaciones", "/Notificaciones");
        var r = await _acciones.AvisarAprobacionAsync(_usuarioId, evento.Id, ct);
        return new { activo = r.Activo, mensaje = r.Mensaje };
    }

    [KernelFunction("DashboardDeVentas")]
    [Description("Dashboard de ventas del organizador: ingresos, entradas vendidas, neto tras comisión, ocupación, ticket promedio, ventas de los últimos 7 días y ranking de eventos. Si se indica un evento, da su detalle por zona, saldo por cobrar, órdenes y asistentes que ingresaron. El chat muestra además los gráficos.")]
    public async Task<object> DashboardDeVentasAsync(
        [Description("Nombre de uno de sus eventos (opcional; vacío = todos sus eventos)")] string? nombre = null,
        [Description("Id del evento, si se conoce")] int? eventoId = null,
        CancellationToken ct = default)
    {
        int? id = null;
        if (eventoId is > 0 || !string.IsNullOrWhiteSpace(nombre))
        {
            var evento = await BuscarEventoAsync(_db, nombre, eventoId, _db.Eventos.Where(e => e.OrganizadorId == _usuarioId), ct);
            if (evento == null)
                return new { encontrado = false, mensaje = "No encontré un evento tuyo con ese nombre." };
            id = evento.Id;
        }

        var v = await _acciones.VentasAsync(_usuarioId, id, ct);
        if (!v.Encontrado)
            return new { encontrado = false, mensaje = "No encontré un evento tuyo con ese nombre." };

        Dashboard = v.Dashboard;
        EventoId = v.EventoId;
        Enlazar(id == null ? "Panel de ventas" : "Abrir panel del evento", id == null ? "/Panel" : $"/Panel/Evento/{id}");

        return new
        {
            encontrado = true,
            evento = v.Evento,
            entradasVendidas = v.Vendidas,
            aforo = v.Aforo,
            ocupacion = $"{Math.Round(v.Ocupacion * 100):0} %",
            ingresos = Formato.Monto(v.Ingresos),
            comision = Formato.Monto(v.Comision),
            neto = Formato.Monto(v.Neto),
            porCobrar = v.PorCobrar.HasValue ? Formato.Monto(v.PorCobrar.Value) : null,
            ordenesPagadas = v.OrdenesPagadas,
            ticketPromedio = Formato.Monto(v.TicketPromedio),
            asistentesQueIngresaron = v.Ingresados,
            reservadasSinPagar = v.Reservadas,
            ultimos7Dias = new { entradas = v.VendidasUltimos7Dias, ingresos = Formato.Monto(v.IngresosUltimos7Dias) },
            zonas = v.Zonas.Select(z => new { z.Nombre, z.Vendidas, z.Capacidad, z.Disponibles, ingresos = Formato.Monto(z.Ingresos) }),
            eventos = v.Eventos.OrderByDescending(e => e.Ingresos).Take(10)
                .Select(e => new { e.Titulo, fecha = Fecha(e.Fecha), e.Estado, e.Vendidas, e.Aforo, ingresos = Formato.Monto(e.Ingresos) })
        };
    }

    [KernelFunction("MisEventosComoOrganizador")]
    [Description("Eventos que publicó el organizador autenticado: estado (pendiente, aprobado, rechazado, cancelado), fecha, entradas vendidas sobre aforo e ingresos.")]
    public async Task<object> MisEventosComoOrganizadorAsync(CancellationToken ct = default)
    {
        var eventos = await _db.Eventos.AsNoTracking()
            .Where(e => e.OrganizadorId == _usuarioId)
            .Select(e => new
            {
                e.Id, e.Titulo, e.Fecha, e.Estado, e.MotivoRechazo,
                Vendidas = e.TiposEntrada.Sum(t => t.Vendidas),
                Capacidad = e.TiposEntrada.Sum(t => t.Capacidad)
            })
            .ToListAsync(ct);

        var ingresos = await IngresosAsync(eventos.Select(e => e.Id).ToList(), ct);

        Enlazar("Mis eventos", "/Eventos/MisEventos");
        Enlazar("Panel de ventas", "/Panel");
        return new
        {
            total = eventos.Count,
            eventos = eventos.OrderByDescending(e => e.Fecha).Take(15).Select(e => new
            {
                e.Id, e.Titulo, fecha = Fecha(e.Fecha), estado = e.Estado.ToString(), e.MotivoRechazo,
                vendidas = e.Vendidas, aforo = e.Capacidad,
                ocupacion = e.Capacidad == 0 ? "0 %" : $"{100 * e.Vendidas / e.Capacidad} %",
                ingresos = Formato.Monto(ingresos.GetValueOrDefault(e.Id).Total),
                netoParaElOrganizador = Formato.Monto(ingresos.GetValueOrDefault(e.Id).Neto)
            })
        };
    }

    [KernelFunction("VentasDeMiEvento")]
    [Description("Detalle de ventas de un evento del organizador autenticado: vendidas y disponibles por zona, ingresos, comisión y asistentes que ya ingresaron.")]
    public async Task<object> VentasDeMiEventoAsync(
        [Description("Nombre del evento")] string? nombre = null,
        [Description("Id del evento, si se conoce")] int? eventoId = null,
        CancellationToken ct = default)
    {
        var evento = await BuscarEventoAsync(_db, nombre, eventoId, _db.Eventos.Where(e => e.OrganizadorId == _usuarioId), ct);
        if (evento == null)
            return new { encontrado = false, mensaje = "No encontré un evento tuyo con ese nombre." };

        EventoId = evento.Id;
        Enlazar($"Panel de {evento.Titulo}", $"/Panel/Evento/{evento.Id}");

        var ingresos = (await IngresosAsync(new List<int> { evento.Id }, ct)).GetValueOrDefault(evento.Id);
        var ingresaron = await _db.Entradas.CountAsync(e => e.EventoId == evento.Id && e.Estado == EstadoEntrada.Usada, ct);

        return new
        {
            encontrado = true,
            evento.Titulo,
            fecha = Fecha(evento.Fecha),
            estado = evento.Estado.ToString(),
            zonas = evento.TiposEntrada.Select(t => new { t.Nombre, precio = Formato.Precio(t.Precio), t.Vendidas, t.Disponibles, t.Capacidad }),
            ingresos = Formato.Monto(ingresos.Total),
            comision = Formato.Monto(ingresos.Comision),
            neto = Formato.Monto(ingresos.Neto),
            asistentesQueIngresaron = ingresaron
        };
    }

    // Los montos se suman en memoria (SQLite no agrega decimales en el servidor)
    private async Task<Dictionary<int, (decimal Total, decimal Comision, decimal Neto)>> IngresosAsync(List<int> eventos, CancellationToken ct)
    {
        var ordenes = await _db.Ordenes.AsNoTracking()
            .Where(o => eventos.Contains(o.EventoId) && o.Estado == EstadoOrden.Pagada)
            .Select(o => new { o.EventoId, o.Total, o.Comision })
            .ToListAsync(ct);
        return ordenes.GroupBy(o => o.EventoId).ToDictionary(g => g.Key,
            g => (g.Sum(o => o.Total), g.Sum(o => o.Comision), g.Sum(o => o.Total - o.Comision)));
    }
}

// ---------- Administrador ----------
// Solo cifras agregadas: nunca devuelve correos, teléfonos ni documentos de las personas
public class FuncionesAdministrador : FuncionesBase
{
    public const string NombrePlugin = "Administracion";

    private readonly ApplicationDbContext _db;
    private readonly string? _usuarioId;
    private readonly IAccionesAsistente? _acciones;

    public FuncionesAdministrador(ApplicationDbContext db, string? usuarioId = null, IAccionesAsistente? acciones = null)
    {
        _db = db;
        _usuarioId = usuarioId;
        _acciones = acciones;
    }

    [KernelFunction("AvisarmeDeEventosPorAprobar")]
    [Description("Activa (o quita, con activar=false) una notificación para el administrador cada vez que lleguen eventos nuevos esperando moderación.")]
    public async Task<object> AvisarmeDeEventosPorAprobarAsync(
        [Description("true para activar el aviso, false para quitarlo")] bool activar = true,
        CancellationToken ct = default)
    {
        if (_usuarioId == null || _acciones == null)
            return new { activo = false, mensaje = "No disponible." };

        Enlazar("🔔 Mis notificaciones", "/Notificaciones");
        var r = await _acciones.AvisarEventosPorAprobarAsync(_usuarioId, activar, ct);
        return new { activo = r.Activo, mensaje = r.Mensaje };
    }

    [KernelFunction("ResumenDeLaPlataforma")]
    [Description("Resumen general para el administrador: usuarios registrados, eventos por estado, pendientes de moderación, solicitudes de organizador y de personal pendientes, y ventas y comisiones de los últimos 30 días.")]
    public async Task<object> ResumenDeLaPlataformaAsync(CancellationToken ct = default)
    {
        var desde = DateTime.UtcNow.AddDays(-30);
        var porEstado = await _db.Eventos.AsNoTracking().GroupBy(e => e.Estado).Select(g => new { Estado = g.Key, Total = g.Count() }).ToListAsync(ct);
        var ventas = await _db.Ordenes.AsNoTracking()
            .Where(o => o.Estado == EstadoOrden.Pagada && o.FechaPago >= desde)
            .Select(o => new { o.Total, o.Comision })
            .ToListAsync(ct);

        Enlazar("Administración", "/Admin");
        Enlazar("Analítica", "/Analitica");

        return new
        {
            usuariosRegistrados = await _db.Users.CountAsync(ct),
            usuariosActivos = await _db.Users.CountAsync(u => u.Activo, ct),
            eventosPorEstado = porEstado.ToDictionary(e => e.Estado.ToString(), e => e.Total),
            eventosPendientesDeModeracion = await _db.Eventos.CountAsync(e => e.Estado == EstadoEvento.Pendiente && !e.PendientePagoPublicacion, ct),
            solicitudesOrganizadorPendientes = await _db.SolicitudesOrganizador.CountAsync(s => s.Estado == EstadoSolicitud.Pendiente, ct),
            solicitudesPersonalPendientes = await _db.SolicitudesPersonal.CountAsync(s => s.Estado == EstadoSolicitud.Pendiente, ct),
            ultimos30Dias = new
            {
                ordenesPagadas = ventas.Count,
                ventas = Formato.Monto(ventas.Sum(v => v.Total)),
                comisiones = Formato.Monto(ventas.Sum(v => v.Comision))
            }
        };
    }

    [KernelFunction("EventosPendientesDeModeracion")]
    [Description("Lista los eventos que esperan aprobación del administrador, del más antiguo al más reciente.")]
    public async Task<object> EventosPendientesDeModeracionAsync(CancellationToken ct = default)
    {
        var pendientes = await _db.Eventos.AsNoTracking()
            .Where(e => e.Estado == EstadoEvento.Pendiente && !e.PendientePagoPublicacion)
            .OrderBy(e => e.FechaCreacion)
            .Select(e => new { e.Id, e.Titulo, e.Fecha, e.FechaCreacion, Organizador = e.Organizador != null ? e.Organizador.NombreCompleto : null })
            .Take(15)
            .ToListAsync(ct);

        Enlazar("Moderar eventos", "/Admin");
        return pendientes.Select(e => new
        {
            e.Id, e.Titulo, fechaEvento = Fecha(e.Fecha),
            enviadoHaceDias = (int)(DateTime.UtcNow - e.FechaCreacion).TotalDays,
            e.Organizador
        }).ToList();
    }
}
