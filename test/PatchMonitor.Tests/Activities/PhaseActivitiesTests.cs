using Contracts.Domain;
using Contracts.Domain.Gates;
using Contracts.Phase;
using Contracts.State;
using PatchMonitor.Activities;
using PatchMonitor.Services;
using Xunit;
using static PatchMonitor.Tests.Domain.ExecutionSnapshotBuilder;
using static PatchMonitor.Tests.Phase.SnapshotSetBuilder;

namespace PatchMonitor.Tests.Activities;

/// <summary>
/// <see cref="PhaseActivities.AssessPatch"/> combina <c>PhaseResolver</c> (spec 04) y
/// <c>PhaseEvaluator</c> (spec 02) en un solo round-trip. Reusa <see cref="SnapshotSetBuilder"/>
/// y <see cref="ExecutionSnapshotBuilder"/> de los tests de esos specs.
/// </summary>
public class PhaseActivitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static PhaseActivities Activities() => new(
        new PhaseResolver(
            new InMemoryPhaseOverrideStore(),
            new PhaseOptions(TimeSpan.FromHours(24)),
            new PatchMonitor.Tests.Phase.FakeTimeProvider(Now)),
        new PhaseEvaluator(new IPhaseGate[]
        {
            new CoexistenceToDeprecatedGate(),
            new DeprecatedToCleanGate(),
        }),
        new InMemoryPhaseOverrideStore());

    [Fact]
    public void Fase_Unknown_da_Verdict_null()
    {
        var assessment = Activities().AssessPatch(Empty());

        Assert.Equal(PatchPhase.Unknown, assessment.Resolution.Phase);
        Assert.Null(assessment.Verdict);
    }

    [Fact]
    public void Fase_Clean_da_Verdict_null()
    {
        var assessment = Activities().AssessPatch(Of(
            Closed().WithDeprecatedMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(25))));

        Assert.Equal(PatchPhase.Clean, assessment.Resolution.Phase);
        Assert.Null(assessment.Verdict);
    }

    [Fact]
    public void Coexistence_con_ejecucion_pre_patch_abierta_da_Verdict_Blocked()
    {
        var assessment = Activities().AssessPatch(Of(
            Open().WithoutMarker().StartedAt(T0),
            Open().WithMarker().StartedAt(T0.AddDays(1))));

        Assert.Equal(PatchPhase.Coexistence, assessment.Resolution.Phase);
        Assert.NotNull(assessment.Verdict);
        Assert.Equal(GateOutcome.Blocked, assessment.Verdict!.Outcome);
    }

    [Fact]
    public void Coexistence_con_las_mismas_ejecuciones_drenadas_da_Verdict_Ready()
    {
        var assessment = Activities().AssessPatch(Of(
            Closed().WithoutMarker().StartedAt(T0),
            Open().WithMarker().StartedAt(T0.AddDays(1))));

        Assert.Equal(PatchPhase.Coexistence, assessment.Resolution.Phase);
        Assert.NotNull(assessment.Verdict);
        Assert.Equal(GateOutcome.Ready, assessment.Verdict!.Outcome);
    }

    [Fact]
    public void Con_previous_Clean_y_sin_markers_nuevos_se_conserva_Clean_con_Verdict_null()
    {
        // Sin previous esto es Deprecated (marker deprecado reciente sin ausencias posteriores).
        var executions = Of(Closed().WithDeprecatedMarker().StartedAt(T0));
        var previous = PatchState.Initial(Key) with
        {
            Phase = PatchPhase.Clean,
            Source = PhaseSource.Inferred,
            LastChangedAt = T0.AddDays(5),
        };

        var without = Activities().AssessPatch(executions);
        var with = Activities().AssessPatch(executions, previous);

        Assert.Equal(PatchPhase.Deprecated, without.Resolution.Phase);
        Assert.Equal(PatchPhase.Clean, with.Resolution.Phase);
        Assert.Null(with.Verdict);
    }
}
