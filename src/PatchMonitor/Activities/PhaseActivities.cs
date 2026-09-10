using Contracts.Discovery;
using Contracts.Domain;
using Contracts.Phase;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace PatchMonitor.Activities;

/// <summary>
/// Envoltura <c>[Activity]</c> del resolver de fase y del store de overrides, para que los
/// specs 06 y 08 los invoquen desde workflows. Es cómputo puro en memoria: no hay RPC contra el
/// cluster ni fallo transitorio que esperar.
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
    private readonly IPhaseOverrideStore _overrides;

    public PhaseActivities(IPhaseResolver resolver, IPhaseOverrideStore overrides)
    {
        _resolver = resolver;
        _overrides = overrides;
    }

    [Activity]
    public PhaseResolution ResolvePhase(PatchDiscoveryResult result) => _resolver.Resolve(result);

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
