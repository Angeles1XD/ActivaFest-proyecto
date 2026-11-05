using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Email;
using ActivaFest.Services.Imagenes;
using ActivaFest.Services.Plano;

namespace ActivaFest.Services.Eventos;

public record ResultadoEdicion(bool Exito, List<string> Errores, int CompradoresNotificados = 0)
{
    public static ResultadoEdicion Fallo(params string[] errores) => new(false, errores.ToList());
}

public interface IEventoEdicionService
{
    Task<ResultadoEdicion> ActualizarAsync(int eventoId, EventoFormViewModel datos, string urlEvento, CancellationToken ct = default);
}

// =============================
// ✏️ EDICIÓN DE EVENTOS (HU09 / RF07)
// =============================
// Protege lo ya vendido: la capacidad nunca baja de lo comprometido y no se borran zonas con compras.
public class EventoEdicionService : IEventoEdicionService
{
    private readonly ApplicationDbContext _db;
    private readonly IAlmacenamientoImagenes _imagenes;
    private readonly ICatalogoService _catalogo;
    private readonly IEmailService _email;
    private readonly ILogger<EventoEdicionService> _logger;

    public EventoEdicionService(ApplicationDbContext db, IAlmacenamientoImagenes imagenes, ICatalogoService catalogo,
        IEmailService email, ILogger<EventoEdicionService> logger)
    {
        _db = db;
        _imagenes = imagenes;
        _catalogo = catalogo;
        _email = email;
        _logger = logger;
    }

    public async Task<ResultadoEdicion> ActualizarAsync(int eventoId, EventoFormViewModel datos, string urlEvento, CancellationToken ct = default)
    {
        var evento = await _db.Eventos
            .Include(e => e.TiposEntrada)
            .Include(e => e.Imagenes)
            .AsSplitQuery()
            .FirstOrDefaultAsync(e => e.Id == eventoId, ct);

        if (evento == null)
            return ResultadoEdicion.Fallo("El evento no existe.");

        // Un evento cancelado es un estado final (diagrama de estados): no se edita ni vuelve a revisión
        if (evento.Estado == EstadoEvento.Cancelado)
            return ResultadoEdicion.Fallo("Este evento fue cancelado y ya no se puede editar.");

        var errores = new List<string>();

        // ---------- Zonas de entrada ----------
        var enviadas = datos.TiposEntrada;

        // HU34: un evento no pasa de pago a gratuito ni al revés (cambia el cobro y la venta de entradas)
        if (enviadas.Count > 0 && enviadas.All(t => t.Precio == 0) != evento.EsGratuito)
            errores.Add(evento.EsGratuito
                ? "Este evento es de entrada libre: no se le pueden poner precios. Crea un evento nuevo con entradas pagadas."
                : "Este evento vende entradas: no se puede convertir en gratuito. Crea un evento nuevo de entrada libre.");
        var idsEnviados = enviadas.Where(t => t.Id.HasValue).Select(t => t.Id!.Value).ToHashSet();

        if (idsEnviados.Any(id => evento.TiposEntrada.All(t => t.Id != id)))
            return ResultadoEdicion.Fallo("Zona de entrada no válida.");

        var conCompras = await _db.OrdenItems
            .Where(i => i.TipoEntrada!.EventoId == eventoId)
            .Select(i => i.TipoEntradaId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var tipo in evento.TiposEntrada.Where(t => !idsEnviados.Contains(t.Id)).ToList())
        {
            if (conCompras.Contains(tipo.Id))
                errores.Add($"La zona \"{tipo.Nombre}\" ya tiene compras: no se puede eliminar.");
            else
                _db.TiposEntrada.Remove(tipo);
        }

        // ---------- Plano del recinto (HU25) ----------
        // Cambiar la plantilla o la distribución de asientos regenera el plano: solo se permite sin compras
        var plantilla = datos.Plantilla;
        var cambiaDistribucion = plantilla != evento.PlantillaPlano
            || evento.TiposEntrada.Any(t => !idsEnviados.Contains(t.Id))
            || enviadas.Any(t => !t.Id.HasValue)
            || (plantilla == PlantillaPlano.Teatro && enviadas.Any(t =>
                    evento.TiposEntrada.First(z => z.Id == t.Id).Filas != t.Filas ||
                    evento.TiposEntrada.First(z => z.Id == t.Id).AsientosPorFila != t.AsientosPorFila));

        if (cambiaDistribucion && conCompras.Count > 0 && (plantilla == PlantillaPlano.Teatro || plantilla != evento.PlantillaPlano))
            errores.Add("El evento ya tiene compras: no puedes cambiar la plantilla del plano ni la distribución de asientos.");

        for (var i = 0; i < enviadas.Count; i++)
        {
            var input = enviadas[i];
            if (input.Id is int id)
            {
                var tipo = evento.TiposEntrada.First(t => t.Id == id);
                var comprometidas = tipo.Vendidas + tipo.Reservadas;
                if (input.Capacidad < comprometidas)
                {
                    errores.Add($"La zona \"{tipo.Nombre}\" ya tiene {comprometidas} entradas vendidas o reservadas: su capacidad no puede ser menor.");
                    continue;
                }

                tipo.Nombre = input.Nombre.Trim();
                tipo.Precio = Math.Round(input.Precio, 2); // solo afecta a compras futuras: cada orden guarda su precio
                tipo.Capacidad = input.Capacidad;
                PlanoService.AplicarZona(tipo, input, plantilla, i);
            }
            else
            {
                evento.TiposEntrada.Add(PlanoService.AplicarZona(
                    new TipoEntrada { Nombre = input.Nombre.Trim(), Precio = Math.Round(input.Precio, 2), Capacidad = input.Capacidad },
                    input, plantilla, i));
            }
        }

        // ---------- Fotos ----------
        var aEliminar = evento.Imagenes.Where(i => datos.ImagenesEliminar.Contains(i.Id)).ToList();
        var nuevas = datos.Fotos.Where(f => f.Length > 0).ToList();
        // FotosSubidas: las de un envío anterior con errores (el controlador ya comprobó que son de este usuario)
        var total = evento.Imagenes.Count - aEliminar.Count + nuevas.Count + datos.FotosSubidas.Count;

        if (total < EventoFormViewModel.MinFotos)
            errores.Add($"El evento debe tener al menos {EventoFormViewModel.MinFotos} fotos.");
        if (total > EventoFormViewModel.MaxFotos)
            errores.Add($"El evento puede tener como máximo {EventoFormViewModel.MaxFotos} fotos.");

        foreach (var foto in nuevas)
        {
            var error = ValidadorImagenes.Validar(foto);
            if (error != null)
                errores.Add(error);
        }

        // ---------- Plano oficial del recinto (imagen opcional) ----------
        var planoNuevo = datos.PlanoOficialArchivo is { Length: > 0 } archivoPlano ? archivoPlano : null;
        if (planoNuevo != null && ValidadorImagenes.Validar(planoNuevo) is { } errorPlano)
            errores.Add($"Plano oficial: {errorPlano}");
        var planoAnterior = planoNuevo != null || datos.PlanoSubido != null || datos.QuitarPlanoOficial ? evento.PlanoOficial : null;

        if (errores.Count > 0)
            return new ResultadoEdicion(false, errores);

        // ---------- Datos generales ----------
        var fechaAnterior = evento.Fecha;
        var ubicacionAnterior = evento.Ubicacion;

        evento.Titulo = datos.Titulo.Trim();
        evento.Descripcion = datos.Descripcion.Trim();
        evento.CategoriaId = datos.CategoriaId!.Value;
        evento.Fecha = datos.Fecha!.Value;
        evento.Ubicacion = datos.Ubicacion.Trim();
        evento.Latitud = Math.Round(datos.Latitud!.Value, 6);
        evento.Longitud = Math.Round(datos.Longitud!.Value, 6);
        evento.Aforo = evento.TiposEntrada.Sum(t => t.Capacidad);
        evento.PlantillaPlano = plantilla;

        // Un evento publicado sigue publicado (tiene ventas en curso); uno pendiente o rechazado vuelve a revisión
        if (evento.Estado != EstadoEvento.Aprobado)
        {
            evento.Estado = EstadoEvento.Pendiente;
            evento.MotivoRechazo = null;
        }

        var rutasNuevas = new List<string>();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Cada zona existente se actualiza con un UPDATE condicional que sube su RowVersion (HU11):
            // si justo entró una compra, la capacidad no puede quedar por debajo de lo vendido
            foreach (var input in enviadas.Where(t => t.Id.HasValue))
            {
                // Valores ya normalizados (en teatro la capacidad es filas x asientos)
                var z = evento.TiposEntrada.First(t => t.Id == input.Id);
                var (nueva, nombre, precio) = (z.Capacidad, z.Nombre, z.Precio);
                var (color, puerta, numerados, filas, porFila) = (z.Color, z.PuertaIngreso, z.AsientosNumerados, z.Filas, z.AsientosPorFila);
                var ok = await _db.TiposEntrada
                    .Where(t => t.Id == input.Id && t.Vendidas + t.Reservadas <= nueva)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Capacidad, nueva)
                        .SetProperty(t => t.Nombre, nombre)
                        .SetProperty(t => t.Precio, precio)
                        .SetProperty(t => t.Color, color)
                        .SetProperty(t => t.PuertaIngreso, puerta)
                        .SetProperty(t => t.AsientosNumerados, numerados)
                        .SetProperty(t => t.Filas, filas)
                        .SetProperty(t => t.AsientosPorFila, porFila)
                        .SetProperty(t => t.RowVersion, t => t.RowVersion + 1), ct);

                if (ok == 0)
                {
                    await tx.RollbackAsync(ct);
                    return ResultadoEdicion.Fallo("Se vendieron entradas mientras editabas: revisa las capacidades e inténtalo de nuevo.");
                }
            }

            var orden = evento.Imagenes.Except(aEliminar).Select(i => i.Orden).DefaultIfEmpty(-1).Max() + 1;
            foreach (var ruta in datos.FotosSubidas)
                evento.Imagenes.Add(new EventoImagen { Ruta = ruta, Orden = orden++ });
            foreach (var foto in nuevas)
            {
                var ruta = await _imagenes.GuardarAsync(foto, "eventos", ct);
                rutasNuevas.Add(ruta);
                evento.Imagenes.Add(new EventoImagen { Ruta = ruta, Orden = orden++ });
            }

            if (planoNuevo != null)
            {
                evento.PlanoOficial = await _imagenes.GuardarAsync(planoNuevo, "planos", ct);
                rutasNuevas.Add(evento.PlanoOficial);
            }
            else if (datos.PlanoSubido != null)
            {
                evento.PlanoOficial = datos.PlanoSubido;
            }
            else if (datos.QuitarPlanoOficial)
            {
                evento.PlanoOficial = null;
            }

            // Las zonas existentes ya se guardaron arriba; los contadores de venta los maneja solo CompraService
            foreach (var tipo in evento.TiposEntrada.Where(t => _db.Entry(t).State == EntityState.Modified))
                _db.Entry(tipo).State = EntityState.Unchanged;

            _db.EventoImagenes.RemoveRange(aEliminar);
            await _db.SaveChangesAsync(ct);

            // HU25: si cambió la distribución, el plano se vuelve a generar desde la plantilla
            if (cambiaDistribucion)
            {
                await _db.Asientos.Where(a => a.TipoEntrada!.EventoId == eventoId).ExecuteDeleteAsync(ct);
                if (plantilla == PlantillaPlano.Teatro)
                {
                    var zonas = await _db.TiposEntrada.AsNoTracking().Where(t => t.EventoId == eventoId).OrderBy(t => t.Id).ToListAsync(ct);
                    var asientos = PlanoService.GenerarAsientos(zonas);
                    asientos.ForEach(a => a.TipoEntrada = null);
                    _db.Asientos.AddRange(asientos);
                    await _db.SaveChangesAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Una zona que se quería eliminar recibió una compra mientras se editaba (su RowVersion cambió)
            await tx.RollbackAsync(CancellationToken.None);
            foreach (var ruta in rutasNuevas)
                await _imagenes.EliminarAsync(ruta, CancellationToken.None);
            return ResultadoEdicion.Fallo("Se vendieron entradas mientras editabas: revisa las zonas e inténtalo de nuevo.");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _logger.LogError(ex, "Error al actualizar el evento {Id}", eventoId);
            foreach (var ruta in rutasNuevas)
                await _imagenes.EliminarAsync(ruta, CancellationToken.None);
            return ResultadoEdicion.Fallo("No se pudo guardar el evento. Inténtalo de nuevo.");
        }

        // Los archivos quitados se borran recién cuando la BD ya no los referencia
        foreach (var imagen in aEliminar)
            await _imagenes.EliminarAsync(imagen.Ruta, CancellationToken.None);
        if (!string.IsNullOrEmpty(planoAnterior))
            await _imagenes.EliminarAsync(planoAnterior, CancellationToken.None);

        if (evento.Estado == EstadoEvento.Aprobado)
            await _catalogo.InvalidarAsync(ct);

        // ---------- Aviso a compradores si cambió la fecha o el lugar (RF13) ----------
        var notificados = 0;
        if (fechaAnterior != evento.Fecha || ubicacionAnterior != evento.Ubicacion)
        {
            var correos = await _db.Entradas
                .Where(e => e.EventoId == eventoId && e.Estado == EstadoEntrada.Activa)
                .Join(_db.Users, e => e.UsuarioId, u => u.Id, (e, u) => u.Email)
                .Where(c => c != null)
                .Distinct()
                .ToListAsync(ct);

            var html = EmailTemplates.EventoActualizado(evento, fechaAnterior, ubicacionAnterior, urlEvento);
            foreach (var correo in correos)
                await _email.EnviarAsync(correo!, $"Cambio en tu evento: {evento.Titulo}", html);

            notificados = correos.Count;
        }

        return new ResultadoEdicion(true, new List<string>(), notificados);
    }
}
