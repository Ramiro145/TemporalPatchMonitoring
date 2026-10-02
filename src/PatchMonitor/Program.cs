using Common.Temporal;
using Microsoft.Extensions.DependencyInjection;
using Common;
using Contracts;
using Contracts.Discovery;
using Contracts.Monitor;
using PatchMonitor.Activities;
using PatchMonitor.Infrastructure;
using PatchMonitor.Workflows;
using Temporalio.Client;

// Task queue del worker del monitor. TEMPORAL_HOST lo lee WorkerHost al conectar.
var taskQueue = Environment.GetEnvironmentVariable("MONITOR_TASK_QUEUE") ?? TaskQueues.PatchMonitor;

var services = new ServiceCollection();
services.AddPatchMonitorServices();
var provider = services.BuildServiceProvider();

// El Schedule lo crea el worker que va a atender sus ejecuciones, no la API ni un servicio
// one-shot: un Schedule sin worker escuchando solo acumula ticks fallidos. La creación es
// idempotente: crea el Schedule si falta y lo actualiza si difiere de la configuración (spec 15).
var monitorOptions = provider.GetRequiredService<MonitorOptions>();
var clusterOptions = provider.GetRequiredService<MonitorClusterOptions>();
var discoveryOptions = provider.GetRequiredService<DiscoveryOptions>();
var scheduleClient = await provider.GetRequiredService<ResettableAsyncLazy<ITemporalClient>>().GetValueAsync();

// Guarda de auto-observación (spec 12): si el cluster/namespace propio coincide con el
// observado, el monitor se va a descubrir a sí mismo. Nunca bloquea (convención del repo).
if (clusterOptions.Host == discoveryOptions.TargetHost &&
    clusterOptions.Namespace == discoveryOptions.Namespace)
{
    Console.WriteLine(
        $"ADVERTENCIA: el monitor va a observarse a sí mismo — su propio cluster/namespace " +
        $"('{clusterOptions.Host}', '{clusterOptions.Namespace}') coincide con el observado.");
}

// Topes incoherentes (spec 15, M-7): las ejecuciones listadas más allá de MaxHistories no se
// inspeccionan, quedan Unknown y dejan los patches en Inconclusive. Nunca bloquea.
if (discoveryOptions.MaxHistories < discoveryOptions.MaxExecutions)
{
    Console.WriteLine(
        $"ADVERTENCIA: DISCOVERY_MAX_HISTORIES ({discoveryOptions.MaxHistories}) es menor que " +
        $"DISCOVERY_MAX_EXECUTIONS ({discoveryOptions.MaxExecutions}): las ejecuciones que " +
        $"excedan el primero no se inspeccionan y los patches pueden quedar Inconclusive.");
}

// Namespace propio del monitor: se crea si no existe antes de tocar el Schedule (spec 12).
await NamespaceBootstrapper.EnsureNamespaceAsync(scheduleClient, clusterOptions.Namespace);

var ensured = await ScheduleBootstrapper.EnsureScheduleAsync(scheduleClient, monitorOptions);
Console.WriteLine(ensured switch
{
    ScheduleEnsureResult.Created => $"Schedule '{monitorOptions.ScheduleId}' creado.",
    ScheduleEnsureResult.Updated => $"Schedule '{monitorOptions.ScheduleId}' actualizado: difería de la configuración.",
    _ => $"Schedule '{monitorOptions.ScheduleId}' ya existía y coincide con la configuración.",
});

await WorkerHost.RunAsync<HealthWorkflow>(
    taskQueue,
    provider,
    activityTypes: new[]
    {
        typeof(DiscoveryActivities), typeof(PhaseActivities), typeof(PatchStateActivities),
        typeof(NotificationActivities), typeof(ConfigActivities),
    },
    additionalWorkflowTypes: new[]
    {
        typeof(PatchStateWorkflow), typeof(PatchRegistryWorkflow), typeof(MonitorWorkflow),
    });
