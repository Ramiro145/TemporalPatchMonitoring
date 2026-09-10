using Contracts.Discovery;
using Contracts.Domain;
using PatchMonitor.Tests.Domain;

namespace PatchMonitor.Tests.Phase;

/// <summary>
/// Arma <see cref="PatchDiscoveryResult"/> para los tests de <c>PhaseResolver</c>, de forma que
/// se lean como la tabla de decisión del spec 04. Reusa <see cref="ExecutionSnapshotBuilder"/>
/// del spec 02 para las ejecuciones sueltas y agrega el envoltorio con la <see cref="PatchKey"/>.
/// </summary>
public sealed class SnapshotSetBuilder
{
    /// <summary>Instante de referencia fijo para los <c>StartTime</c> de los fixtures.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Patch de referencia; su identidad no influye en la inferencia de fase.</summary>
    public static readonly PatchKey Key = new("default", "OrderWorkflow", "core-patch");

    private readonly List<ExecutionSnapshot> _snaps = new();
    private bool _truncated;

    public static SnapshotSetBuilder New() => new();

    public SnapshotSetBuilder With(params ExecutionSnapshotBuilder[] executions)
    {
        _snaps.AddRange(executions.Select(e => e.Build()));
        return this;
    }

    /// <summary>Marca el conjunto como truncado (<see cref="ExecutionSnapshotSet.IsTruncated"/>).</summary>
    public SnapshotSetBuilder Truncated()
    {
        _truncated = true;
        return this;
    }

    public PatchDiscoveryResult Build() =>
        new(Key, new ExecutionSnapshotSet(_snaps.ToArray(), _truncated));

    /// <summary>Atajo: un <see cref="PatchDiscoveryResult"/> no truncado con estas ejecuciones.</summary>
    public static PatchDiscoveryResult Of(params ExecutionSnapshotBuilder[] executions) =>
        New().With(executions).Build();

    /// <summary>Atajo: un <see cref="PatchDiscoveryResult"/> sin ninguna ejecución.</summary>
    public static PatchDiscoveryResult Empty() =>
        new(Key, ExecutionSnapshotSet.Empty);
}
