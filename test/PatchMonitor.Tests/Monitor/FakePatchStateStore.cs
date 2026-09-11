using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Temporalio.Exceptions;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// <see cref="IPatchStateStore"/> en memoria para <c>MonitorWorkflowTests</c>: reproduce, sin
/// cluster, la misma regla de avance de <c>Revision</c> que <c>PatchStateWorkflow</c> (spec 05)
/// —solo sube cuando la tupla <c>(Phase, Verdict.Outcome, Verdict.NextPhase)</c> cambia—, para
/// poder asertar <see cref="Contracts.Monitor.MonitorRunSummary.VerdictsChanged"/> sin levantar
/// un entity workflow real. <see cref="FailRecordFor"/> simula un patch cuyo assessment no se
/// puede persistir, para los tests de aislamiento de fallos.
/// </summary>
public sealed class FakePatchStateStore : IPatchStateStore
{
    private readonly Dictionary<PatchKey, PatchState> _states = new();
    private readonly HashSet<PatchKey> _keys = new();
    private readonly HashSet<PatchKey> _failing = new();
    private readonly HashSet<PatchKey> _failingGet = new();

    /// <summary>A partir de ahora, <see cref="RecordAssessmentAsync"/> lanza para esta clave.</summary>
    public void FailRecordFor(PatchKey key) => _failing.Add(key);

    /// <summary>
    /// A partir de ahora, <see cref="GetStateAsync"/> lanza para esta clave (simula un entity
    /// ilegible); la clave igual aparece en <see cref="ListAsync"/>, como en el registry real.
    /// </summary>
    public void FailGetFor(PatchKey key)
    {
        _failingGet.Add(key);
        _keys.Add(key);
    }

    /// <summary>Siembra el estado de una clave directamente, sin pasar por <see cref="RecordAssessmentAsync"/>.</summary>
    public void Seed(PatchKey key, PatchState state)
    {
        _states[key] = state;
        _keys.Add(key);
    }

    public Task<PatchState> RecordAssessmentAsync(PatchAssessmentInput input, CancellationToken ct = default)
    {
        if (_failing.Contains(input.Key))
        {
            throw new ApplicationFailureException(
                $"fallo simulado registrando {input.Key}", errorType: "SimulatedFailure", nonRetryable: true);
        }

        var current = _states.TryGetValue(input.Key, out var existing)
            ? existing
            : PatchState.Initial(input.Key);

        var changed =
            input.Resolution.Phase != current.Phase
            || input.Verdict?.Outcome != current.LastVerdict?.Outcome
            || input.Verdict?.NextPhase != current.LastVerdict?.NextPhase;

        var next = changed
            ? current with
            {
                Phase = input.Resolution.Phase,
                Source = input.Resolution.Source,
                PhaseReason = input.Resolution.Reason,
                PreviousVerdict = current.LastVerdict,
                LastVerdict = input.Verdict,
                LastObservedAt = input.ObservedAt,
                LastChangedAt = input.ObservedAt,
                AssessmentCount = current.AssessmentCount + 1,
                Revision = current.Revision + 1,
            }
            : current with
            {
                LastObservedAt = input.ObservedAt,
                AssessmentCount = current.AssessmentCount + 1,
            };

        _states[input.Key] = next;
        _keys.Add(input.Key);
        return Task.FromResult(next);
    }

    public Task<PatchState?> GetStateAsync(PatchKey key, CancellationToken ct = default)
    {
        if (_failingGet.Contains(key))
        {
            throw new InvalidOperationException($"fallo simulado leyendo {key}");
        }

        return Task.FromResult(_states.TryGetValue(key, out var state) ? state : null);
    }

    public Task RegisterAsync(PatchKey key, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<PatchKey>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PatchKey>>(_keys.ToArray());

    public Task<IReadOnlyList<PhaseOverride>> LoadActiveOverridesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PhaseOverride>>(
            _states.Values
                .Where(s => s.Override is not null)
                .Select(s => s.Override!)
                .ToArray());

    public Task<PatchState> SetOverrideAsync(PhaseOverride ov, CancellationToken ct = default) =>
        throw new NotSupportedException("No lo usa MonitorWorkflow.");

    public Task<PatchState> ClearOverrideAsync(PatchKey key, CancellationToken ct = default) =>
        throw new NotSupportedException("No lo usa MonitorWorkflow.");

    public Task<bool> TryClaimNotificationAsync(PatchKey key, int revision, CancellationToken ct = default)
    {
        var current = _states.TryGetValue(key, out var existing) ? existing : PatchState.Initial(key);
        if (revision <= current.NotifiedRevision)
        {
            return Task.FromResult(false);
        }

        _states[key] = current with { NotifiedRevision = revision };
        return Task.FromResult(true);
    }
}
