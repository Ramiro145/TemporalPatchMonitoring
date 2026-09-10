using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Temporalio.Activities;

namespace PatchMonitor.Activities;

/// <summary>
/// Envoltura <c>[Activity]</c> del <see cref="IPatchStateStore"/> para que el
/// <c>MonitorWorkflow</c> del spec 06 lea y escriba el estado durable sin hacer RPC desde
/// dentro de un workflow. <see cref="LoadPhaseOverridesAsync"/> además hidrata el
/// <see cref="IPhaseOverrideStore"/> en memoria que consume el resolver (spec 04).
/// </summary>
public class PatchStateActivities
{
    private readonly IPatchStateStore _store;
    private readonly IPhaseOverrideStore _overrideCache;

    public PatchStateActivities(IPatchStateStore store, IPhaseOverrideStore overrideCache)
    {
        _store = store;
        _overrideCache = overrideCache;
    }

    [Activity]
    public Task<PatchState> RecordAssessmentAsync(PatchAssessmentInput input) =>
        _store.RecordAssessmentAsync(input);

    /// <summary>
    /// Trae los overrides vigentes de todos los entity workflows y sincroniza con ellos el
    /// <see cref="IPhaseOverrideStore"/> de corrida: hace <c>Clear</c> de los que ya no vienen
    /// del entity (borrados por el operador) y <c>Set</c> de los que sí. Devuelve la cantidad
    /// de overrides vigentes cargados.
    /// </summary>
    [Activity]
    public async Task<int> LoadPhaseOverridesAsync()
    {
        var active = await _store.LoadActiveOverridesAsync().ConfigureAwait(false);
        var activeIds = active
            .Select(ov => ov.Key.ToWorkflowId())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var cached in _overrideCache.GetAll())
        {
            if (!activeIds.Contains(cached.Key.ToWorkflowId()))
            {
                _overrideCache.Clear(cached.Key);
            }
        }

        foreach (var ov in active)
        {
            _overrideCache.Set(ov);
        }

        return active.Count;
    }

    [Activity]
    public Task<PatchState?> GetPatchStateAsync(PatchKey key) => _store.GetStateAsync(key);

    [Activity]
    public Task<IReadOnlyList<PatchKey>> ListPatchesAsync() => _store.ListAsync();
}
