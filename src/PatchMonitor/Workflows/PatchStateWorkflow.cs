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
/// La ejecución nunca cierra por sí sola (es un entity workflow): <see cref="RunAsync"/> hace
/// <c>Continue-As-New</c> cuando <see cref="PatchState.AssessmentCount"/> supera
/// <see cref="StateOptions.ContinueAsNewThreshold"/> y no queda ningún handler en vuelo,
/// arrastrando el estado recortado por <see cref="PatchState.ForCarryover"/>.
/// </remarks>
[Workflow]
public class PatchStateWorkflow : IPatchStateWorkflow
{
    /// <summary>Marker de la migración de opciones de entities antiguas (spec 17).</summary>
    internal const string MigrateOptionsPatchId = "state-options-recorded-v1";

    private static readonly PatchPhase[] ValidOverridePhases =
    {
        PatchPhase.Coexistence,
        PatchPhase.Deprecated,
        PatchPhase.Clean,
    };

    // Nulo en una ejecución antigua (arrancada antes del spec 14) hasta que MigrateOptionsAsync
    // las graba. Nunca se completa desde el entorno: leerlo no es determinístico (spec 17).
    private StateOptions? _options;
    private PatchState _state;

    /// <summary>
    /// Corre antes que cualquier signal/update, incluido uno entregado por
    /// <c>signal-with-start</c> en la misma tanda que el arranque: sin este constructor,
    /// <see cref="RecordAssessmentAsync"/> podía ejecutar con <c>_state</c> todavía en su
    /// default y tirar <see cref="NullReferenceException"/>.
    /// </summary>
    [WorkflowInit]
    public PatchStateWorkflow(PatchKey key, PatchState? carryover, StateOptions? options = null)
    {
        // Las opciones llegan como argumento de arranque y el Continue-As-New las arrastra:
        // leer el entorno acá no sería determinístico (spec 14). Una ejecución antigua, arrancada
        // sin el argumento, queda con _options nulo (spec 17) y no lee el entorno: su replay es
        // el mismo con cualquier valor de PATCH_STATE_*. Recibe las opciones por
        // MigrateOptionsAsync y desde ahí queda determinística.
        _options = options;
        _state = carryover ?? PatchState.Initial(key);
    }

    [WorkflowRun]
    public async Task RunAsync(PatchKey key, PatchState? carryover, StateOptions? options = null)
    {
        // Se espera a tener opciones grabadas, al umbral de assessments Y a que no haya handlers
        // en vuelo: un Continue-As-New con un update de override a medio aplicar perdería la
        // actualización. Sin opciones (ejecución antigua sin migrar) la condición nunca se cumple,
        // igual que en su historia grabada.
        await Workflow.WaitConditionAsync(
            () => _options is { } o
                  && _state.AssessmentCount >= o.ContinueAsNewThreshold
                  && Workflow.AllHandlersFinished);

        var recorded = _options!;
        throw Workflow.CreateContinueAsNewException(
            (IPatchStateWorkflow wf) => wf.RunAsync(key, _state.ForCarryover(recorded.HistoryLimit), recorded));
    }

    [WorkflowSignal]
    public Task MigrateOptionsAsync(StateOptions options)
    {
        // Idempotente: una ejecución con opciones (propias o ya migradas) no cambia.
        if (_options is not null)
        {
            return Task.CompletedTask;
        }

        // El marker se evalúa en vivo (la señal no existe en historias viejas), así que devuelve
        // true y queda grabado; un replay posterior lo encuentra y repite la misma decisión.
        if (Workflow.Patched(MigrateOptionsPatchId))
        {
            _options = options;
        }

        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public bool HasRecordedOptions() => _options is not null;

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

    /// <summary>
    /// Comparación y asignación simple sobre <see cref="_state"/>: sin validator, a diferencia
    /// de <see cref="SetOverrideAsync"/>, porque no hay entrada de usuario que rechazar.
    /// </summary>
    [WorkflowUpdate]
    public Task<bool> TryClaimNotificationAsync(int revision)
    {
        if (revision <= _state.NotifiedRevision)
        {
            return Task.FromResult(false);
        }

        _state = _state with { NotifiedRevision = revision };
        return Task.FromResult(true);
    }

    private IReadOnlyList<PatchStateChange> AppendTrimmed(
        IReadOnlyList<PatchStateChange> history, PatchStateChange change)
    {
        var next = new List<PatchStateChange>(history) { change };
        var limit = _options?.HistoryLimit ?? StateOptions.DefaultHistoryLimit;
        if (next.Count > limit)
        {
            next.RemoveRange(0, next.Count - limit);
        }

        return next;
    }
}
