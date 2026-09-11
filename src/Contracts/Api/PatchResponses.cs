using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;

namespace Contracts.Api;

/// <summary>
/// Resumen de un patch para <c>GET /patches</c>: no lleva <see cref="PatchState.History"/> —un
/// listado de <see cref="ApiOptions.MaxListPatches"/> patches no puede arrastrar cada uno su
/// ring buffer.
/// </summary>
public sealed record PatchSummaryResponse(
    string Namespace, string WorkflowType, string PatchId,
    PatchPhase Phase, PhaseSource Source, GateOutcome? Outcome, PatchPhase? NextPhase,
    int BlockingExecutionCount, bool HasOverride, int Revision,
    DateTimeOffset? LastObservedAt, DateTimeOffset? LastChangedAt)
{
    public static PatchSummaryResponse FromState(PatchState state) =>
        new(
            state.Key.Namespace,
            state.Key.WorkflowType,
            state.Key.PatchId,
            state.Phase,
            state.Source,
            state.LastVerdict?.Outcome,
            state.LastVerdict?.NextPhase,
            state.LastVerdict?.BlockingExecutionCount ?? 0,
            state.Override is not null,
            state.Revision,
            state.LastObservedAt,
            state.LastChangedAt);

    /// <summary>
    /// La key está en el registry pero su entity no responde: entra al listado con
    /// <see cref="PatchPhase.Unknown"/> en vez de tirar abajo el request entero.
    /// </summary>
    public static PatchSummaryResponse Unreadable(PatchKey key, string reason) =>
        new(
            key.Namespace,
            key.WorkflowType,
            key.PatchId,
            PatchPhase.Unknown,
            PhaseSource.Inferred,
            Outcome: null,
            NextPhase: null,
            BlockingExecutionCount: 0,
            HasOverride: false,
            Revision: 0,
            LastObservedAt: null,
            LastChangedAt: null);
}

/// <summary>Estado durable completo de un patch para <c>GET /patches/{ns}/{type}/{patchId}</c>.</summary>
public sealed record PatchDetailResponse(
    PatchSummaryResponse Summary, string PhaseReason,
    PhaseVerdict? LastVerdict, PhaseVerdict? PreviousVerdict,
    PhaseOverride? Override, int AssessmentCount, int NotifiedRevision,
    IReadOnlyList<PatchStateChange> History)
{
    public static PatchDetailResponse FromState(PatchState state) =>
        new(
            PatchSummaryResponse.FromState(state),
            state.PhaseReason,
            state.LastVerdict,
            state.PreviousVerdict,
            state.Override,
            state.AssessmentCount,
            state.NotifiedRevision,
            state.History);
}

/// <summary>Respuesta de <c>GET /patches</c>: acotada por <see cref="ApiOptions.MaxListPatches"/>.</summary>
public sealed record PatchListResponse(
    int Count, bool Truncated, IReadOnlyList<PatchSummaryResponse> Patches);
