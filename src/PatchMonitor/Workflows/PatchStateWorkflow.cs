using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Contracts.Workflows;
using Temporalio.Workflows;

namespace PatchMonitor.Workflows;

/// <summary>
/// Entity workflow que persiste el estado durable de un patch (spec 05). Uno por
/// <see cref="PatchKey"/>, con <c>WorkflowId = key.ToWorkflowId()</c>, arrancado por
/// <c>signal-with-start</c>. Acumula los assessments que llegan por signal, aplica el override
/// vigente sobre la fase recibida y avanza <see cref="PatchState.Revision"/> solo cuando la
/// tupla <c>(Phase, Verdict.Outcome, Verdict.NextPhase)</c> cambia.
/// </summary>
/// <remarks>
/// El <c>Continue-As-New</c> que mantiene la ejecución acotada llega en un paso posterior de
/// este spec; por ahora <see cref="RunAsync"/> se limita a hidratar el estado y esperar.
/// </remarks>
[Workflow]
public class PatchStateWorkflow : IPatchStateWorkflow
{
    private static readonly PatchPhase[] ValidOverridePhases =
    {
        PatchPhase.Coexistence,
        PatchPhase.Deprecated,
        PatchPhase.Clean,
    };

    private StateOptions _options = null!;
    private PatchState _state = null!;

    [WorkflowRun]
    public async Task RunAsync(PatchKey key, PatchState? carryover)
    {
        // Se lee una vez por ejecución. Tras un Continue-As-New la nueva instancia
        // vuelve a leer el entorno, así un cambio de umbral aplica sin redeploy del código.
        _options = StateOptions.FromEnvironment();
        _state = carryover ?? PatchState.Initial(key);

        // Entity workflow: se mantiene abierto indefinidamente. El Continue-As-New por
        // umbral se agrega más adelante en este spec.
        await Workflow.WaitConditionAsync(() => false);
    }

    [WorkflowSignal]
    public Task RecordAssessmentAsync(PatchAssessmentInput input)
    {
        var observedAt = input.ObservedAt;
        var activeOverride = _state.Override is { } ov && ov.IsActiveAt(observedAt)
            ? _state.Override
            : null;

        var phase = activeOverride is not null ? activeOverride.Phase : input.Resolution.Phase;
        var source = activeOverride is not null ? PhaseSource.Override : input.Resolution.Source;
        var reason = activeOverride is not null
            ? $"Override de {activeOverride.DeclaredBy}: fase forzada a {activeOverride.Phase}. " +
              $"Inferida: {input.Resolution.Phase} — {input.Resolution.Reason}"
            : input.Resolution.Reason;
        var verdict = input.Verdict;

        var changed =
            phase != _state.Phase
            || verdict?.Outcome != _state.LastVerdict?.Outcome
            || verdict?.NextPhase != _state.LastVerdict?.NextPhase;

        if (!changed)
        {
            // Igual ⇒ solo avanzan AssessmentCount y LastObservedAt.
            _state = _state with
            {
                LastObservedAt = observedAt,
                AssessmentCount = _state.AssessmentCount + 1,
            };
            return Task.CompletedTask;
        }

        var change = new PatchStateChange(
            observedAt,
            _state.Phase,
            phase,
            _state.LastVerdict?.Outcome,
            verdict?.Outcome,
            reason);

        _state = _state with
        {
            Phase = phase,
            Source = source,
            PhaseReason = reason,
            PreviousVerdict = _state.LastVerdict,
            LastVerdict = verdict,
            LastObservedAt = observedAt,
            LastChangedAt = observedAt,
            AssessmentCount = _state.AssessmentCount + 1,
            Revision = _state.Revision + 1,
            History = AppendTrimmed(_state.History, change),
        };

        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public PatchState GetState() => _state;

    /// <summary>
    /// Rechaza de forma sincrónica un override cuya fase no esté en
    /// <c>{Coexistence, Deprecated, Clean}</c>. Al lanzar acá el update ni siquiera entra a
    /// la Event History y el estado del entity queda intacto.
    /// </summary>
    [WorkflowUpdateValidator(nameof(SetOverrideAsync))]
    public void ValidateSetOverride(PhaseOverride ov)
    {
        if (Array.IndexOf(ValidOverridePhases, ov.Phase) < 0)
        {
            throw new ArgumentException(
                $"Fase de override inválida: {ov.Phase}. Debe ser Coexistence, Deprecated o Clean.");
        }
    }

    [WorkflowUpdate]
    public Task<PatchState> SetOverrideAsync(PhaseOverride ov)
    {
        _state = _state with
        {
            Override = ov,
            Phase = ov.Phase,
            Source = PhaseSource.Override,
            PhaseReason = $"Override de {ov.DeclaredBy}: fase forzada a {ov.Phase}.",
        };

        return Task.FromResult(_state);
    }

    [WorkflowUpdate]
    public Task<PatchState> ClearOverrideAsync()
    {
        _state = _state with { Override = null };
        return Task.FromResult(_state);
    }

    private IReadOnlyList<PatchStateChange> AppendTrimmed(
        IReadOnlyList<PatchStateChange> history, PatchStateChange change)
    {
        var next = new List<PatchStateChange>(history) { change };
        if (next.Count > _options.HistoryLimit)
        {
            next.RemoveRange(0, next.Count - _options.HistoryLimit);
        }

        return next;
    }
}
