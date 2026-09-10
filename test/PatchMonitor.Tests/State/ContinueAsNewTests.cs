using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Contracts.Workflows;
using PatchMonitor.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace PatchMonitor.Tests.State;

/// <summary>
/// El <c>Continue-As-New</c> de ambos entity workflows, con umbrales bajos por env var para
/// ejercitar el camino real sin generar cientos de eventos. Sobre
/// <see cref="WorkflowEnvironment.StartTimeSkippingAsync"/>.
/// </summary>
[Collection(EnvVarCollection.Name)]
public class ContinueAsNewTests
{
    private const string ThresholdVar = "PATCH_STATE_CAN_THRESHOLD";
    private const string HistoryVar = "PATCH_STATE_HISTORY_LIMIT";

    private static readonly PatchKey Key = new("default", "OrderWorkflow", "order-v2");
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static PatchAssessmentInput Assessment(PhaseVerdict verdict, DateTimeOffset at) =>
        new(Key, new PhaseResolution(PatchPhase.Coexistence, PhaseSource.Inferred, "inferida", at), verdict, at);

    private static PhaseVerdict Verdict(GateOutcome outcome, PatchPhase? next, DateTimeOffset at) =>
        new(outcome, PatchPhase.Deprecated, next, 0, Array.Empty<string>(), $"{outcome}", at);

    private static async Task WithEnvAsync(string? threshold, string? history, Func<Task> body)
    {
        var savedThreshold = Environment.GetEnvironmentVariable(ThresholdVar);
        var savedHistory = Environment.GetEnvironmentVariable(HistoryVar);
        try
        {
            Environment.SetEnvironmentVariable(ThresholdVar, threshold);
            Environment.SetEnvironmentVariable(HistoryVar, history);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ThresholdVar, savedThreshold);
            Environment.SetEnvironmentVariable(HistoryVar, savedHistory);
        }
    }

    private static async Task<string> WaitForNewRunAsync(WorkflowHandle handle, string firstRunId)
    {
        for (var i = 0; i < 200; i++)
        {
            var description = await handle.DescribeAsync();
            if (description.RunId != firstRunId)
            {
                return description.RunId;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("El Continue-As-New no ocurrió dentro del tiempo esperado.");
    }

    [Fact]
    public async Task El_entity_hace_continue_as_new_al_superar_el_umbral_y_conserva_el_estado()
    {
        await WithEnvAsync("3", "2", async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"can-state-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchStateWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchStateWorkflow wf) => wf.RunAsync(Key, null),
                    new WorkflowOptions(id: $"can-state-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));

                var firstRunId = (await handle.DescribeAsync()).RunId;

                var ov = new PhaseOverride(Key, PatchPhase.Deprecated, "operador", T0, null);
                await handle.ExecuteUpdateAsync(wf => wf.SetOverrideAsync(ov));

                await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                    Assessment(Verdict(GateOutcome.Blocked, PatchPhase.Clean, T0), T0)));
                await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                    Assessment(Verdict(GateOutcome.Ready, null, T0.AddMinutes(5)), T0.AddMinutes(5))));
                await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                    Assessment(Verdict(GateOutcome.Inconclusive, PatchPhase.Clean, T0.AddMinutes(10)), T0.AddMinutes(10))));

                var laterRunId = await WaitForNewRunAsync(handle, firstRunId!);

                var state = await handle.QueryAsync(wf => wf.GetState());

                Assert.NotEqual(firstRunId, laterRunId);
                Assert.Equal(0, state.AssessmentCount);
                Assert.Equal(3, state.Revision);
                Assert.Equal(PatchPhase.Deprecated, state.Phase);
                Assert.NotNull(state.Override);
                Assert.Equal(2, state.History.Count);
            });
        });
    }

    [Fact]
    public async Task El_registry_hace_continue_as_new_arrastrando_el_set_completo()
    {
        await WithEnvAsync("3", null, async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"can-registry-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchRegistryWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchRegistryWorkflow wf) => wf.RunAsync(null),
                    new WorkflowOptions(id: $"can-registry-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));

                var firstRunId = (await handle.DescribeAsync()).RunId;

                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "A", "a")));
                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "B", "b")));
                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "C", "c")));

                var laterRunId = await WaitForNewRunAsync(handle, firstRunId!);

                var state = await handle.QueryAsync(wf => wf.List());

                Assert.NotEqual(firstRunId, laterRunId);
                Assert.Equal(3, state.Keys.Count);
            });
        });
    }
}
