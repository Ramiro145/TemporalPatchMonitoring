using Microsoft.Extensions.DependencyInjection;
using Common;
using Contracts;
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
var scheduleClient = await provider.GetRequiredService<Lazy<Task<ITemporalClient>>>().Value;
var created = await ScheduleBootstrapper.EnsureScheduleAsync(scheduleClient, monitorOptions);
Console.WriteLine(created
    ? $"Schedule '{monitorOptions.ScheduleId}' creado."
    : $"Schedule '{monitorOptions.ScheduleId}' ya existía.");

await WorkerHost.RunAsync<HealthWorkflow>(
    taskQueue,
    provider,
    activityTypes: new[] { typeof(DiscoveryActivities), typeof(PhaseActivities), typeof(PatchStateActivities) },
    additionalWorkflowTypes: new[]
    {
        typeof(PatchStateWorkflow), typeof(PatchRegistryWorkflow), typeof(MonitorWorkflow),
    });
