using Contracts.Monitor;
using Temporalio.Workflows;

namespace Contracts.Workflows;

/// <summary>
/// Contrato compartido entre cliente y worker del workflow de la pasada de monitoreo
/// (spec 06). Efímero: una ejecución arranca, hace una pasada sobre los patches descubiertos y
/// cierra devolviendo un <see cref="MonitorRunSummary"/>. Lo dispara un Temporal Schedule; no
/// hay estado que sobreviva entre ejecuciones aquí, ese vive en los entity workflows del
/// spec 05.
/// </summary>
[Workflow]
public interface IMonitorWorkflow
{
    [WorkflowRun]
    Task<MonitorRunSummary> RunAsync();
}
