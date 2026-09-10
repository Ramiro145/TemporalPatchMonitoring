namespace Contracts.Discovery;

/// <summary>
/// Descubre qué <c>patchId</c> hay en juego en el namespace objetivo y arma, por cada uno, el
/// <see cref="Domain.ExecutionSnapshotSet"/> que los gates del spec 02 evalúan. Es el único
/// punto del sistema que habla con Temporal como <i>fuente de datos</i> (<c>Construction.md</c>
/// §7). El spec 06 lo invoca desde una Activity dentro de <c>MonitorWorkflow</c>.
/// </summary>
public interface IPatchDiscovery
{
    Task<IReadOnlyList<PatchDiscoveryResult>> DiscoverAsync(CancellationToken ct = default);
}
