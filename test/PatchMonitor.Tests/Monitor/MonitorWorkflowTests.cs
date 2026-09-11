using Contracts.Discovery;
using Contracts.Domain.Gates;
using Contracts.Monitor;
using Contracts.Notification;
using Contracts.Phase;
using Contracts.Workflows;
using PatchMonitor.Activities;
using PatchMonitor.Services;
using PatchMonitor.Tests.Discovery;
using PatchMonitor.Tests.Notification;
using PatchMonitor.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;
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

    private static async Task<MonitorRunSummary> RunAsync(
        IExecutionSource source, FakePatchStateStore store, IEnumerable<INotifier>? notifiers = null)
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
        var notificationActivities = new NotificationActivities(
            store, new CompositeNotifier(notifiers ?? new INotifier[] { new FakeNotifier("log") }));

        var options = new TemporalWorkerOptions(taskQueue).AddWorkflow<MonitorWorkflow>();
        options.AddAllActivities(discoveryActivities);
        options.AddAllActivities(phaseActivities);
        options.AddAllActivities(stateActivities);
        options.AddAllActivities(notificationActivities);

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

    [Fact]
    public async Task Un_patch_que_falla_al_persistir_no_aborta_la_pasada_y_el_error_queda_en_Errors()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"));
        var store = new FakePatchStateStore();
        store.FailRecordFor(new Contracts.Domain.PatchKey("default", "OrderWorkflow", "core-patch"));

        var summary = await RunAsync(source, store);

        Assert.Equal(2, summary.PatchesDiscovered);
        Assert.Equal(1, summary.PatchesAssessed);
        var error = Assert.Single(summary.Errors);
        Assert.Contains("fallo simulado", error);
    }

    [Fact]
    public async Task MaxPatchesPerRun_acota_los_assessments_sin_afectar_lo_descubierto()
    {
        var saved = Environment.GetEnvironmentVariable("MONITOR_MAX_PATCHES_PER_RUN");
        try
        {
            Environment.SetEnvironmentVariable("MONITOR_MAX_PATCHES_PER_RUN", "1");

            var source = new FakeExecutionSource().Seed(
                HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
                HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"));

            var summary = await RunAsync(source, new FakePatchStateStore());

            Assert.Equal(2, summary.PatchesDiscovered);
            Assert.Equal(1, summary.PatchesAssessed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MONITOR_MAX_PATCHES_PER_RUN", saved);
        }
    }

    [Fact]
    public async Task Un_cambio_de_veredicto_con_notificador_que_no_falla_produce_una_notificacion()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"));
        var notifier = new FakeNotifier("log");

        var summary = await RunAsync(source, new FakePatchStateStore(), new INotifier[] { notifier });

        Assert.True(summary.VerdictsChanged > 0);
        Assert.Equal(summary.VerdictsChanged, summary.NotificationsSent);
        Assert.Equal(0, summary.NotificationsFailed);
    }

    [Fact]
    public async Task Una_segunda_pasada_sin_cambio_no_produce_notificaciones()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"));
        var store = new FakePatchStateStore();
        var notifier = new FakeNotifier("log");

        await RunAsync(source, store, new INotifier[] { notifier });

        var second = await RunAsync(source, store, new INotifier[] { notifier });

        Assert.Equal(0, second.VerdictsChanged);
        Assert.Equal(0, second.NotificationsSent);
    }

    [Fact]
    public async Task Un_notificador_que_siempre_falla_cuenta_NotificationsFailed_sin_afectar_el_assessment()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"));
        var notifier = new FakeNotifier("log", fails: true);

        var summary = await RunAsync(source, new FakePatchStateStore(), new INotifier[] { notifier });

        Assert.Equal(2, summary.PatchesAssessed);
        Assert.True(summary.NotificationsFailed > 0);
        Assert.DoesNotContain(summary.Errors, e => !e.Contains("(notificación)"));
    }

    [Fact]
    public async Task Fallo_de_descubrimiento_entero_hace_fallar_la_corrida()
    {
        var ex = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => RunAsync(new FailingExecutionSource(), new FakePatchStateStore()));

        var activityFailure = Assert.IsType<ActivityFailureException>(ex.InnerException);
        var appFailure = Assert.IsType<ApplicationFailureException>(activityFailure.InnerException);
        Assert.Equal("DiscoveryConfigurationError", appFailure.ErrorType);
    }
}
