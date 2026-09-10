using Contracts.Domain;
using Contracts.State;
using Contracts.Workflows;
using PatchMonitor.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace PatchMonitor.Tests.State;

/// <summary>
/// El registry singleton sobre <see cref="WorkflowEnvironment.StartTimeSkippingAsync"/>: sin
/// Docker ni SQL. La primera corrida descarga el binario del test-server de Temporal.
/// </summary>
public class PatchRegistryWorkflowTests
{
    private static readonly PatchKey KeyA = new("default", "OrderWorkflow", "order-v2");
    private static readonly PatchKey KeyB = new("default", "ShippingWorkflow", "ship-v3");

    private static async Task RunAsync(Func<WorkflowHandle<IPatchRegistryWorkflow>, Task> body)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"registry-{Guid.NewGuid():N}";
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue).AddWorkflow<PatchRegistryWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                (IPatchRegistryWorkflow wf) => wf.RunAsync(null),
                new WorkflowOptions(id: $"registry-wf-{Guid.NewGuid():N}", taskQueue: taskQueue));

            await body(handle);
        });
    }

    [Fact]
    public async Task Registrar_dos_claves_distintas_deja_dos_entradas()
    {
        await RunAsync(async handle =>
        {
            await handle.SignalAsync(wf => wf.RegisterAsync(KeyA));
            await handle.SignalAsync(wf => wf.RegisterAsync(KeyB));

            var state = await handle.QueryAsync(wf => wf.List());

            Assert.Equal(2, state.Keys.Count);
            Assert.Contains(KeyA, state.Keys);
            Assert.Contains(KeyB, state.Keys);
        });
    }

    [Fact]
    public async Task Registrar_la_misma_clave_dos_veces_es_idempotente()
    {
        await RunAsync(async handle =>
        {
            await handle.SignalAsync(wf => wf.RegisterAsync(KeyA));
            await handle.SignalAsync(wf => wf.RegisterAsync(KeyA));

            var state = await handle.QueryAsync(wf => wf.List());

            Assert.Single(state.Keys);
            Assert.Equal(KeyA, state.Keys[0]);
        });
    }

    [Fact]
    public async Task Unregister_saca_la_clave()
    {
        await RunAsync(async handle =>
        {
            await handle.SignalAsync(wf => wf.RegisterAsync(KeyA));
            await handle.SignalAsync(wf => wf.RegisterAsync(KeyB));
            await handle.SignalAsync(wf => wf.UnregisterAsync(KeyA));

            var state = await handle.QueryAsync(wf => wf.List());

            Assert.Single(state.Keys);
            Assert.Equal(KeyB, state.Keys[0]);
        });
    }

    [Fact]
    public async Task Unregister_de_una_clave_ausente_es_no_op()
    {
        await RunAsync(async handle =>
        {
            await handle.SignalAsync(wf => wf.RegisterAsync(KeyA));
            await handle.SignalAsync(wf => wf.UnregisterAsync(KeyB));

            var state = await handle.QueryAsync(wf => wf.List());

            Assert.Single(state.Keys);
            Assert.Equal(KeyA, state.Keys[0]);
        });
    }
}
