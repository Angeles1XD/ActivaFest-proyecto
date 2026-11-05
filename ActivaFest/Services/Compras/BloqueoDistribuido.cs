using System.Collections.Concurrent;
using StackExchange.Redis;

namespace ActivaFest.Services.Compras;

public interface IBloqueoDistribuido
{
    // Devuelve el bloqueo (se libera con DisposeAsync) o null si no se obtuvo dentro del tiempo de espera.
    // "duracion" es una red de seguridad: si el proceso muere con el bloqueo tomado, expira solo.
    Task<IAsyncDisposable?> AdquirirAsync(string clave, TimeSpan duracion, TimeSpan espera, CancellationToken ct = default);
}

// =============================
// 🔒 BLOQUEO DISTRIBUIDO CON REDIS (HU11)
// =============================
// Varias instancias de la app (p. ej. en Render) comparten el bloqueo por evento, así las reservas del mismo
// evento se procesan de una en una. SET clave valor NX PX = solo lo obtiene quien llega primero; al soltarlo
// se borra solo si sigue siendo nuestro (script Lua), para no liberar el bloqueo de otro.
public class BloqueoRedis : IBloqueoDistribuido
{
    private const string LiberarScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
    private static readonly TimeSpan Pausa = TimeSpan.FromMilliseconds(40);

    private readonly IConnectionMultiplexer _redis;
    private readonly BloqueoEnMemoria _respaldo;
    private readonly ILogger<BloqueoRedis> _logger;

    public BloqueoRedis(IConnectionMultiplexer redis, BloqueoEnMemoria respaldo, ILogger<BloqueoRedis> logger)
    {
        _redis = redis;
        _respaldo = respaldo;
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> AdquirirAsync(string clave, TimeSpan duracion, TimeSpan espera, CancellationToken ct = default)
    {
        var token = Guid.NewGuid().ToString("N");
        var limite = DateTime.UtcNow + espera;
        var clave2 = new RedisKey("activafest:bloqueo:" + clave);

        try
        {
            var db = _redis.GetDatabase();
            do
            {
                if (await db.StringSetAsync(clave2, token, duracion, When.NotExists))
                    return new Liberador(async () => await db.ScriptEvaluateAsync(LiberarScript, new[] { clave2 }, new RedisValue[] { token }));

                await Task.Delay(Pausa, ct);
            }
            while (DateTime.UtcNow < limite);

            return null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Si Redis no responde, la venta sigue: el UPDATE condicional con RowVersion ya impide sobrevender
            _logger.LogWarning("Redis no disponible para el bloqueo {Clave} ({Motivo}); se usa el bloqueo local", clave, ex.Message);
            return await _respaldo.AdquirirAsync(clave, duracion, espera, ct);
        }
    }

    private sealed class Liberador(Func<Task> liberar) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await liberar(); }
            catch (Exception) { /* expira solo por su duración */ }
        }
    }
}

// Bloqueo dentro de un solo proceso (desarrollo local o respaldo si Redis cae)
public class BloqueoEnMemoria : IBloqueoDistribuido
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaforos = new();

    public async Task<IAsyncDisposable?> AdquirirAsync(string clave, TimeSpan duracion, TimeSpan espera, CancellationToken ct = default)
    {
        var semaforo = _semaforos.GetOrAdd(clave, _ => new SemaphoreSlim(1, 1));
        return await semaforo.WaitAsync(espera, ct) ? new Liberador(semaforo) : null;
    }

    private sealed class Liberador(SemaphoreSlim semaforo) : IAsyncDisposable
    {
        private int _liberado;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _liberado, 1) == 0)
                semaforo.Release();
            return ValueTask.CompletedTask;
        }
    }
}

// =============================
// 🪑 BLOQUEO DE ASIENTOS POR 10 MINUTOS (HU26)
// =============================
// Al elegir asientos, cada uno queda apartado en Redis para esa compra ("asiento:{id}" = código de la orden)
// durante la reserva. Es la primera barrera (rápida y compartida entre instancias); la segunda es la BD:
// el asiento pasa a Bloqueado con un UPDATE condicional y ENTRADA.AsientoId tiene índice único.
public interface IBloqueoAsientos
{
    // Bloquea todos o ninguno: si alguno ya está tomado por otra compra devuelve sus ids y no bloquea nada
    Task<IReadOnlyList<int>> BloquearAsync(IReadOnlyCollection<int> asientos, string dueño, TimeSpan duracion, CancellationToken ct = default);

    // Libera solo los asientos que sigan siendo de ese dueño
    Task LiberarAsync(IReadOnlyCollection<int> asientos, string dueño, CancellationToken ct = default);
}

public class BloqueoAsientosRedis : IBloqueoAsientos
{
    private const string LiberarScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly IConnectionMultiplexer _redis;
    private readonly BloqueoAsientosEnMemoria _respaldo;
    private readonly ILogger<BloqueoAsientosRedis> _logger;

    public BloqueoAsientosRedis(IConnectionMultiplexer redis, BloqueoAsientosEnMemoria respaldo, ILogger<BloqueoAsientosRedis> logger)
    {
        _redis = redis;
        _respaldo = respaldo;
        _logger = logger;
    }

    private static RedisKey Clave(int asiento) => "activafest:asiento:" + asiento;

    public async Task<IReadOnlyList<int>> BloquearAsync(IReadOnlyCollection<int> asientos, string dueño, TimeSpan duracion, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var tomados = new List<int>();
            var ocupados = new List<int>();

            foreach (var id in asientos)
            {
                if (await db.StringSetAsync(Clave(id), dueño, duracion, When.NotExists) || await db.StringGetAsync(Clave(id)) == dueño)
                    tomados.Add(id);
                else
                    ocupados.Add(id);
            }

            if (ocupados.Count > 0)
                await LiberarAsync(tomados, dueño, ct);

            return ocupados;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning("Redis no disponible para bloquear asientos ({Motivo}); se usa el bloqueo local", ex.Message);
            return await _respaldo.BloquearAsync(asientos, dueño, duracion, ct);
        }
    }

    public async Task LiberarAsync(IReadOnlyCollection<int> asientos, string dueño, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            foreach (var id in asientos)
                await db.ScriptEvaluateAsync(LiberarScript, new[] { Clave(id) }, new RedisValue[] { dueño });
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            await _respaldo.LiberarAsync(asientos, dueño, ct); // si no, la clave expira sola a los 10 minutos
        }
    }
}

public class BloqueoAsientosEnMemoria : IBloqueoAsientos
{
    private readonly ConcurrentDictionary<int, (string Dueño, DateTime Vence)> _asientos = new();
    private readonly object _candado = new();

    public Task<IReadOnlyList<int>> BloquearAsync(IReadOnlyCollection<int> asientos, string dueño, TimeSpan duracion, CancellationToken ct = default)
    {
        lock (_candado)
        {
            var ahora = DateTime.UtcNow;
            var ocupados = asientos
                .Where(id => _asientos.TryGetValue(id, out var b) && b.Vence > ahora && b.Dueño != dueño)
                .ToList();

            if (ocupados.Count == 0)
                foreach (var id in asientos)
                    _asientos[id] = (dueño, ahora + duracion);

            return Task.FromResult<IReadOnlyList<int>>(ocupados);
        }
    }

    public Task LiberarAsync(IReadOnlyCollection<int> asientos, string dueño, CancellationToken ct = default)
    {
        lock (_candado)
        {
            foreach (var id in asientos)
                if (_asientos.TryGetValue(id, out var b) && b.Dueño == dueño)
                    _asientos.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }
}
