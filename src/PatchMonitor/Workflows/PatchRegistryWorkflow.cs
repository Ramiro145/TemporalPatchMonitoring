using Contracts.Domain;
using Contracts.State;
using Contracts.Workflows;
using Temporalio.Workflows;

namespace PatchMonitor.Workflows;

/// <summary>
/// Índice singleton de todas las <see cref="PatchKey"/> conocidas (spec 05). Una sola
/// ejecución, con <c>WorkflowId = StateOptions.RegistryWorkflowId</c>, arrancada por
/// <c>signal-with-start</c> desde <c>TemporalPatchStateStore</c>. El registro es idempotente:
/// registrar la misma clave N veces deja una sola entrada. Como nunca cierra, hace
/// <c>Continue-As-New</c> cuando la cantidad de signals recibidos supera
/// <see cref="StateOptions.ContinueAsNewThreshold"/>, arrastrando el set completo.
/// </summary>
[Workflow]
public class PatchRegistryWorkflow : IPatchRegistryWorkflow
{
    // Clave = WorkflowId determinístico del entity; valor = la PatchKey original. El
    // WorkflowId saneado es lo que da la deduplicación estable.
    private readonly Dictionary<string, PatchKey> _keys = new();
    // Nulo en una ejecución antigua (arrancada antes del spec 14) hasta que MigrateOptionsAsync
    // las graba. Nunca se completa desde el entorno: leerlo no es determinístico (spec 17).
    private StateOptions? _options;
    private DateTimeOffset _updatedAt;
    private int _signalsSinceStart;

    [WorkflowRun]
    public async Task RunAsync(PatchRegistryState? carryover, StateOptions? options = null)
    {
        // Argumento de arranque arrastrado por el Continue-As-New. Una ejecución antigua (sin él)
        // no lee el entorno (spec 17): queda sin opciones y no hace Continue-As-New hasta recibir
        // MigrateOptionsAsync. "?? _options" evita pisar unas opciones que un signal ya grabó si
        // llegó antes de que corra RunAsync.
        _options = options ?? _options;
        _updatedAt = Workflow.UtcNow;

        if (carryover is not null)
        {
            foreach (var key in carryover.Keys)
            {
                _keys[key.ToWorkflowId()] = key;
            }

            _updatedAt = carryover.UpdatedAt;
        }

        await Workflow.WaitConditionAsync(
            () => _options is { } o
                  && _signalsSinceStart >= o.ContinueAsNewThreshold
                  && Workflow.AllHandlersFinished);

        var recorded = _options!;
        throw Workflow.CreateContinueAsNewException(
            (IPatchRegistryWorkflow wf) => wf.RunAsync(List(), recorded));
    }

    [WorkflowSignal]
    public Task MigrateOptionsAsync(StateOptions options)
    {
        // Idempotente. El marker se evalúa en vivo (la señal no existe en historias viejas).
        if (_options is null && Workflow.Patched(PatchStateWorkflow.MigrateOptionsPatchId))
        {
            _options = options;
        }

        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public bool HasRecordedOptions() => _options is not null;

    [WorkflowSignal]
    public Task RegisterAsync(PatchKey key)
    {
        _signalsSinceStart++;

        if (_keys.TryAdd(key.ToWorkflowId(), key))
        {
            _updatedAt = Workflow.UtcNow;
        }

        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task UnregisterAsync(PatchKey key)
    {
        _signalsSinceStart++;

        if (_keys.Remove(key.ToWorkflowId()))
        {
            _updatedAt = Workflow.UtcNow;
        }

        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public PatchRegistryState List() =>
        new(
            _keys.Values
                .OrderBy(key => key.ToWorkflowId(), StringComparer.Ordinal)
                .ToArray(),
            _updatedAt);
}
