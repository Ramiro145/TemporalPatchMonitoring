using Contracts.Domain;

namespace Contracts.Phase;

/// <summary>
/// De dónde salió la fase de un <see cref="PhaseResolution"/>: deducida de los snapshots del
/// spec 03 (<see cref="Inferred"/>) o impuesta por el operador con un override manual
/// (<see cref="Override"/>). Cuando gana el override, la fase que se habría inferido viaja en
/// <see cref="PhaseResolution.Reason"/> para que el desacuerdo sea visible.
/// </summary>
public enum PhaseSource
{
    /// <summary>Fase deducida de los markers de las ejecuciones observadas.</summary>
    Inferred = 0,

    /// <summary>Fase forzada por un <see cref="PhaseOverride"/> vigente del operador.</summary>
    Override = 1,
}

/// <summary>
/// Resultado de resolver la fase actual de un patch: la fase, cómo se llegó a ella y una
/// razón legible para diagnóstico. Es un valor en memoria; persistirlo es del spec 05.
/// El spec 06 lo combina con el <c>PhaseEvaluator</c> del spec 02 en un <c>PatchAssessment</c>.
/// </summary>
public sealed record PhaseResolution(
    PatchPhase Phase,
    PhaseSource Source,
    string Reason,
    DateTimeOffset ResolvedAt);
