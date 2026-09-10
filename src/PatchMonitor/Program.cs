using Microsoft.Extensions.DependencyInjection;
using Common;
using Contracts;
using PatchMonitor.Activities;
using PatchMonitor.Infrastructure;
using PatchMonitor.Workflows;

// Task queue del worker del monitor. TEMPORAL_HOST lo lee WorkerHost al conectar.
var taskQueue = Environment.GetEnvironmentVariable("MONITOR_TASK_QUEUE") ?? TaskQueues.PatchMonitor;

var services = new ServiceCollection();
services.AddPatchMonitorServices();
var provider = services.BuildServiceProvider();

await WorkerHost.RunAsync<HealthWorkflow>(
    taskQueue,
    provider,
    activityTypes: new[] { typeof(DiscoveryActivities) });
