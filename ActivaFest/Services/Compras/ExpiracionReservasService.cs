namespace ActivaFest.Services.Compras;

// =============================
// ⏰ LIBERA RESERVAS VENCIDAS (HU11)
// =============================
// Cada minuto devuelve al aforo las entradas de órdenes que no se pagaron a tiempo.
public class ExpiracionReservasService : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ExpiracionReservasService> _logger;

    public ExpiracionReservasService(IServiceScopeFactory scopes, ILogger<ExpiracionReservasService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(Intervalo);

        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ICompraService>().LiberarExpiradasAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error al liberar reservas vencidas");
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken));
    }
}
