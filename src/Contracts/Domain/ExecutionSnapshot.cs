namespace Contracts.Domain;

/// <summary>
/// Foto de una ejecución de workflow relevante para un patch, en el momento en que el
/// descubrimiento (spec 03) la inspeccionó. <see cref="Marker"/> ya llega resuelto:
/// quién lee el flag <c>deprecated</c> de la Event History es problema del spec 04.
/// </summary>
public sealed record ExecutionSnapshot(
    string WorkflowId,
    string RunId,
    string WorkflowType,
    ExecutionStatus Status,
    MarkerPresence Marker,
    DateTimeOffset StartTime);

/// <summary>
/// Conjunto de <see cref="ExecutionSnapshot"/> que un gate evalúa de una sola vez.
/// <see cref="IsTruncated"/> viaja con los datos —no como parámetro suelto— y vale
/// <c>true</c> cuando el descubrimiento topeó el número de ejecuciones inspeccionadas o
/// falló: en ese caso el gate nunca puede devolver <see cref="GateOutcome.Ready"/>.
/// </summary>
public sealed record ExecutionSnapshotSet(
    IReadOnlyList<ExecutionSnapshot> Snapshots,
    bool IsTruncated)
{
    /// <summary>Conjunto vacío y completo (sin truncar).</summary>
    public static ExecutionSnapshotSet Empty { get; } =
        new(Array.Empty<ExecutionSnapshot>(), IsTruncated: false);
}
