using Common.State;
using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using PatchMonitor.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace PatchMonitor.Tests.State;

/// <summary>
/// <see cref="TemporalPatchStateStore"/> contra <see cref="WorkflowEnvironment.StartTimeSkippingAsync"/>:
/// el store habla con un worker real que corre los dos entity workflows, sin Docker ni SQL.
/// </summary>
public class TemporalPatchStateStoreTests
{
    private static readonly PatchKey KeyA = new("default", "OrderWorkflow", "order-v2");
    private static readonly PatchKey KeyB = new("default", "ShippingWorkflow", "ship-v3");
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static PatchAssessmentInput Assessment(PatchKey key, DateTimeOffset at) =>
        new(
            key,
            new PhaseResolution(PatchPhase.Coexistence, PhaseSource.Inferred, "inferida", at),
            new PhaseVerdict(
                GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated,
                0, Array.Empty<string>(), "blocked", at),
            at);

    private sealed class ThrowingSink : IDecisionSink
    {
        public Task RecordAsync(PatchKey key, PatchState state, CancellationToken ct = default) =>
            throw new InvalidOperationException("sink caído");
    }

    private static async Task RunAsync(IDecisionSink sink, Func<TemporalPatchStateStore, Task> body)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"store-{Guid.NewGuid():N}";
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
        var store = new TemporalPatchStateStore(client, options, sink);

        await worker.ExecuteAsync(() => body(store));
    }

    [Fact]
    public async Task RecordAssessment_sobre_una_key_nueva_crea_el_entity_y_lo_registra()
    {
        await RunAsync(new NoopDecisionSink(), async store =>
        {
            var state = await store.RecordAssessmentAsync(Assessment(KeyA, T0));

            Assert.Equal(1, state.AssessmentCount);
            Assert.Equal(1, state.Revision);
            Assert.Equal(PatchPhase.Coexistence, state.Phase);

            var fetched = await store.GetStateAsync(KeyA);
            Assert.NotNull(fetched);
            Assert.Equal(1, fetched!.AssessmentCount);

            var keys = await store.ListAsync();
            Assert.Contains(KeyA, keys);
        });
    }

    [Fact]
    public async Task GetState_de_una_key_sin_entity_devuelve_null_y_no_lanza()
    {
        await RunAsync(new NoopDecisionSink(), async store =>
        {
            var missing = await store.GetStateAsync(new PatchKey("default", "Nope", "nope"));

            Assert.Null(missing);
        });
    }

    [Fact]
    public async Task ListAsync_devuelve_las_keys_registradas()
    {
        await RunAsync(new NoopDecisionSink(), async store =>
        {
            await store.RecordAssessmentAsync(Assessment(KeyA, T0));
            await store.RecordAssessmentAsync(Assessment(KeyB, T0));

            var keys = await store.ListAsync();

            Assert.Equal(2, keys.Count);
            Assert.Contains(KeyA, keys);
            Assert.Contains(KeyB, keys);
        });
    }

    [Fact]
    public async Task Un_sink_que_lanza_no_rompe_RecordAssessment()
    {
        await RunAsync(new ThrowingSink(), async store =>
        {
            var state = await store.RecordAssessmentAsync(Assessment(KeyA, T0));

            Assert.Equal(1, state.Revision);
            Assert.Equal(1, state.AssessmentCount);
        });
    }

    [Fact]
    public async Task LoadActiveOverrides_devuelve_solo_los_vigentes()
    {
        await RunAsync(new NoopDecisionSink(), async store =>
        {
            await store.RecordAssessmentAsync(Assessment(KeyA, T0));
            await store.RecordAssessmentAsync(Assessment(KeyB, T0));

            var now = DateTimeOffset.UtcNow;
            var active = new PhaseOverride(KeyA, PatchPhase.Deprecated, "operador", now, null);
            var expired = new PhaseOverride(
                KeyB, PatchPhase.Clean, "operador", now.AddHours(-2), now.AddHours(-1));

            await store.SetOverrideAsync(active);
            await store.SetOverrideAsync(expired);

            var result = await store.LoadActiveOverridesAsync();

            Assert.Single(result);
            Assert.Equal(KeyA, result[0].Key);
            Assert.Equal(PatchPhase.Deprecated, result[0].Phase);
        });
    }

    [Fact]
    public async Task SetOverride_y_ClearOverride_ida_y_vuelta()
    {
        await RunAsync(new NoopDecisionSink(), async store =>
        {
            await store.RecordAssessmentAsync(Assessment(KeyA, T0));

            var ov = new PhaseOverride(KeyA, PatchPhase.Deprecated, "operador", T0, null);
            var afterSet = await store.SetOverrideAsync(ov);
            Assert.Equal(PatchPhase.Deprecated, afterSet.Phase);
            Assert.Equal(PhaseSource.Override, afterSet.Source);

            var afterClear = await store.ClearOverrideAsync(KeyA);
            Assert.Null(afterClear.Override);
        });
    }

    [Fact]
    public async Task TryClaimNotification_dos_llamadas_seguidas_con_la_misma_revision_solo_la_primera_reclama()
    {
        await RunAsync(new NoopDecisionSink(), async store =>
        {
            await store.RecordAssessmentAsync(Assessment(KeyA, T0));

            var first = await store.TryClaimNotificationAsync(KeyA, 1);
            var second = await store.TryClaimNotificationAsync(KeyA, 1);

            Assert.True(first);
            Assert.False(second);

            var state = await store.GetStateAsync(KeyA);
            Assert.Equal(1, state!.NotifiedRevision);
        });
    }
}
