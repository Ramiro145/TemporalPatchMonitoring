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
        await WithEnvAsync(null, null, async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"can-state-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchStateWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchStateWorkflow wf) => wf.RunAsync(Key, null, new StateOptions(3, 2, taskQueue)),
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
    public async Task El_umbral_pasado_por_argumento_gana_sobre_el_entorno_y_el_continue_as_new_lo_arrastra()
    {
        // El entorno trae un umbral enorme: si el workflow lo leyera, nunca haría CAN.
        await WithEnvAsync("1000", "1000", async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"can-state-arg-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchStateWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var options = new StateOptions(ContinueAsNewThreshold: 3, HistoryLimit: 2, taskQueue);
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchStateWorkflow wf) => wf.RunAsync(Key, null, options),
                    new WorkflowOptions(id: $"can-state-arg-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));

                var firstRunId = (await handle.DescribeAsync()).RunId;

                for (var i = 0; i < 3; i++)
                {
                    var at = T0.AddMinutes(i);
                    await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                        Assessment(Verdict(GateOutcome.Blocked, PatchPhase.Clean, at), at)));
                }

                var secondRunId = await WaitForNewRunAsync(handle, firstRunId!);

                // La segunda ejecución recibió las opciones por el CAN, no del entorno: con el
                // umbral del entorno (1000) este segundo salto nunca ocurriría.
                for (var i = 3; i < 6; i++)
                {
                    var at = T0.AddMinutes(i);
                    await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                        Assessment(Verdict(GateOutcome.Blocked, PatchPhase.Clean, at), at)));
                }

                var thirdRunId = await WaitForNewRunAsync(handle, secondRunId);

                Assert.NotEqual(firstRunId, secondRunId);
                Assert.NotEqual(secondRunId, thirdRunId);
            });
        });
    }

    [Fact]
    public async Task El_registry_con_umbral_por_argumento_hace_continue_as_new_sin_leer_el_entorno()
    {
        await WithEnvAsync("1000", null, async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"can-registry-arg-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchRegistryWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var options = new StateOptions(ContinueAsNewThreshold: 3, HistoryLimit: 2, taskQueue);
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchRegistryWorkflow wf) => wf.RunAsync(null, options),
                    new WorkflowOptions(id: $"can-registry-arg-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));

                var firstRunId = (await handle.DescribeAsync()).RunId;

                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "A", "a")));
                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "B", "b")));
                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "C", "c")));

                var secondRunId = await WaitForNewRunAsync(handle, firstRunId!);

                // El umbral viaja en el CAN: tres signals más provocan un segundo salto.
                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "D", "d")));
                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "E", "e")));
                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "F", "f")));

                var thirdRunId = await WaitForNewRunAsync(handle, secondRunId);
                var state = await handle.QueryAsync(wf => wf.List());

                Assert.NotEqual(secondRunId, thirdRunId);
                Assert.Equal(6, state.Keys.Count);
            });
        });
    }

    [Fact]
    public async Task El_registry_hace_continue_as_new_arrastrando_el_set_completo()
    {
        await WithEnvAsync(null, null, async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"can-registry-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchRegistryWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchRegistryWorkflow wf) => wf.RunAsync(null, new StateOptions(3, 2, taskQueue)),
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

    // ---- Spec 17: entities antiguas (arrancadas sin opciones) ----------------------------

    [Fact]
    public async Task Un_entity_antiguo_sin_opciones_no_hace_CAN_aunque_el_entorno_tenga_un_umbral_bajo()
    {
        // Antes del spec 17 el workflow leía el entorno: con umbral 1 hacía CAN en el primer
        // assessment y el replay de una historia grabada con otro umbral moría con NonDeterminism.
        await WithEnvAsync("1", null, async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"legacy-state-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchStateWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchStateWorkflow wf) => wf.RunAsync(Key, null, null),
                    new WorkflowOptions(id: $"legacy-state-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));
                var firstRunId = (await handle.DescribeAsync()).RunId;

                for (var i = 0; i < 3; i++)
                {
                    var at = T0.AddMinutes(i);
                    await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                        Assessment(Verdict(GateOutcome.Blocked, PatchPhase.Clean, at), at)));
                }

                var state = await handle.QueryAsync(wf => wf.GetState());
                await Task.Delay(300);

                Assert.Equal(3, state.AssessmentCount);
                Assert.Equal(firstRunId, (await handle.DescribeAsync()).RunId);
                Assert.False(await handle.QueryAsync(wf => wf.HasRecordedOptions()));
            });
        });
    }

    [Fact]
    public async Task Un_entity_antiguo_migrado_hace_CAN_con_el_umbral_grabado_y_lo_arrastra()
    {
        // El entorno trae un umbral enorme: si el workflow lo leyera, nunca haría CAN.
        await WithEnvAsync("1000", null, async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"legacy-state-mig-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchStateWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchStateWorkflow wf) => wf.RunAsync(Key, null, null),
                    new WorkflowOptions(id: $"legacy-state-mig-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));
                var firstRunId = (await handle.DescribeAsync()).RunId;

                await handle.SignalAsync(wf => wf.MigrateOptionsAsync(new StateOptions(2, 2, taskQueue)));
                // Idempotente: unas opciones ya grabadas no se pisan.
                await handle.SignalAsync(wf => wf.MigrateOptionsAsync(new StateOptions(1000, 2, taskQueue)));
                Assert.True(await handle.QueryAsync(wf => wf.HasRecordedOptions()));

                for (var i = 0; i < 2; i++)
                {
                    var at = T0.AddMinutes(i);
                    await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                        Assessment(Verdict(GateOutcome.Blocked, PatchPhase.Clean, at), at)));
                }

                var secondRunId = await WaitForNewRunAsync(handle, firstRunId!);

                // La segunda ejecución nació con las opciones arrastradas por el CAN.
                Assert.True(await handle.QueryAsync(wf => wf.HasRecordedOptions()));
                for (var i = 2; i < 4; i++)
                {
                    var at = T0.AddMinutes(i);
                    await handle.SignalAsync(wf => wf.RecordAssessmentAsync(
                        Assessment(Verdict(GateOutcome.Blocked, PatchPhase.Clean, at), at)));
                }

                var thirdRunId = await WaitForNewRunAsync(handle, secondRunId);
                Assert.NotEqual(secondRunId, thirdRunId);
            });
        });
    }

    [Fact]
    public async Task Un_registry_antiguo_sin_opciones_no_hace_CAN_hasta_migrar()
    {
        await WithEnvAsync("1", null, async () =>
        {
            await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
            var taskQueue = $"legacy-registry-{Guid.NewGuid():N}";
            using var worker = new TemporalWorker(
                env.Client,
                new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchRegistryWorkflow>());

            await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync(
                    (IPatchRegistryWorkflow wf) => wf.RunAsync(null, null),
                    new WorkflowOptions(id: $"legacy-registry-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));
                var firstRunId = (await handle.DescribeAsync()).RunId;

                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "A", "a")));
                await handle.SignalAsync(wf => wf.RegisterAsync(new PatchKey("default", "B", "b")));
                var before = await handle.QueryAsync(wf => wf.List());
                await Task.Delay(300);

                Assert.Equal(2, before.Keys.Count);
                Assert.Equal(firstRunId, (await handle.DescribeAsync()).RunId);
                Assert.False(await handle.QueryAsync(wf => wf.HasRecordedOptions()));

                // Migrado con un umbral ya superado: el CAN ocurre y arrastra el set completo.
                await handle.SignalAsync(wf => wf.MigrateOptionsAsync(new StateOptions(2, 2, taskQueue)));
                await handle.SignalAsync(wf => wf.MigrateOptionsAsync(new StateOptions(1000, 2, taskQueue)));

                var secondRunId = await WaitForNewRunAsync(handle, firstRunId!);
                var after = await handle.QueryAsync(wf => wf.List());

                Assert.NotEqual(firstRunId, secondRunId);
                Assert.Equal(2, after.Keys.Count);
                Assert.True(await handle.QueryAsync(wf => wf.HasRecordedOptions()));
            });
        });
    }
}
