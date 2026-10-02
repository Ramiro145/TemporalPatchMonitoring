using Contracts.Discovery;
using Contracts.State;

namespace Contracts.Phase;

/// <summary>
/// Resuelve la fase actual de un patch a partir de su <see cref="PatchDiscoveryResult"/> ya
/// armado por el spec 03. Síncrono y sin <c>CancellationToken</c>: es cómputo puro sobre datos
/// en memoria, sin I/O contra el cluster. Lo consume el <c>MonitorWorkflow</c> del spec 06.
/// </summary>
public interface IPhaseResolver
{
    /// <summary>
    /// La fase inferida (o el override vigente) para <paramref name="result"/>.
    /// <paramref name="previous"/> es el estado durable del patch en la pasada anterior (o
    /// <c>null</c> si aún no existe); permite conservar <c>Clean</c> cuando la evidencia sale de
    /// la ventana de lookback sin que haya ninguna novedad (spec 15, M-6).
    /// </summary>
    PhaseResolution Resolve(PatchDiscoveryResult result, PatchState? previous);
}
