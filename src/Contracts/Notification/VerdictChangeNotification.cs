using Contracts.Domain;
using Contracts.State;

namespace Contracts.Notification;

/// <summary>
/// Payload de una notificación de cambio de veredicto: acotado a propósito, no el
/// <see cref="PatchState"/> entero (que arrastra todo el <see cref="PatchState.History"/>).
/// <see cref="NotificationId"/> es determinístico (<c>{workflowId}#{revision}</c>) y sirve de
/// segunda red de contención para que el receptor deduplique si igual le llega dos veces por
/// algo fuera del control del monitor.
/// </summary>
public sealed record VerdictChangeNotification(
    string NotificationId,
    PatchKey Key,
    int Revision,
    PatchPhase FromPhase,
    PatchPhase ToPhase,
    GateOutcome? FromOutcome,
    GateOutcome? ToOutcome,
    PatchPhase? NextPhase,
    int BlockingExecutionCount,
    IReadOnlyList<string> BlockingSample,
    string Reason,
    DateTimeOffset ChangedAt)
{
    /// <summary>
    /// Arma el payload a partir del <see cref="PatchState"/> vigente: <c>To*</c> sale de
    /// <see cref="PatchState.Phase"/> y <see cref="PatchState.LastVerdict"/>; <c>From*</c> sale
    /// de la última entrada de <see cref="PatchState.History"/> (o de <see
    /// cref="PatchPhase.Unknown"/> sin outcome si todavía no hay ninguna, caso del alta 0→1).
    /// </summary>
    public static VerdictChangeNotification FromState(PatchState state)
    {
        var last = state.History.Count > 0 ? state.History[^1] : null;

        return new VerdictChangeNotification(
            $"{state.Key.ToWorkflowId()}#{state.Revision}",
            state.Key,
            state.Revision,
            last?.FromPhase ?? PatchPhase.Unknown,
            state.Phase,
            last?.FromOutcome,
            state.LastVerdict?.Outcome,
            state.LastVerdict?.NextPhase,
            state.LastVerdict?.BlockingExecutionCount ?? 0,
            state.LastVerdict?.BlockingSample ?? Array.Empty<string>(),
            state.PhaseReason,
            last?.At ?? state.LastChangedAt ?? default);
    }
}
