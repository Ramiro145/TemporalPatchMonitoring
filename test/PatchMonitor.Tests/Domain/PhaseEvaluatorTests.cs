using Contracts.Domain;
using Contracts.Domain.Gates;
using Xunit;
using static PatchMonitor.Tests.Domain.ExecutionSnapshotBuilder;

namespace PatchMonitor.Tests.Domain;

public class PhaseEvaluatorTests
{
    private static readonly PatchKey Key = new("default", "OrderWorkflow", "core-patch");

    private readonly PhaseEvaluator _evaluator = new(new IPhaseGate[]
    {
        new CoexistenceToDeprecatedGate(),
        new DeprecatedToCleanGate(),
    });

    [Theory]
    [InlineData(PatchPhase.Unknown)]
    [InlineData(PatchPhase.Clean)]
    public void Fases_sin_transicion_lanzan_InvalidOperationException(PatchPhase phase)
    {
        Assert.Throws<InvalidOperationException>(() =>
            _evaluator.Evaluate(Key, phase, ExecutionSnapshotSet.Empty));
    }

    [Fact]
    public void Fase_Coexistence_delega_en_el_gate_1_a_2()
    {
        var set = Set(Open().WithoutMarker());

        var verdict = _evaluator.Evaluate(Key, PatchPhase.Coexistence, set);

        Assert.Equal(GateOutcome.Blocked, verdict.Outcome);
        Assert.Equal(PatchPhase.Coexistence, verdict.CurrentPhase);
        Assert.Equal(PatchPhase.Deprecated, verdict.NextPhase);
    }

    [Fact]
    public void Fase_Deprecated_delega_en_el_gate_2_a_3()
    {
        var set = Set(Open().WithDeprecatedMarker());

        var verdict = _evaluator.Evaluate(Key, PatchPhase.Deprecated, set);

        Assert.Equal(GateOutcome.Blocked, verdict.Outcome);
        Assert.Equal(PatchPhase.Deprecated, verdict.CurrentPhase);
        Assert.Equal(PatchPhase.Clean, verdict.NextPhase);
    }

    [Fact]
    public void Sin_gate_para_la_fase_actual_lanza_InvalidOperationException()
    {
        var evaluatorSinGates = new PhaseEvaluator(Array.Empty<IPhaseGate>());

        Assert.Throws<InvalidOperationException>(() =>
            evaluatorSinGates.Evaluate(Key, PatchPhase.Coexistence, ExecutionSnapshotSet.Empty));
    }
}
