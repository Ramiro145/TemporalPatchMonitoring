using Contracts.Domain;

namespace Contracts.Phase;

/// <summary>
/// Fase impuesta a mano por el operador para un patch, saltándose la inferencia. El operador
/// sabe qué desplegó; el override vigente gana siempre (sin validarse contra la fase inferida
/// ni contra <c>PhaseTransition.IsLegal</c>: esa validación es del spec 08, al escribir el
/// override por HTTP). <see cref="ExpiresAt"/> es opcional: un override permanente olvidado
/// congelaría el monitoreo del patch, así que puede caducar.
/// </summary>
public sealed record PhaseOverride(
    PatchKey Key,
    PatchPhase Phase,
    string DeclaredBy,
    DateTimeOffset DeclaredAt,
    DateTimeOffset? ExpiresAt)
{
    /// <summary>
    /// <c>true</c> si el override sigue vigente en el instante <paramref name="now"/>: sin
    /// <see cref="ExpiresAt"/> nunca caduca; con él, vale hasta (sin incluir) ese momento.
    /// </summary>
    public bool IsActiveAt(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}
