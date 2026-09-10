namespace Contracts.Domain;

/// <summary>
/// Reglas de orden del ciclo de vida de un patch: "pasar de los pasos 1 al 2 y del 2 al 3
/// en orden" (README). Función pura, sin estado. La consumen el override manual del
/// spec 04 y la API del spec 08 para rechazar saltos ilegales.
/// </summary>
public static class PhaseTransition
{
    /// <summary>
    /// <c>true</c> solo para <see cref="PatchPhase.Coexistence"/> → <see cref="PatchPhase.Deprecated"/>
    /// y <see cref="PatchPhase.Deprecated"/> → <see cref="PatchPhase.Clean"/>. Todo lo demás es
    /// ilegal: saltear fases, retroceder, quedarse en la misma, o involucrar
    /// <see cref="PatchPhase.Unknown"/>.
    /// </summary>
    public static bool IsLegal(PatchPhase from, PatchPhase to) =>
        (from, to) switch
        {
            (PatchPhase.Coexistence, PatchPhase.Deprecated) => true,
            (PatchPhase.Deprecated, PatchPhase.Clean) => true,
            _ => false,
        };

    /// <summary>
    /// Fase siguiente en el orden legal, o <c>null</c> para <see cref="PatchPhase.Clean"/>
    /// (fase final) y <see cref="PatchPhase.Unknown"/> (sin fase de origen conocida).
    /// </summary>
    public static PatchPhase? NextOf(PatchPhase phase) =>
        phase switch
        {
            PatchPhase.Coexistence => PatchPhase.Deprecated,
            PatchPhase.Deprecated => PatchPhase.Clean,
            _ => null,
        };
}
