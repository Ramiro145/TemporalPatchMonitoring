using Contracts.Discovery;
using Contracts.Domain.Gates;
using Contracts.Monitor;
using Contracts.Phase;
using Contracts.Workflows;
using PatchMonitor.Activities;
using PatchMonitor.Services;
using PatchMonitor.Tests.Discovery;
using PatchMonitor.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// La pasada de <see cref="MonitorWorkflow"/> sobre <see cref="WorkflowEnvironment.StartTimeSkippingAsync"/>,
/// con las Activities reales (spec 03/04/05) enchufadas a un <see cref="FakeExecutionSource"/>
/// de dos patches y un <see cref="FakePatchStateStore"/> en memoria.
/// </summary>
public class MonitorWorkflowTests
{
    private static readonly DiscoveryOptions Discovery =
        new("default", LookbackDays: 7, MaxExecutions: 500, MaxHistories: 200);

    private static async Task<MonitorRunSummary> RunAsync(FakeExecutionSource source, FakePatchStateStore store)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"monitor-{Guid.NewGuid():N}";

        var discoveryActivities = new DiscoveryActivities(new PatchDiscoveryService(source, Discovery));
        var phaseActivities = new PhaseActivities(
            new PhaseResolver(
                new InMemoryPhaseOverrideStore(),
                new PhaseOptions(TimeSpan.FromHours(24)),
                TimeProvider.System),
            new PhaseEvaluator(new IPhaseGate[]
            {
                new CoexistenceToDeprecatedGate(),
                new DeprecatedToCleanGate(),
            }),
            new InMemoryPhaseOverrideStore());
        var stateActivities = new PatchStateActivities(store, new InMemoryPhaseOverrideStore());

        var options = new TemporalWorkerOptions(taskQueue).AddWorkflow<MonitorWorkflow>();
        options.AddAllActivities(discoveryActivities);
        options.AddAllActivities(phaseActivities);
        options.AddAllActivities(stateActivities);

        using var worker = new TemporalWorker(env.Client, options);

        MonitorRunSummary summary = null!;
        await worker.ExecuteAsync(async () =>
        {
            summary = await env.Client.ExecuteWorkflowAsync(
                (IMonitorWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions(id: $"monitor-run-{Guid.NewGuid():N}", taskQueue: taskQueue));
        });

        return summary;
    }

    [Fact]
    public async Task Pasada_feliz_descubre_y_evalua_los_dos_patches()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"));

        var summary = await RunAsync(source, new FakePatchStateStore());

        Assert.Equal(2, summary.PatchesDiscovered);
        Assert.Equal(2, summary.PatchesAssessed);
        Assert.Empty(summary.Errors);
    }

    [Fact]
    public async Task Segundo_run_con_el_mismo_estado_deja_VerdictsChanged_en_cero()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"));
        var store = new FakePatchStateStore();

        var first = await RunAsync(source, store);
        Assert.True(first.VerdictsChanged > 0);

        var second = await RunAsync(source, store);

        Assert.Equal(0, second.VerdictsChanged);
    }
}
