using Contracts.Domain;

namespace Contracts.Phase;

/// <summary>
/// Puerto de almacenamiento de los overrides de fase declarados por el operador. En este spec
/// lo implementa <c>InMemoryPhaseOverrideStore</c> (un <c>ConcurrentDictionary</c> que se
/// pierde al reiniciar el worker); el spec 05 lo reemplaza por un entity workflow durable y el
/// spec 08 lo escribe vía HTTP.
/// </summary>
public interface IPhaseOverrideStore
{
    /// <summary>
    /// El override vigente para <paramref name="key"/>, o <c>null</c> si no hay ninguno o el
    /// que había ya caducó. La implementación puede descartar los vencidos al consultarlos.
    /// </summary>
    PhaseOverride? Get(PatchKey key);

    /// <summary>Guarda <paramref name="ov"/>, reemplazando cualquier override previo del mismo patch.</summary>
    void Set(PhaseOverride ov);

    /// <summary>Borra el override de <paramref name="key"/> si existe; no-op si no hay ninguno.</summary>
    void Clear(PatchKey key);

    /// <summary>Todos los overrides vigentes, para que el spec 08 los liste y el operador los revise.</summary>
    IReadOnlyList<PhaseOverride> GetAll();
}
