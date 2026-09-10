using Contracts.Domain;
using Contracts.Domain.Gates;
using Xunit;
using static PatchMonitor.Tests.Domain.ExecutionSnapshotBuilder;

namespace PatchMonitor.Tests.Domain;

public class DeprecatedToCleanGateTests
{
    private static readonly PatchKey Key = new("default", "OrderWorkflow", "core-patch");

    private readonly DeprecatedToCleanGate _gate = new();

    [Fact]
    public void From_y_To_son_fase_2_y_fase_3()
    {
        Assert.Equal(PatchPhase.Deprecated, _gate.From);
        Assert.Equal(PatchPhase.Clean, _gate.To);
    }

    [Fact]
    public void Conjunto_vacio_no_truncado_da_Ready()
    {
        var verdict = _gate.Evaluate(Key, ExecutionSnapshotSet.Empty);

        Assert.Equal(GateOutcome.Ready, verdict.Outcome);
        Assert.Equal(PatchPhase.Clean, verdict.NextPhase);
    }

    [Fact]
    public void Una_abierta_con_marker_sin_deprecated_bloquea()
    {
        var set = Set(Open().WithMarker().WithWorkflowId("con-marker-1"));

        var verdict = _gate.Evaluate(Key, set);

        Assert.Equal(GateOutcome.Blocked, verdict.Outcome);
        Assert.Equal(1, verdict.BlockingExecutionCount);
        Assert.Equal(new[] { "con-marker-1" }, verdict.BlockingSample);
    }

    [Fact]
    public void Una_abierta_con_marker_deprecated_bloquea()
    {
        var set = Set(Open().WithDeprecatedMarker());

        Assert.Equal(GateOutcome.Blocked, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Una_abierta_sin_marker_no_bloquea_el_paso_a_clean()
    {
        // Asimetría con el gate 1→2 (Construction.md §4 restricción #3):
        // "abierta sin marker" bloquea 1→2 pero NO bloquea 2→3.
        var set = Set(Open().WithoutMarker());

        Assert.Equal(GateOutcome.Ready, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Cerradas_con_marker_no_bloquean()
    {
        var set = Set(
            Closed(ExecutionStatus.Completed).WithMarker(),
            Closed(ExecutionStatus.Terminated).WithDeprecatedMarker());

        Assert.Equal(GateOutcome.Ready, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Una_abierta_sin_inspeccionar_da_Inconclusive()
    {
        var set = Set(Open().Uninspected());

        Assert.Equal(GateOutcome.Inconclusive, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Conjunto_truncado_sin_bloqueantes_da_Inconclusive()
    {
        var set = TruncatedSet(Open().WithoutMarker());

        Assert.Equal(GateOutcome.Inconclusive, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Bloqueante_y_sin_inspeccionar_juntos_dan_Blocked()
    {
        var set = Set(
            Open().WithDeprecatedMarker(),
            Open().Uninspected());

        Assert.Equal(GateOutcome.Blocked, _gate.Evaluate(Key, set).Outcome);
    }
}
