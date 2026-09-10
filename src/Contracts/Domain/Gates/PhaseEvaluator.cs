namespace Contracts.Domain.Gates;

/// <summary>
/// Elige el <see cref="IPhaseGate"/> que corresponde a la fase actual de un patch y le
/// delega la evaluación. Resuelve por su cuenta los dos casos que no tienen gate:
/// <see cref="PatchPhase.Unknown"/> (fase sin resolver) y <see cref="PatchPhase.Clean"/>
/// (fase final).
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
    /// No hay ningún <see cref="IPhaseGate"/> registrado para <paramref name="currentPhase"/>
    /// (error de cableado de DI, no de datos).
    /// </exception>
    public PhaseVerdict Evaluate(PatchKey key, PatchPhase currentPhase, ExecutionSnapshotSet executions)
    {
        var evaluatedAt = DateTimeOffset.UtcNow;

        switch (currentPhase)
        {
            case PatchPhase.Unknown:
                return PhaseVerdict.Inconclusive(
                    currentPhase,
                    nextPhase: null,
                    "fase actual no resuelta",
                    evaluatedAt);

            case PatchPhase.Clean:
                return PhaseVerdict.Blocked(
                    currentPhase,
                    nextPhase: null,
                    blockingExecutionCount: 0,
                    Array.Empty<string>(),
                    "fase final: no hay transición siguiente",
                    evaluatedAt);

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
