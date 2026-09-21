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
// idempotente (EnsureScheduleAsync captura ScheduleAlreadyRunningException).
var monitorOptions = provider.GetRequiredService<MonitorOptions>();
var clusterOptions = provider.GetRequiredService<MonitorClusterOptions>();
var discoveryOptions = provider.GetRequiredService<DiscoveryOptions>();
var scheduleClient = await provider.GetRequiredService<Lazy<Task<ITemporalClient>>>().Value;

// Guarda de auto-observación (spec 12): si el cluster/namespace propio coincide con el
// observado, el monitor se va a descubrir a sí mismo. Nunca bloquea (convención del repo).
if (clusterOptions.Host == discoveryOptions.TargetHost &&
    clusterOptions.Namespace == discoveryOptions.Namespace)
{
    Console.WriteLine(
        $"ADVERTENCIA: el monitor va a observarse a sí mismo — su propio cluster/namespace " +
        $"('{clusterOptions.Host}', '{clusterOptions.Namespace}') coincide con el observado.");
}

// Namespace propio del monitor: se crea si no existe antes de tocar el Schedule (spec 12).
await NamespaceBootstrapper.EnsureNamespaceAsync(scheduleClient, clusterOptions.Namespace);

var created = await ScheduleBootstrapper.EnsureScheduleAsync(scheduleClient, monitorOptions);
Console.WriteLine(created
    ? $"Schedule '{monitorOptions.ScheduleId}' creado."
    : $"Schedule '{monitorOptions.ScheduleId}' ya existía.");

await WorkerHost.RunAsync<HealthWorkflow>(
    taskQueue,
    provider,
    activityTypes: new[]
    {
        typeof(DiscoveryActivities), typeof(PhaseActivities), typeof(PatchStateActivities),
        typeof(NotificationActivities),
    },
    additionalWorkflowTypes: new[]
    {
        typeof(PatchStateWorkflow), typeof(PatchRegistryWorkflow), typeof(MonitorWorkflow),
    });
