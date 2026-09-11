using Common;
using Contracts;
using Contracts.Workflows;
using MonitorApi.Endpoints;
using MonitorApi.Infrastructure;
using Temporalio.Client;

var builder = WebApplication.CreateBuilder(args);

// Dónde corre el cluster propio del monitor (conexión del cliente de la API y del worker).
var temporalHost = Environment.GetEnvironmentVariable("TEMPORAL_HOST") ?? "temporal:7233";
// Task queue del worker del monitor; se expone en /health para diagnóstico.
var taskQueue = Environment.GetEnvironmentVariable("MONITOR_TASK_QUEUE") ?? TaskQueues.PatchMonitor;
// Namespace observado. Declarado ya en el spec 01 (sin consumidor real todavía); el spec 03
// empieza a leer de él. Acá solo se refleja en la respuesta de /health.
var targetNamespace = Environment.GetEnvironmentVariable("TARGET_TEMPORAL_NAMESPACE") ?? "default";

// TemporalClient singleton. Se conecta de forma ansiosa (igual que el OrderApi del repo de
// referencia); con depends_on: temporal + reintentos de ConnectAsync alcanza para el arranque.
builder.Services.AddSingleton(_ => TemporalClient.ConnectAsync(new TemporalClientConnectOptions
{
    TargetHost = temporalHost
}).GetAwaiter().GetResult());

builder.Services.AddMonitorApiServices();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.WebHost.UseUrls("http://0.0.0.0:5100");

var app = builder.Build();

// Swagger habilitado siempre (no solo en Development).
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Monitor API v1");
    c.RoutePrefix = "swagger";
});

// Reachability del cluster sin fallar aunque no exista ningún workflow: se consulta un id
// inexistente y se reusa WorkflowValidator, que distingue "no encontrado" (cluster vivo) de
// cualquier otro error de RPC (cluster inalcanzable).
app.MapGet("/health", async (TemporalClient client) =>
{
    var probe = await WorkflowValidator.ValidateWorkflowAsync(client, $"health-probe-{Guid.NewGuid()}");
    var reachable = probe.Exists || probe.Error == WorkflowValidator.NotFoundError;

    return Results.Ok(new
    {
        temporal = reachable ? "ok" : "unreachable",
        targetNamespace,
        taskQueue
    });
});

// Arranca HealthWorkflow y devuelve el workflowId generado por WorkflowStarter.
app.MapPost("/health/workflow", async () =>
{
    var workflowId = await WorkflowStarter.StartAsync<IHealthWorkflow, string>(
        taskQueue,
        wf => wf.RunAsync(),
        "health-workflow");

    return Results.Ok(new { workflowId });
});

// Superficie de control de patches y del Schedule (spec 08).
app.MapGet("/patches", PatchEndpoints.ListAsync)
    .WithName("ListPatches").WithTags("Patches");
app.MapGet("/patches/{ns}/{type}/{patchId}", PatchEndpoints.GetAsync)
    .WithName("GetPatch").WithTags("Patches");
app.MapPost("/patches/{ns}/{type}/{patchId}/override", PatchEndpoints.SetOverrideAsync)
    .WithName("SetPatchOverride").WithTags("Patches");
app.MapDelete("/patches/{ns}/{type}/{patchId}/override", PatchEndpoints.ClearOverrideAsync)
    .WithName("ClearPatchOverride").WithTags("Patches");

app.MapGet("/schedule", ScheduleEndpoints.DescribeAsync)
    .WithName("DescribeSchedule").WithTags("Schedule");
app.MapPost("/schedule/pause", ScheduleEndpoints.PauseAsync)
    .WithName("PauseSchedule").WithTags("Schedule");
app.MapPost("/schedule/unpause", ScheduleEndpoints.UnpauseAsync)
    .WithName("UnpauseSchedule").WithTags("Schedule");
app.MapPost("/schedule/trigger", ScheduleEndpoints.TriggerAsync)
    .WithName("TriggerSchedule").WithTags("Schedule");

app.Run();
