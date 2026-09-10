using Contracts.Domain;
using Contracts.Phase;

namespace Contracts.State;

/// <summary>
/// Puerto que consumen los specs 06, 07 y 08 para leer y escribir el estado durable de los
/// patches sin conocer Temporal. La implementación (<c>TemporalPatchStateStore</c>) traduce
/// cada método a <c>signal-with-start</c> / query sobre los entity workflows del cluster
/// propio del monitor; en tests se sustituye sin levantar cluster.
/// </summary>
public interface IPatchStateStore
{
    /// <summary>
    /// Registra el assessment en el entity del patch (creándolo si no existe), registra la
    /// clave en el registry y devuelve el <see cref="PatchState"/> resultante. También
    /// notifica al <see cref="IDecisionSink"/>, tragando cualquier fallo de este.
    /// </summary>
    Task<PatchState> RecordAssessmentAsync(PatchAssessmentInput input, CancellationToken ct = default);

    /// <summary>Estado durable del patch, o <c>null</c> si su entity no existe todavía. Cualquier otro error de RPC propaga.</summary>
    Task<PatchState?> GetStateAsync(PatchKey key, CancellationToken ct = default);

    /// <summary>Agrega la clave al registry singleton. Idempotente.</summary>
    Task RegisterAsync(PatchKey key, CancellationToken ct = default);

    /// <summary>Todas las <see cref="PatchKey"/> conocidas según el registry singleton.</summary>
    Task<IReadOnlyList<PatchKey>> ListAsync(CancellationToken ct = default);

    /// <summary>Los overrides vigentes de todos los entity workflows, para hidratar el store en memoria del resolver (spec 04).</summary>
    Task<IReadOnlyList<PhaseOverride>> LoadActiveOverridesAsync(CancellationToken ct = default);

    /// <summary>Impone un override de fase sobre el entity del patch y devuelve el estado resultante. Rechaza fases inválidas.</summary>
    Task<PatchState> SetOverrideAsync(PhaseOverride ov, CancellationToken ct = default);

    /// <summary>Quita el override vigente del entity del patch y devuelve el estado resultante.</summary>
    Task<PatchState> ClearOverrideAsync(PatchKey key, CancellationToken ct = default);
}
