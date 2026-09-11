using Contracts.Domain;
using Contracts.State;
using Contracts.Workflows;
using PatchMonitor.Tests.State;
using PatchMonitor.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="PatchStateWorkflow.TryClaimNotificationAsync"/>: reclama una <c>Revision</c>
/// solo si es mayor que <see cref="PatchState.NotifiedRevision"/> vigente, y ese campo
/// sobrevive al <c>Continue-As-New</c> igual que <see cref="PatchState.Revision"/>.
/// </summary>
[Collection(EnvVarCollection.Name)]
public class NotificationClaimTests
{
    private const string ThresholdVar = "PATCH_STATE_CAN_THRESHOLD";

    private static readonly PatchKey Key = new("default", "OrderWorkflow", "order-v2");

    private static async Task RunAsync(Func<WorkflowHandle<IPatchStateWorkflow>, Task> body)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"notif-claim-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchStateWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                (IPatchStateWorkflow wf) => wf.RunAsync(Key, null),
                new WorkflowOptions(id: $"notif-claim-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));

            await body(handle);
        });
    }

    [Fact]
    public async Task Reclamar_una_revision_nueva_avanza_notified_revision()
    {
        await RunAsync(async handle =>
        {
            var claimed = await handle.ExecuteUpdateAsync(wf => wf.TryClaimNotificationAsync(1));
            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.True(claimed);
            Assert.Equal(1, state.NotifiedRevision);
        });
    }

    [Fact]
    public async Task Reclamar_la_misma_revision_de_nuevo_devuelve_false_sin_cambiar_estado()
    {
        await RunAsync(async handle =>
        {
            await handle.ExecuteUpdateAsync(wf => wf.TryClaimNotificationAsync(1));

            var claimedAgain = await handle.ExecuteUpdateAsync(wf => wf.TryClaimNotificationAsync(1));
            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.False(claimedAgain);
            Assert.Equal(1, state.NotifiedRevision);
        });
    }

    [Fact]
    public async Task Reclamar_una_revision_mayor_avanza_de_nuevo()
    {
        await RunAsync(async handle =>
        {
            await handle.ExecuteUpdateAsync(wf => wf.TryClaimNotificationAsync(1));

            var claimed = await handle.ExecuteUpdateAsync(wf => wf.TryClaimNotificationAsync(2));
            var state = await handle.QueryAsync(wf => wf.GetState());

            Assert.True(claimed);
            Assert.Equal(2, state.NotifiedRevision);
        });
    }

    [Fact]
    public async Task NotifiedRevision_sobrevive_al_continue_as_new()
    {
        var savedThreshold = Environment.GetEnvironmentVariable(ThresholdVar);
        try
        {
            Environment.SetEnvironmentVariable(ThresholdVar, "1");

            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"notif-claim-can-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchStateWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchStateWorkflow wf) => wf.RunAsync(Key, null),
                    new WorkflowOptions(id: $"notif-claim-can-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));

                await handle.ExecuteUpdateAsync(wf => wf.TryClaimNotificationAsync(1));

                var firstRunId = (await handle.DescribeAsync()).RunId;

                var at = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
                var verdict = new Contracts.Domain.PhaseVerdict(
                    GateOutcome.Ready, PatchPhase.Coexistence, PatchPhase.Deprecated, 0, Array.Empty<string>(), "ready", at);
                var resolution = new Contracts.Phase.PhaseResolution(
                    PatchPhase.Coexistence, Contracts.Phase.PhaseSource.Inferred, "inferida", at);

                await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                    new PatchAssessmentInput(Key, resolution, verdict, at)));

                string laterRunId = firstRunId!;
                for (var i = 0; i < 200 && laterRunId == firstRunId; i++)
                {
                    await Task.Delay(25);
                    laterRunId = (await handle.DescribeAsync()).RunId!;
                }

                Assert.NotEqual(firstRunId, laterRunId);

                var state = await handle.QueryAsync(wf => wf.GetState());
                Assert.Equal(1, state.NotifiedRevision);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(ThresholdVar, savedThreshold);
        }
    }
}
