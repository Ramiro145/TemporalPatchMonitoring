namespace Contracts.Domain;

/// <summary>
/// Fase del ciclo de vida de un patch de Temporal (<c>Workflow.Patched</c> /
/// <c>Workflow.DeprecatePatch</c>). La máquina de estados es fija y conocida
/// (ver <c>Construction.md</c> §1); <see cref="Unknown"/> es el valor honesto para
/// cuando la fase todavía no se pudo resolver (historia truncada, sin datos).
/// </summary>
public enum PatchPhase
{
    /// <summary>Fase no resuelta. Default del enum: no debe interpretarse como fase 1.</summary>
    Unknown = 0,

    /// <summary>
    /// Fase 1 — Convivencia. <c>if (Workflow.Patched("id")) { nuevo } else { viejo }</c>.
    /// Se sale cuando no queda ninguna ejecución pre-patch abierta.
    /// </summary>
    Coexistence = 1,

    /// <summary>
    /// Fase 2 — Deprecación. <c>Workflow.DeprecatePatch("id");</c> + paso incondicional.
    /// Se sale cuando no queda ninguna ejecución abierta con el marker.
    /// </summary>
    Deprecated = 2,

    /// <summary>Fase 3 — Código limpio. El paso sin ninguna mención al patch. Fase final.</summary>
    Clean = 3,
}
