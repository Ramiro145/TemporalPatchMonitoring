namespace Contracts.Domain.Gates;

/// <summary>
/// Gate 2→3 (Deprecación → Código limpio). Bloquean las ejecuciones <b>abiertas</b> que
/// llevan el marker, tanto <see cref="MarkerPresence.Present"/> como
/// <see cref="MarkerPresence.PresentDeprecated"/>: mientras siga viva una ejecución que
/// puede reproducir el <c>Workflow.Patched</c>, no se puede borrar el código del patch.
/// <para>
/// Asimetría con el gate 1→2 (<c>Construction.md</c> §4 restricción #3): acá una ejecución
/// abierta <see cref="MarkerPresence.Absent"/> <b>no</b> bloquea. Las cerradas nunca
/// bloquean. Conjunto vacío y no truncado da <see cref="GateOutcome.Ready"/>;
/// <see cref="ExecutionSnapshotSet.IsTruncated"/> fuerza <see cref="GateOutcome.Inconclusive"/>.
/// </para>
/// </summary>
public sealed class DeprecatedToCleanGate : IPhaseGate
{
    public PatchPhase From => PatchPhase.Deprecated;

    public PatchPhase To => PatchPhase.Clean;

    public PhaseVerdict Evaluate(PatchKey key, ExecutionSnapshotSet executions)
    {
        var evaluatedAt = DateTimeOffset.UtcNow;

        var open = executions.Snapshots.Where(s => s.Status.IsOpen()).ToArray();
        var blocking = open
            .Where(s => s.Marker is MarkerPresence.Present or MarkerPresence.PresentDeprecated)
            .ToArray();

        // Blocked gana sobre Inconclusive.
        if (blocking.Length > 0)
        {
            return PhaseVerdict.Blocked(
                From,
                To,
                blocking.Length,
                blocking.Select(s => s.WorkflowId).ToArray(),
                $"{blocking.Length} ejecución(es) abiertas todavía llevan el marker del patch",
                evaluatedAt);
        }

        var hasUninspectedOpen = open.Any(s => s.Marker == MarkerPresence.Unknown);
        if (hasUninspectedOpen || executions.IsTruncated)
        {
            return PhaseVerdict.Inconclusive(
                From,
                To,
                executions.IsTruncated
                    ? "el descubrimiento se truncó: no se puede afirmar que no queden ejecuciones con el marker"
                    : "hay ejecuciones abiertas con la historia sin inspeccionar",
                evaluatedAt);
        }

        return PhaseVerdict.Ready(
            From,
            To,
            "no quedan ejecuciones abiertas con el marker del patch",
            evaluatedAt);
    }
}
