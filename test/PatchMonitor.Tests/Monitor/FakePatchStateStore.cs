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
    private readonly HashSet<PatchKey> _failing = new();

    /// <summary>A partir de ahora, <see cref="RecordAssessmentAsync"/> lanza para esta clave.</summary>
    public void FailRecordFor(PatchKey key) => _failing.Add(key);

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
        return Task.FromResult(next);
    }

    public Task<PatchState?> GetStateAsync(PatchKey key, CancellationToken ct = default) =>
        Task.FromResult(_states.TryGetValue(key, out var state) ? state : null);

    public Task RegisterAsync(PatchKey key, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<PatchKey>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PatchKey>>(_states.Keys.ToArray());

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
}
