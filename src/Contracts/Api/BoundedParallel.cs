namespace Contracts.Api;

/// <summary>
/// Ejecuta una operación asíncrona por elemento con a lo sumo <c>maxConcurrency</c> en vuelo y
/// devuelve los resultados en el orden de la entrada. Sirve para las lecturas de estado de
/// <c>GET /patches</c> y de los overrides activos, que antes eran una consulta tras otra
/// (spec 16, B-5). Si una operación lanza, la excepción se propaga una vez que terminaron las
/// ya iniciadas; el llamador que no quiera eso captura dentro de <c>operation</c>.
/// </summary>
public static class BoundedParallel
{
    public static async Task<TOut[]> SelectAsync<TIn, TOut>(
        IReadOnlyList<TIn> items,
        int maxConcurrency,
        Func<TIn, CancellationToken, Task<TOut>> operation,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await operation(item, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
