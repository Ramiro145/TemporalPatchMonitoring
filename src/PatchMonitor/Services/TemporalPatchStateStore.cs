using System.Linq.Expressions;
using Common;
using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Contracts.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace PatchMonitor.Services;

/// <summary>
/// Implementación de <see cref="IPatchStateStore"/> sobre el <see cref="ITemporalClient"/> del
/// <b>cluster propio del monitor</b> (<c>TEMPORAL_HOST</c>, no <c>TARGET_TEMPORAL_HOST</c>: el
/// estado del monitor no vive en el namespace observado). Toda escritura es "crea-o-señala":
/// un start idempotente (<see cref="WorkflowIdConflictPolicy.UseExisting"/>) más el signal, sin
/// carrera de "existe / no existe". Los updates de override necesitan un entity ya arrancado,
/// que <see cref="EnsureEntityAsync"/> garantiza con el mismo start idempotente.
/// </summary>
public sealed class TemporalPatchStateStore : IPatchStateStore
{
    private readonly Lazy<Task<ITemporalClient>> _client;
    private readonly StateOptions _options;
    private readonly IDecisionSink _sink;

    public TemporalPatchStateStore(
        Lazy<Task<ITemporalClient>> client, StateOptions options, IDecisionSink sink)
    {
        _client = client;
        _options = options;
        _sink = sink;
    }

    public async Task<PatchState> RecordAssessmentAsync(
        PatchAssessmentInput input, CancellationToken ct = default)
    {
        // signal-with-start sobre el entity del patch: la primera pasada lo crea, las
        // siguientes solo lo señalan.
        var handle = await SignalWithStartAsync(
            input.Key.ToWorkflowId(),
            (IPatchStateWorkflow wf) => wf.RunAsync(input.Key, null),
            (IPatchStateWorkflow wf) => wf.RecordAssessmentAsync(input)).ConfigureAwait(false);

        // Indexar la clave en el registry, también por signal-with-start.
        await RegisterAsync(input.Key, ct).ConfigureAwait(false);

        // Leer el estado ya escrito y ofrecérselo al sink; su fallo no puede tirar abajo el
        // monitoreo (mismo criterio que el notificador del spec 07).
        var state = await handle.QueryAsync(wf => wf.GetState()).ConfigureAwait(false);
        try
        {
            await _sink.RecordAsync(input.Key, state, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tragado a propósito.
        }

        return state;
    }

    public async Task<PatchState?> GetStateAsync(PatchKey key, CancellationToken ct = default)
    {
        var client = await _client.Value.ConfigureAwait(false);
        var workflowId = key.ToWorkflowId();

        var (exists, _, error) = await WorkflowValidator
            .ValidateWorkflowAsync(client, workflowId).ConfigureAwait(false);

        if (!exists)
        {
            if (error == WorkflowValidator.NotFoundError)
            {
                return null;
            }

            throw new InvalidOperationException(
                $"No se pudo consultar el entity '{workflowId}': {error}");
        }

        return await client.GetWorkflowHandle<IPatchStateWorkflow>(workflowId)
            .QueryAsync(wf => wf.GetState()).ConfigureAwait(false);
    }

    public async Task RegisterAsync(PatchKey key, CancellationToken ct = default)
    {
        await SignalWithStartAsync(
            StateOptions.RegistryWorkflowId,
            (IPatchRegistryWorkflow wf) => wf.RunAsync(null),
            (IPatchRegistryWorkflow wf) => wf.RegisterAsync(key)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PatchKey>> ListAsync(CancellationToken ct = default)
    {
        var client = await _client.Value.ConfigureAwait(false);

        var (exists, _, error) = await WorkflowValidator
            .ValidateWorkflowAsync(client, StateOptions.RegistryWorkflowId).ConfigureAwait(false);

        if (!exists)
        {
            if (error == WorkflowValidator.NotFoundError)
            {
                return Array.Empty<PatchKey>();
            }

            throw new InvalidOperationException($"No se pudo consultar el registry: {error}");
        }

        var state = await client.GetWorkflowHandle<IPatchRegistryWorkflow>(StateOptions.RegistryWorkflowId)
            .QueryAsync(wf => wf.List()).ConfigureAwait(false);

        return state.Keys;
    }

    public async Task<IReadOnlyList<PhaseOverride>> LoadActiveOverridesAsync(CancellationToken ct = default)
    {
        var keys = await ListAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var active = new List<PhaseOverride>();

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            var state = await GetStateAsync(key, ct).ConfigureAwait(false);
            if (state?.Override is { } ov && ov.IsActiveAt(now))
            {
                active.Add(ov);
            }
        }

        return active;
    }

    public async Task<PatchState> SetOverrideAsync(PhaseOverride ov, CancellationToken ct = default)
    {
        var handle = await EnsureEntityAsync(ov.Key).ConfigureAwait(false);
        return await handle.ExecuteUpdateAsync(wf => wf.SetOverrideAsync(ov)).ConfigureAwait(false);
    }

    public async Task<PatchState> ClearOverrideAsync(PatchKey key, CancellationToken ct = default)
    {
        var handle = await EnsureEntityAsync(key).ConfigureAwait(false);
        return await handle.ExecuteUpdateAsync(wf => wf.ClearOverrideAsync()).ConfigureAwait(false);
    }

    // "Crea-o-señala" en un solo camino lógico: un start idempotente (UseExisting) seguido del
    // signal. Equivale a signal-with-start; se hace en dos RPCs porque el test-server de
    // time-skipping no responde queries hechas inmediatamente después de un
    // SignalWithStartWorkflowExecution.
    private async Task<WorkflowHandle<T>> SignalWithStartAsync<T>(
        string workflowId,
        Expression<Func<T, Task>> runCall,
        Expression<Func<T, Task>> signalCall)
        where T : class
    {
        var client = await _client.Value.ConfigureAwait(false);
        var options = new WorkflowOptions(workflowId, _options.TaskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
        };

        var handle = await client.StartWorkflowAsync(runCall, options).ConfigureAwait(false);
        await handle.SignalAsync(signalCall).ConfigureAwait(false);
        return handle;
    }

    // Los updates de override no admiten signal-with-start: el entity tiene que existir. Un
    // start idempotente (UseExisting) lo garantiza sin carrera.
    private async Task<WorkflowHandle<IPatchStateWorkflow>> EnsureEntityAsync(PatchKey key)
    {
        var client = await _client.Value.ConfigureAwait(false);
        var options = new WorkflowOptions(key.ToWorkflowId(), _options.TaskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
        };

        return await client.StartWorkflowAsync(
            (IPatchStateWorkflow wf) => wf.RunAsync(key, null), options).ConfigureAwait(false);
    }
}
