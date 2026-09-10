namespace Contracts.Domain.Gates;

/// <summary>
/// Criterio de salto de una fase concreta a la siguiente. Cada implementación es dueña de
/// su predicado completo (incluido el filtro por estado de la ejecución); la asimetría
/// entre el gate 1→2 y el 2→3 (<c>Construction.md</c> §4 restricción #3) vive en archivos
/// separados, no en un parámetro.
/// </summary>
public interface IPhaseGate
{
    /// <summary>Fase desde la que este gate evalúa el salto.</summary>
    PatchPhase From { get; }

    /// <summary>Fase a la que se salta si el gate da <see cref="GateOutcome.Ready"/>.</summary>
    PatchPhase To { get; }

    /// <summary>
    /// Evalúa si el patch <paramref name="key"/> puede pasar de <see cref="From"/> a
    /// <see cref="To"/> dado el conjunto de ejecuciones observadas.
    /// </summary>
    PhaseVerdict Evaluate(PatchKey key, ExecutionSnapshotSet executions);
}
