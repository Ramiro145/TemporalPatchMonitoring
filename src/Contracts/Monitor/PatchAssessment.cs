using Contracts.Domain;
using Contracts.Phase;

namespace Contracts.Monitor;

/// <summary>
/// Resultado de evaluar un patch en una pasada de monitoreo: la fase resuelta y, cuando
/// corresponde, el veredicto del gate de salto. <see cref="Verdict"/> es <c>null</c> para las
/// fases <see cref="PatchPhase.Clean"/> (final, no hay gate que evaluar) y
/// <see cref="PatchPhase.Unknown"/> (sin resolver).
/// </summary>
public sealed record PatchAssessment(PhaseResolution Resolution, PhaseVerdict? Verdict);
