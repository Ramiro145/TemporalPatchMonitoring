using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using PatchMonitor.Activities;
using PatchMonitor.Services;
using PatchMonitor.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace PatchMonitor.Tests.State;

/// <summary>
/// <see cref="PatchStateActivities"/> sobre un store real contra
/// <see cref="WorkflowEnvironment.StartTimeSkippingAsync"/>. El foco es la hidratación del
/// <see cref="IPhaseOverrideStore"/> de corrida.
/// </summary>
public class PatchStateActivitiesTests
{
    private static readonly PatchKey KeyA = new("default", "OrderWorkflow", "order-v2");
    private static readonly PatchKey KeyStale = new("default", "LegacyWorkflow", "legacy-v1");
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static PatchAssessmentInput Assessment(PatchKey key) =>
        new(
            key,
            new PhaseResolution(PatchPhase.Coexistence, PhaseSource.Inferred, "inferida", T0),
            new PhaseVerdict(
                GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated,
                0, Array.Empty<string>(), "blocked", T0),
            T0);

    private static async Task RunAsync(
        Func<PatchStateActivities, TemporalPatchStateStore, InMemoryPhaseOverrideStore, Task> body)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"activities-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<PatchStateWorkflow>()
                .AddWorkflow<PatchRegistryWorkflow>());

        var options = new StateOptions(
            StateOptions.DefaultContinueAsNewThreshold,
            StateOptions.DefaultHistoryLimit,
            taskQueue);
        var client = new Lazy<Task<ITemporalClient>>(() => Task.FromResult<ITemporalClient>(env.Client));
        var store = new TemporalPatchStateStore(client, options, new NoopDecisionSink());
        var overrideCache = new InMemoryPhaseOverrideStore();
        var activities = new PatchStateActivities(store, overrideCache);

        await worker.ExecuteAsync(() => body(activities, store, overrideCache));
    }

    [Fact]
    public async Task LoadPhaseOverrides_deja_en_la_cache_exactamente_los_vigentes_del_entity()
    {
        await RunAsync(async (activities, store, overrideCache) =>
        {
            await store.RecordAssessmentAsync(Assessment(KeyA));
            await store.SetOverrideAsync(
                new PhaseOverride(KeyA, PatchPhase.Deprecated, "operador", T0, null));

            // Override que quedó en la caché de una corrida anterior pero ya no vive en ningún
            // entity (el operador lo borró).
            overrideCache.Set(new PhaseOverride(KeyStale, PatchPhase.Clean, "operador", T0, null));

            var count = await activities.LoadPhaseOverridesAsync();

            var all = overrideCache.GetAll();
            Assert.Equal(1, count);
            Assert.Single(all);
            Assert.Equal(KeyA, all[0].Key);
            Assert.Equal(PatchPhase.Deprecated, all[0].Phase);
        });
    }

    [Fact]
    public async Task Las_activities_de_lectura_delegan_en_el_store()
    {
        await RunAsync(async (activities, _, _) =>
        {
            var recorded = await activities.RecordAssessmentAsync(Assessment(KeyA));
            Assert.Equal(1, recorded.Revision);

            var fetched = await activities.GetPatchStateAsync(KeyA);
            Assert.NotNull(fetched);

            var keys = await activities.ListPatchesAsync();
            Assert.Contains(KeyA, keys);
        });
    }
}
