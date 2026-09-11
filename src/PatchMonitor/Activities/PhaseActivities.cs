using Contracts.Discovery;
using Contracts.Domain;
using Contracts.Domain.Gates;
using Contracts.Monitor;
using Contracts.Phase;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace PatchMonitor.Activities;

/// <summary>
/// Envoltura <c>[Activity]</c> del resolver de fase, el evaluador de gate y el store de
/// overrides, para que los specs 06 y 08 los invoquen desde workflows. Es cómputo puro en
/// memoria: no hay RPC contra el cluster ni fallo transitorio que esperar.
/// </summary>
public class PhaseActivities
{
    private static readonly PatchPhase[] ValidOverridePhases =
    {
        PatchPhase.Coexistence,
        PatchPhase.Deprecated,
        PatchPhase.Clean,
    };

    private readonly IPhaseResolver _resolver;
    private readonly PhaseEvaluator _evaluator;
    private readonly IPhaseOverrideStore _overrides;

    public PhaseActivities(IPhaseResolver resolver, PhaseEvaluator evaluator, IPhaseOverrideStore overrides)
    {
        _resolver = resolver;
        _evaluator = evaluator;
        _overrides = overrides;
    }

    [Activity]
    public PhaseResolution ResolvePhase(PatchDiscoveryResult result) => _resolver.Resolve(result);

    /// <summary>
    /// Resuelve la fase de <paramref name="result"/> y, solo si es <see cref="PatchPhase.Coexistence"/>
    /// o <see cref="PatchPhase.Deprecated"/>, evalúa el gate de salto. Para <see cref="PatchPhase.Clean"/>
    /// y <see cref="PatchPhase.Unknown"/> devuelve <c>Verdict = null</c>: no hay gate que aplique.
    /// </summary>
    [Activity]
    public PatchAssessment AssessPatch(PatchDiscoveryResult result)
    {
        var resolution = _resolver.Resolve(result);

        PhaseVerdict? verdict = resolution.Phase is PatchPhase.Coexistence or PatchPhase.Deprecated
            ? _evaluator.Evaluate(result.Key, resolution.Phase, result.Executions)
            : null;

        return new PatchAssessment(resolution, verdict);
    }

    [Activity]
    public void SetPhaseOverride(PhaseOverride ov)
    {
        if (Array.IndexOf(ValidOverridePhases, ov.Phase) < 0)
        {
            // Error del operador, no transitorio: reintentar cada 5 minutos no lo arregla.
            throw new ApplicationFailureException(
                $"Fase de override inválida: {ov.Phase}. Debe ser Coexistence, Deprecated o Clean.",
                errorType: "InvalidPhaseOverride",
                nonRetryable: true);
        }

        _overrides.Set(ov);
    }

    [Activity]
    public void ClearPhaseOverride(PatchKey key) => _overrides.Clear(key);
}
