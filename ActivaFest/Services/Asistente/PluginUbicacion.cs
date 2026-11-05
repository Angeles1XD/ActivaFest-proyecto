using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using ActivaFest.Services.Plano;

namespace ActivaFest.Services.Asistente;

// =============================
// 🧩 FUNCIÓN DE SEMANTIC KERNEL: ObtenerUbicacionDeMiEntrada (HU28)
// =============================
// La IA no adivina la ubicación: llama a esta función de solo lectura. El usuario va fijado al crear el plugin
// con la sesión del servidor, así que la IA nunca puede pedir las entradas de otra persona.
public class PluginUbicacion
{
    public const string NombrePlugin = "Ubicacion";
    public const string NombreFuncion = "ObtenerUbicacionDeMiEntrada";

    private readonly IUbicacionService _ubicacion;
    private readonly string _usuarioId;

    public PluginUbicacion(IUbicacionService ubicacion, string usuarioId)
    {
        _ubicacion = ubicacion;
        _usuarioId = usuarioId;
    }

    [KernelFunction(NombreFuncion)]
    [Description("Devuelve la zona, fila, asiento, puerta e indicaciones para llegar de las entradas activas del usuario autenticado.")]
    public Task<List<UbicacionMiEntrada>> ObtenerUbicacionDeMiEntradaAsync(
        [Description("Fecha del evento en formato yyyy-MM-dd (opcional)")] string? fecha = null,
        [Description("Id del evento (opcional)")] int? eventoId = null,
        CancellationToken ct = default)
    {
        DateTime? dia = DateTime.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;
        return _ubicacion.ObtenerUbicacionDeMiEntradaAsync(_usuarioId, dia, eventoId, ct);
    }

    // Kernel con la función registrada para este usuario (lo usa el asistente; con Llama 3.2, la elige el modelo)
    public static Kernel KernelPara(IUbicacionService ubicacion, string usuarioId)
    {
        var kernel = Kernel.CreateBuilder().Build();
        kernel.Plugins.AddFromObject(new PluginUbicacion(ubicacion, usuarioId), NombrePlugin);
        return kernel;
    }
}
