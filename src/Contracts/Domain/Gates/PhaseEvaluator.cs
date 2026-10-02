namespace Contracts.Domain.Gates;

/// <summary>
/// Elige el <see cref="IPhaseGate"/> que corresponde a la fase actual de un patch y le
/// delega la evaluación. Solo existe para las fases de transición
/// (<see cref="PatchPhase.Coexistence"/> y <see cref="PatchPhase.Deprecated"/>):
/// <see cref="PatchPhase.Unknown"/> y <see cref="PatchPhase.Clean"/> no tienen veredicto
/// (<c>Verdict = null</c> en <c>PhaseActivities.AssessPatch</c>) y lanzan si se las pasa.
/// </summary>
public sealed class PhaseEvaluator
{
    private readonly IReadOnlyDictionary<PatchPhase, IPhaseGate> _gatesByFrom;

    public PhaseEvaluator(IEnumerable<IPhaseGate> gates)
    {
        _gatesByFrom = gates.ToDictionary(g => g.From);
    }

    /// <summary>
    /// Evalúa si el patch <paramref name="key"/>, actualmente en <paramref name="currentPhase"/>,
    /// puede avanzar a la fase siguiente.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="currentPhase"/> es <see cref="PatchPhase.Unknown"/> o
    /// <see cref="PatchPhase.Clean"/>, o no hay ningún <see cref="IPhaseGate"/> registrado para
    /// ella (error de cableado de DI, no de datos).
    /// </exception>
    public PhaseVerdict Evaluate(PatchKey key, PatchPhase currentPhase, ExecutionSnapshotSet executions)
    {
        var evaluatedAt = DateTimeOffset.UtcNow;

        switch (currentPhase)
        {
            case PatchPhase.Unknown:
            case PatchPhase.Clean:
                throw new InvalidOperationException(
                    $"La fase {currentPhase} no tiene transición siguiente: no se evalúa (veredicto null).");

            default:
                if (!_gatesByFrom.TryGetValue(currentPhase, out var gate))
                {
                    throw new InvalidOperationException(
                        $"No hay un IPhaseGate registrado para la fase {currentPhase}.");
                }

                return gate.Evaluate(key, executions);
        }
    }
}
