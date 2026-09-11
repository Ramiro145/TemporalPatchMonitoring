using Contracts.Domain;
using Contracts.Notification;
using Contracts.Phase;
using Contracts.State;
using Xunit;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="VerdictChangeNotification.FromState"/> arma el payload leyendo <c>To*</c> de
/// <see cref="PatchState.Phase"/>/<see cref="PatchState.LastVerdict"/> y <c>From*</c> de la
/// última entrada de <see cref="PatchState.History"/>.
/// </summary>
public class VerdictChangeNotificationTests
{
    private static readonly PatchKey Key = new("default", "OrderWorkflow", "order-v2");
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(5);

    private static PhaseVerdict Verdict(GateOutcome outcome, PatchPhase current, PatchPhase? next, DateTimeOffset at) =>
        new(outcome, current, next, 2, new[] { "wf-1", "wf-2" }, $"{outcome}", at);

    private static PatchStateChange Change(
        DateTimeOffset at, PatchPhase from, PatchPhase to, GateOutcome? fromOutcome, GateOutcome? toOutcome, string reason) =>
        new(at, from, to, fromOutcome, toOutcome, reason);

    [Fact]
    public void FromState_con_history_no_vacia_arma_from_desde_la_ultima_entrada()
    {
        var verdict = Verdict(GateOutcome.Ready, PatchPhase.Coexistence, PatchPhase.Deprecated, T1);
        var state = new PatchState(
            Key,
            PatchPhase.Deprecated,
            PhaseSource.Inferred,
            "Salto a Deprecated",
            LastVerdict: verdict,
            PreviousVerdict: null,
            LastObservedAt: T1,
            LastChangedAt: T1,
            Override: null,
            AssessmentCount: 2,
            Revision: 2,
            NotifiedRevision: 0,
            History: new[]
            {
                Change(T0, PatchPhase.Unknown, PatchPhase.Coexistence, null, GateOutcome.Blocked, "Alta"),
                Change(T1, PatchPhase.Coexistence, PatchPhase.Deprecated, GateOutcome.Blocked, GateOutcome.Ready, "Salto a Deprecated"),
            });

        var notification = VerdictChangeNotification.FromState(state);

        Assert.Equal(PatchPhase.Coexistence, notification.FromPhase);
        Assert.Equal(GateOutcome.Blocked, notification.FromOutcome);
        Assert.Equal(PatchPhase.Deprecated, notification.ToPhase);
        Assert.Equal(GateOutcome.Ready, notification.ToOutcome);
        Assert.Equal(PatchPhase.Deprecated, notification.NextPhase);
        Assert.Equal(2, notification.BlockingExecutionCount);
        Assert.Equal(new[] { "wf-1", "wf-2" }, notification.BlockingSample);
        Assert.Equal("Salto a Deprecated", notification.Reason);
        Assert.Equal(T1, notification.ChangedAt);
        Assert.Equal(2, notification.Revision);
        Assert.Equal(Key, notification.Key);
    }

    [Fact]
    public void FromState_con_history_vacia_usa_unknown_sin_outcome_como_from()
    {
        var verdict = Verdict(GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated, T0);
        var state = PatchState.Initial(Key) with
        {
            Phase = PatchPhase.Coexistence,
            LastVerdict = verdict,
            LastChangedAt = T0,
            AssessmentCount = 1,
            Revision = 1,
        };

        var notification = VerdictChangeNotification.FromState(state);

        Assert.Equal(PatchPhase.Unknown, notification.FromPhase);
        Assert.Null(notification.FromOutcome);
        Assert.Equal(PatchPhase.Coexistence, notification.ToPhase);
        Assert.Equal(GateOutcome.Blocked, notification.ToOutcome);
        Assert.Equal(T0, notification.ChangedAt);
    }

    [Fact]
    public void NotificationId_es_determinístico_y_estable_entre_llamadas()
    {
        var verdict = Verdict(GateOutcome.Ready, PatchPhase.Coexistence, PatchPhase.Deprecated, T0);
        var state = PatchState.Initial(Key) with
        {
            Phase = PatchPhase.Coexistence,
            LastVerdict = verdict,
            LastChangedAt = T0,
            Revision = 1,
        };

        var first = VerdictChangeNotification.FromState(state);
        var second = VerdictChangeNotification.FromState(state);

        var expectedId = $"{Key.ToWorkflowId()}#1";
        Assert.Equal(expectedId, first.NotificationId);
        Assert.Equal(first.NotificationId, second.NotificationId);
    }
}
