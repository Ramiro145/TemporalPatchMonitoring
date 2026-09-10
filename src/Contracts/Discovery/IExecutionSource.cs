namespace Contracts.Discovery;

/// <summary>
/// Puerto <b>angosto</b> sobre Temporal como fuente de datos: lo mínimo que el descubrimiento
/// necesita, con DTOs propios y sin un solo tipo del SDK. El adaptador real
/// (<c>TemporalExecutionSource</c>, en el worker) vive detrás de esta interfaz para que
/// <c>PatchDiscoveryService</c> se pruebe con un fake en memoria, sin cluster ni Docker
/// (<c>Construction.md</c> §7).
/// </summary>
public interface IExecutionSource
{
    /// <summary>
    /// Lista ejecuciones del namespace objetivo con una query simple derivada de
    /// <paramref name="filter"/>. Nunca consulta <c>TemporalChangeVersion</c> por query.
    /// </summary>
    Task<ExecutionListPage> ListExecutionsAsync(
        ExecutionListFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Lee la Event History de una ejecución y devuelve un <see cref="PatchMarker"/> por cada
    /// evento <c>MarkerRecorded</c> de nombre <c>core_patch</c>. Lista vacía = historia leída
    /// completa y sin markers. Lanza si la lectura falla o queda incompleta: el llamador trata
    /// esa ejecución como <see cref="Domain.MarkerPresence.Unknown"/>.
    /// </summary>
    Task<IReadOnlyList<PatchMarker>> ReadPatchMarkersAsync(
        string workflowId, string runId, CancellationToken ct = default);
}
