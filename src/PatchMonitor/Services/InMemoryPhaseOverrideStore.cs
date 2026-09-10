using System.Collections.Concurrent;
using Contracts.Domain;
using Contracts.Phase;

namespace PatchMonitor.Services;

/// <summary>
/// Implementación en memoria de <see cref="IPhaseOverrideStore"/> sobre un
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> con clave <see cref="PatchKey.ToWorkflowId"/>
/// (ya determinística y saneada, spec 02). Se pierde al reiniciar el worker: la durabilidad del
/// override es del spec 05 (entity workflow). Acá el override es una capacidad operativa de
/// corta vida.
/// </summary>
/// <remarks>
/// Doble reloj a propósito: <see cref="TimeProvider"/> no se inyecta acá, así que la limpieza
/// de vencidos es best-effort contra <see cref="DateTimeOffset.UtcNow"/>. La decisión
/// autoritativa de "override vigente" la toma <c>PhaseResolver</c> con
/// <c>IsActiveAt(clock.GetUtcNow())</c> sobre su propio reloj inyectado.
/// </remarks>
public sealed class InMemoryPhaseOverrideStore : IPhaseOverrideStore
{
    private readonly ConcurrentDictionary<string, PhaseOverride> _overrides = new();

    public PhaseOverride? Get(PatchKey key)
    {
        var id = key.ToWorkflowId();
        if (!_overrides.TryGetValue(id, out var ov))
        {
            return null;
        }

        if (!ov.IsActiveAt(DateTimeOffset.UtcNow))
        {
            _overrides.TryRemove(new KeyValuePair<string, PhaseOverride>(id, ov));
            return null;
        }

        return ov;
    }

    public void Set(PhaseOverride ov) => _overrides[ov.Key.ToWorkflowId()] = ov;

    public void Clear(PatchKey key) => _overrides.TryRemove(key.ToWorkflowId(), out _);

    public IReadOnlyList<PhaseOverride> GetAll()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new List<PhaseOverride>();

        foreach (var (id, ov) in _overrides)
        {
            if (ov.IsActiveAt(now))
            {
                active.Add(ov);
            }
            else
            {
                _overrides.TryRemove(new KeyValuePair<string, PhaseOverride>(id, ov));
            }
        }

        return active;
    }
}
