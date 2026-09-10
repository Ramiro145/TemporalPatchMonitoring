using Contracts.Domain;
using Contracts.Domain.Gates;
using Xunit;
using static PatchMonitor.Tests.Domain.ExecutionSnapshotBuilder;

namespace PatchMonitor.Tests.Domain;

public class CoexistenceToDeprecatedGateTests
{
    private static readonly PatchKey Key = new("default", "OrderWorkflow", "core-patch");

    private readonly CoexistenceToDeprecatedGate _gate = new();

    [Fact]
    public void From_y_To_son_fase_1_y_fase_2()
    {
        Assert.Equal(PatchPhase.Coexistence, _gate.From);
        Assert.Equal(PatchPhase.Deprecated, _gate.To);
    }

    [Fact]
    public void Conjunto_vacio_no_truncado_da_Ready()
    {
        var verdict = _gate.Evaluate(Key, ExecutionSnapshotSet.Empty);

        Assert.Equal(GateOutcome.Ready, verdict.Outcome);
        Assert.Equal(PatchPhase.Deprecated, verdict.NextPhase);
        Assert.Equal(0, verdict.BlockingExecutionCount);
    }

    [Fact]
    public void Todas_las_abiertas_con_marker_dan_Ready()
    {
        var set = Set(
            Open().WithMarker(),
            Open().WithDeprecatedMarker());

        Assert.Equal(GateOutcome.Ready, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Una_abierta_sin_marker_bloquea()
    {
        var set = Set(
            Open().WithMarker(),
            Open().WithoutMarker().WithWorkflowId("pre-patch-1"));

        var verdict = _gate.Evaluate(Key, set);

        Assert.Equal(GateOutcome.Blocked, verdict.Outcome);
        Assert.Equal(1, verdict.BlockingExecutionCount);
        Assert.Equal(new[] { "pre-patch-1" }, verdict.BlockingSample);
    }

    [Fact]
    public void Una_cerrada_sin_marker_no_bloquea()
    {
        var set = Set(
            Open().WithMarker(),
            Closed(ExecutionStatus.Completed).WithoutMarker());

        Assert.Equal(GateOutcome.Ready, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Una_abierta_sin_inspeccionar_da_Inconclusive()
    {
        var set = Set(
            Open().WithMarker(),
            Open().Uninspected());

        Assert.Equal(GateOutcome.Inconclusive, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Conjunto_truncado_sin_bloqueantes_da_Inconclusive()
    {
        var set = TruncatedSet(Open().WithMarker());

        Assert.Equal(GateOutcome.Inconclusive, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Bloqueante_y_sin_inspeccionar_juntos_dan_Blocked()
    {
        var set = Set(
            Open().WithoutMarker(),
            Open().Uninspected());

        Assert.Equal(GateOutcome.Blocked, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Bloqueante_y_truncado_juntos_dan_Blocked()
    {
        var set = TruncatedSet(Open().WithoutMarker());

        Assert.Equal(GateOutcome.Blocked, _gate.Evaluate(Key, set).Outcome);
    }

    [Fact]
    public void Con_nueve_bloqueantes_el_conteo_es_nueve_y_la_muestra_cinco()
    {
        var builders = Enumerable.Range(0, 9)
            .Select(i => Open().WithoutMarker().WithWorkflowId($"pre-{i}"))
            .ToArray();

        var verdict = _gate.Evaluate(Key, Set(builders));

        Assert.Equal(GateOutcome.Blocked, verdict.Outcome);
        Assert.Equal(9, verdict.BlockingExecutionCount);
        Assert.Equal(5, verdict.BlockingSample.Count);
    }
}
