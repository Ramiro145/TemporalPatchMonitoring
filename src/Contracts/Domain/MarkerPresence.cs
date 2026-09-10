namespace Contracts.Domain;

/// <summary>
/// Presencia del marker <c>MarkerRecorded · core_patch</c> en la Event History de una
/// ejecución, respecto de un patch concreto. Fase 1 y fase 2 escriben <b>ambas</b> el
/// marker (<c>Construction.md</c> §4 restricción #2); lo único que cambia es el flag
/// <c>deprecated</c> interno, de ahí la separación entre <see cref="Present"/> y
/// <see cref="PresentDeprecated"/>. <see cref="Absent"/> ("inspeccioné y no está") y
/// <see cref="Unknown"/> ("no llegué a inspeccionar") son cosas distintas.
/// </summary>
public enum MarkerPresence
{
    /// <summary>Historia no inspeccionada o truncada: no se sabe si hay marker.</summary>
    Unknown = 0,

    /// <summary>Inspeccionada: no hay marker para este patch (ejecución pre-patch).</summary>
    Absent = 1,

    /// <summary>Marker presente sin el flag <c>deprecated</c> (fase 1).</summary>
    Present = 2,

    /// <summary>Marker presente con el flag <c>deprecated</c> puesto (fase 2).</summary>
    PresentDeprecated = 3,
}
