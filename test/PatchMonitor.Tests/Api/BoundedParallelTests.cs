using Contracts.Api;
using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using MonitorApi.Endpoints;
using Xunit;

namespace PatchMonitor.Tests.Api;

/// <summary>
/// <see cref="BoundedParallel"/> y su uso en <see cref="PatchEndpoints.ListAsync"/> (spec 16,
/// B-5): la concurrencia observada nunca pasa del tope y el orden de salida es el de entrada.
/// </summary>
public class BoundedParallelTests
{
    [Fact]
    public async Task SelectAsync_conserva_el_orden_de_entrada_aunque_terminen_desordenadas()
    {
        var items = Enumerable.Range(0, 6).ToArray();

        var result = await BoundedParallel.SelectAsync(items, 3, async (i, _) =>
        {
            await Task.Delay((6 - i) * 10);
            return i * 10;
        });

        Assert.Equal(new[] { 0, 10, 20, 30, 40, 50 }, result);
    }

    [Fact]
    public async Task SelectAsync_no_supera_el_tope_de_concurrencia()
    {
        var probe = new ConcurrencyProbe();

        await BoundedParallel.SelectAsync(Enumerable.Range(0, 10).ToArray(), 2, async (_, _) =>
        {
            await probe.RunAsync();
            return 0;
        });

        Assert.Equal(2, probe.MaxObserved);
    }

    [Fact]
    public async Task ListAsync_lee_en_paralelo_acotado_y_conserva_el_orden_del_registry()
    {
        var keys = Enumerable.Range(1, 5)
            .Select(i => new PatchKey("default", "OrderWorkflow", $"patch-{i}"))
            .ToArray();
        var probe = new ConcurrencyProbe();
        var store = new ProbingStore(keys, probe);

        var result = await PatchEndpoints.ListAsync(
            store, new ApiOptions(100, TimeSpan.FromHours(24), 20, ListPatchesConcurrency: 2));

        Assert.Equal(keys.Select(k => k.PatchId), result.Value!.Patches.Select(p => p.PatchId));
        Assert.Equal(2, probe.MaxObserved);
    }

    private sealed class ConcurrencyProbe
    {
        private int _current;
        private int _max;

        public int MaxObserved => _max;

        public async Task RunAsync()
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)))
            {
                Interlocked.CompareExchange(ref _max, now, seen);
            }

            await Task.Delay(30);
            Interlocked.Decrement(ref _current);
        }
    }

    private sealed class ProbingStore : IPatchStateStore
    {
        private readonly PatchKey[] _keys;
        private readonly ConcurrencyProbe _probe;

        public ProbingStore(PatchKey[] keys, ConcurrencyProbe probe)
        {
            _keys = keys;
            _probe = probe;
        }

        public Task<IReadOnlyList<PatchKey>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PatchKey>>(_keys);

        public async Task<PatchState?> GetStateAsync(PatchKey key, CancellationToken ct = default)
        {
            await _probe.RunAsync();
            return PatchState.Initial(key);
        }

        public Task<PatchState> RecordAssessmentAsync(PatchAssessmentInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RegisterAsync(PatchKey key, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PhaseOverride>> LoadActiveOverridesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PatchState> SetOverrideAsync(PhaseOverride ov, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PatchState> ClearOverrideAsync(PatchKey key, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TryClaimNotificationAsync(PatchKey key, int revision, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
