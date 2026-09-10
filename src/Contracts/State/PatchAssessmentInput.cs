using Contracts.Domain;
using Contracts.Phase;

namespace Contracts.State;

/// <summary>
/// Lo que el spec 06 manda por signal al entity workflow de un patch en cada pasada de
/// monitoreo: la clave, la resolución de fase (spec 04) y el veredicto del gate (spec 02)
/// junto al instante en que se observó. <see cref="Verdict"/> es nullable porque el gate
/// puede no aplicar (fase <c>Clean</c> o <c>Unknown</c>).
/// </summary>
public sealed record PatchAssessmentInput(
    PatchKey Key,
    PhaseResolution Resolution,
    PhaseVerdict? Verdict,
    DateTimeOffset ObservedAt);
