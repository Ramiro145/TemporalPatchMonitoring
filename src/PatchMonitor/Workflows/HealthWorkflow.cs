using Temporalio.Workflows;
using Contracts.Workflows;

namespace PatchMonitor.Workflows;

/// <summary>
/// Workflow trivial de prueba de vida: no ejecuta activities ni timers, solo devuelve
/// una constante. Sirve para verificar que el worker toma tareas de la task queue y las
/// completa de punta a punta (spec 01). El spec 06 lo reemplaza por <c>MonitorWorkflow</c>.
/// </summary>
[Workflow]
public class HealthWorkflow : IHealthWorkflow
{
    [WorkflowRun]
    public Task<string> RunAsync() => Task.FromResult("patch-monitor alive");
}
