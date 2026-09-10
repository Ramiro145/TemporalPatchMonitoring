using Contracts.Discovery;

namespace Contracts.Phase;

/// <summary>
/// Resuelve la fase actual de un patch a partir de su <see cref="PatchDiscoveryResult"/> ya
/// armado por el spec 03. Síncrono y sin <c>CancellationToken</c>: es cómputo puro sobre datos
/// en memoria, sin I/O contra el cluster. Lo consume el <c>MonitorWorkflow</c> del spec 06.
/// </summary>
public interface IPhaseResolver
{
    /// <summary>La fase inferida (o el override vigente) para <paramref name="result"/>.</summary>
    PhaseResolution Resolve(PatchDiscoveryResult result);
}
