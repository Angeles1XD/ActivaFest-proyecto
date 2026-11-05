using Microsoft.Extensions.Caching.Distributed;

namespace ActivaFest.Services;

// =============================
// 🛟 CACHÉ RESILIENTE (Redis con respaldo en memoria)
// =============================
// Si Redis no responde (servidor caído, credenciales vencidas, red), la app NO se cae: usa la caché en memoria
// durante un minuto y luego vuelve a intentar con Redis. Así una falla de la caché nunca tumba el sitio (RNF03).
public class CacheResiliente : IDistributedCache
{
    private static readonly TimeSpan Pausa = TimeSpan.FromMinutes(1);

    private readonly IDistributedCache _principal;
    private readonly IDistributedCache _respaldo;
    private readonly ILogger<CacheResiliente> _logger;
    private readonly Func<DateTime> _ahora;
    private DateTime _pausadoHasta = DateTime.MinValue;

    public CacheResiliente(IDistributedCache principal, IDistributedCache respaldo, ILogger<CacheResiliente> logger,
        Func<DateTime>? reloj = null)
    {
        _principal = principal;
        _respaldo = respaldo;
        _logger = logger;
        _ahora = reloj ?? (() => DateTime.UtcNow);
    }

    public bool UsandoRespaldo => _ahora() < _pausadoHasta;

    private T Ejecutar<T>(Func<IDistributedCache, T> accion)
    {
        if (!UsandoRespaldo)
        {
            try { return accion(_principal); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Pausar(ex); }
        }
        return accion(_respaldo);
    }

    private async Task<T> EjecutarAsync<T>(Func<IDistributedCache, Task<T>> accion)
    {
        if (!UsandoRespaldo)
        {
            try { return await accion(_principal); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Pausar(ex); }
        }
        return await accion(_respaldo);
    }

    private void Pausar(Exception ex)
    {
        _pausadoHasta = _ahora().Add(Pausa);
        _logger.LogWarning("⚠️ La caché distribuida (Redis) no responde: se usa la caché en memoria durante {Minutos} min. Motivo: {Motivo}",
            Pausa.TotalMinutes, ex.GetBaseException().Message);
    }

    public byte[]? Get(string key) => Ejecutar(c => c.Get(key));

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => EjecutarAsync(c => c.GetAsync(key, token));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Ejecutar(c => { c.Set(key, value, options); return true; });

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        EjecutarAsync(async c => { await c.SetAsync(key, value, options, token); return true; });

    public void Refresh(string key) => Ejecutar(c => { c.Refresh(key); return true; });

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        EjecutarAsync(async c => { await c.RefreshAsync(key, token); return true; });

    public void Remove(string key) => Ejecutar(c => { c.Remove(key); return true; });

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        EjecutarAsync(async c => { await c.RemoveAsync(key, token); return true; });
}
