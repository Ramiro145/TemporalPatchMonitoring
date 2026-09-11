using Contracts.Api;
using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Xunit;

namespace PatchMonitor.Tests.Api;

/// <summary>
/// <see cref="PatchSummaryResponse.FromState"/>, <see cref="PatchSummaryResponse.Unreadable"/> y
/// <see cref="PatchDetailResponse.FromState"/>: los DTOs de salida de la API de control
/// (spec 08) sobre un <see cref="PatchState"/> construido a mano, sin Temporal.
/// </summary>
public class PatchResponsesTests
{
    private static readonly PatchKey KeyA = new("default", "OrderWorkflow", "order-v2");
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static PatchState BuildState(bool withOverride)
    {
        var verdict = new PhaseVerdict(
            GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated,
            3, new[] { "wf-1", "wf-2" }, "bloqueado", T0);
        var previousVerdict = new PhaseVerdict(
            GateOutcome.Inconclusive, PatchPhase.Coexistence, PatchPhase.Deprecated,
            0, Array.Empty<string>(), "inconcluso", T0.AddMinutes(-5));
        var history = new[]
        {
            new PatchStateChange(
                T0, PatchPhase.Unknown, PatchPhase.Coexistence,
                null, GateOutcome.Blocked, "primer assessment"),
        };

        return new PatchState(
            KeyA,
            PatchPhase.Coexistence,
            PhaseSource.Inferred,
            "inferida de los markers",
            LastVerdict: verdict,
            PreviousVerdict: previousVerdict,
            LastObservedAt: T0,
            LastChangedAt: T0.AddMinutes(-10),
            Override: withOverride
                ? new PhaseOverride(KeyA, PatchPhase.Deprecated, "operador", T0, null)
                : null,
            AssessmentCount: 4,
            Revision: 2,
            NotifiedRevision: 1,
            History: history);
    }

    [Fact]
    public void FromState_mapea_outcome_next_phase_y_has_override_desde_el_ultimo_veredicto()
    {
        var summary = PatchSummaryResponse.FromState(BuildState(withOverride: true));

        Assert.Equal("default", summary.Namespace);
        Assert.Equal("OrderWorkflow", summary.WorkflowType);
        Assert.Equal("order-v2", summary.PatchId);
        Assert.Equal(PatchPhase.Coexistence, summary.Phase);
        Assert.Equal(PhaseSource.Inferred, summary.Source);
        Assert.Equal(GateOutcome.Blocked, summary.Outcome);
        Assert.Equal(PatchPhase.Deprecated, summary.NextPhase);
        Assert.Equal(3, summary.BlockingExecutionCount);
        Assert.True(summary.HasOverride);
        Assert.Equal(2, summary.Revision);
    }

    [Fact]
    public void FromState_sin_override_deja_HasOverride_en_false()
    {
        var summary = PatchSummaryResponse.FromState(BuildState(withOverride: false));

        Assert.False(summary.HasOverride);
    }

    [Fact]
    public void FromState_sin_veredicto_deja_outcome_y_next_phase_en_null()
    {
        var state = PatchState.Initial(KeyA);

        var summary = PatchSummaryResponse.FromState(state);

        Assert.Null(summary.Outcome);
        Assert.Null(summary.NextPhase);
        Assert.Equal(0, summary.BlockingExecutionCount);
    }

    [Fact]
    public void Unreadable_deja_Phase_Unknown_conservando_la_key()
    {
        var summary = PatchSummaryResponse.Unreadable(KeyA, "el entity no responde");

        Assert.Equal("default", summary.Namespace);
        Assert.Equal("OrderWorkflow", summary.WorkflowType);
        Assert.Equal("order-v2", summary.PatchId);
        Assert.Equal(PatchPhase.Unknown, summary.Phase);
        Assert.False(summary.HasOverride);
        Assert.Equal(0, summary.Revision);
    }

    [Fact]
    public void PatchDetailResponse_FromState_expone_history_override_y_notified_revision()
    {
        var state = BuildState(withOverride: true);

        var detail = PatchDetailResponse.FromState(state);

        Assert.Equal("inferida de los markers", detail.PhaseReason);
        Assert.Equal(state.LastVerdict, detail.LastVerdict);
        Assert.Equal(state.PreviousVerdict, detail.PreviousVerdict);
        Assert.Equal(state.Override, detail.Override);
        Assert.Equal(4, detail.AssessmentCount);
        Assert.Equal(1, detail.NotifiedRevision);
        Assert.Single(detail.History);
    }
}
