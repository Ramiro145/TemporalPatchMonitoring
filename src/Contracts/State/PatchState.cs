using Contracts.Domain;
using Contracts.Phase;

namespace Contracts.State;

/// <summary>
/// Estado durable de un patch, tal como lo persiste su entity workflow (spec 05). Guarda el
/// veredicto vigente y el anterior más un <see cref="Revision"/> monotónico que solo avanza
/// cuando hay un cambio real: es la señal que el spec 07 usa para notificar exactamente una
/// vez. <see cref="History"/> es un ring buffer acotado de transiciones que le da a la API
/// del spec 08 "cómo evolucionó este patch" sin ningún store extra.
/// </summary>
public sealed record PatchState(
    PatchKey Key,
    PatchPhase Phase,
    PhaseSource Source,
    string PhaseReason,
    PhaseVerdict? LastVerdict,
    PhaseVerdict? PreviousVerdict,
    DateTimeOffset? LastObservedAt,
    DateTimeOffset? LastChangedAt,
    PhaseOverride? Override,
    int AssessmentCount,
    int Revision,
    int NotifiedRevision,
    IReadOnlyList<PatchStateChange> History)
{
    /// <summary>
    /// Estado inicial de un entity recién creado: fase <see cref="PatchPhase.Unknown"/>,
    /// sin veredictos, sin override, <see cref="Revision"/>, <see cref="NotifiedRevision"/> y
    /// <see cref="AssessmentCount"/> en 0 e <see cref="History"/> vacía.
    /// </summary>
    public static PatchState Initial(PatchKey key) =>
        new(
            key,
            PatchPhase.Unknown,
            PhaseSource.Inferred,
            "Sin assessments todavía.",
            LastVerdict: null,
            PreviousVerdict: null,
            LastObservedAt: null,
            LastChangedAt: null,
            Override: null,
            AssessmentCount: 0,
            Revision: 0,
            NotifiedRevision: 0,
            History: Array.Empty<PatchStateChange>());

    /// <summary>
    /// Copia para arrancar la próxima ejecución tras un <c>Continue-As-New</c>:
    /// <see cref="AssessmentCount"/> vuelve a 0 e <see cref="History"/> se recorta a las
    /// últimas <paramref name="historyLimit"/> entradas. Todo lo demás —fase, fuente,
    /// veredictos, override, <see cref="Revision"/> y <see cref="NotifiedRevision"/>— sobrevive
    /// intacto: una notificación ya reclamada no puede repetirse tras el salto.
    /// </summary>
    public PatchState ForCarryover(int historyLimit)
    {
        var trimmed = History.Count <= historyLimit
            ? History
            : History.Skip(History.Count - historyLimit).ToArray();

        return this with
        {
            AssessmentCount = 0,
            History = trimmed,
        };
    }
}

/// <summary>
/// Un elemento del ring buffer <see cref="PatchState.History"/>: una transición registrada
/// del patch, con la fase y el <see cref="GateOutcome"/> de antes y de después más una razón
/// legible. Los <see cref="GateOutcome"/> son nullable porque el gate puede no aplicar
/// (fase <see cref="PatchPhase.Clean"/> o <see cref="PatchPhase.Unknown"/>).
/// </summary>
public sealed record PatchStateChange(
    DateTimeOffset At,
    PatchPhase FromPhase,
    PatchPhase ToPhase,
    GateOutcome? FromOutcome,
    GateOutcome? ToOutcome,
    string Reason);
