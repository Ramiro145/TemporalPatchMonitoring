namespace Contracts.Domain.Gates;

/// <summary>
/// Gate 1→2 (Convivencia → Deprecación). Bloquean las ejecuciones <b>abiertas</b> con
/// <see cref="MarkerPresence.Absent"/>: son pre-patch y todavía corren el código viejo,
/// así que el patch no puede deprecarse. Las ejecuciones cerradas nunca bloquean.
/// <para>
/// Un conjunto vacío y no truncado da <see cref="GateOutcome.Ready"/>: no hay nada que
/// bloquee. El caso "el descubrimiento falló y devolvió nada" lo cubre
/// <see cref="ExecutionSnapshotSet.IsTruncated"/> = <c>true</c>, que fuerza
/// <see cref="GateOutcome.Inconclusive"/>.
/// </para>
/// </summary>
public sealed class CoexistenceToDeprecatedGate : IPhaseGate
{
    public PatchPhase From => PatchPhase.Coexistence;

    public PatchPhase To => PatchPhase.Deprecated;

    public PhaseVerdict Evaluate(PatchKey key, ExecutionSnapshotSet executions)
    {
        var evaluatedAt = DateTimeOffset.UtcNow;

        var open = executions.Snapshots.Where(s => s.Status.IsOpen()).ToArray();
        var blocking = open.Where(s => s.Marker == MarkerPresence.Absent).ToArray();

        // Blocked gana sobre Inconclusive: un bloqueante conocido ya responde la pregunta.
        if (blocking.Length > 0)
        {
            return PhaseVerdict.Blocked(
                From,
                To,
                blocking.Length,
                blocking.Select(s => s.WorkflowId).ToArray(),
                $"{blocking.Length} ejecución(es) pre-patch abiertas sin el marker",
                evaluatedAt);
        }

        var hasUninspectedOpen = open.Any(s => s.Marker == MarkerPresence.Unknown);
        if (hasUninspectedOpen || executions.IsTruncated)
        {
            return PhaseVerdict.Inconclusive(
                From,
                To,
                executions.IsTruncated
                    ? "el descubrimiento se truncó: no se puede afirmar que no queden ejecuciones pre-patch"
                    : "hay ejecuciones abiertas con la historia sin inspeccionar",
                evaluatedAt);
        }

        return PhaseVerdict.Ready(
            From,
            To,
            "no quedan ejecuciones pre-patch abiertas",
            evaluatedAt);
    }
}
