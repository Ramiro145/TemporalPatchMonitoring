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
    private StateOptions _options = null!;
    private DateTimeOffset _updatedAt;
    private int _signalsSinceStart;

    [WorkflowRun]
    public async Task RunAsync(PatchRegistryState? carryover)
    {
        _options = StateOptions.FromEnvironment();
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
            () => _signalsSinceStart >= _options.ContinueAsNewThreshold
                  && Workflow.AllHandlersFinished);

        throw Workflow.CreateContinueAsNewException(
            (IPatchRegistryWorkflow wf) => wf.RunAsync(List()));
    }

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
