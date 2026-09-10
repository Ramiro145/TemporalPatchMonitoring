using Contracts.Domain;
using Contracts.State;
using Temporalio.Workflows;

namespace Contracts.Workflows;

/// <summary>
/// Contrato del registry singleton (spec 05): un índice explícito de todas las
/// <see cref="PatchKey"/> conocidas, con <c>WorkflowId = StateOptions.RegistryWorkflowId</c>,
/// arrancado por <c>signal-with-start</c> y mantenido vivo por <c>Continue-As-New</c>. El
/// spec 08 lo consulta para listar patches sin depender de Visibility (restricción #1).
/// </summary>
[Workflow]
public interface IPatchRegistryWorkflow
{
    /// <param name="carryover">Set de claves arrastrado tras un <c>Continue-As-New</c>, o <c>null</c> en el primer arranque.</param>
    [WorkflowRun]
    Task RunAsync(PatchRegistryState? carryover);

    /// <summary>Agrega una clave al índice. Idempotente: registrar la misma clave N veces deja una sola entrada.</summary>
    [WorkflowSignal]
    Task RegisterAsync(PatchKey key);

    /// <summary>Quita una clave del índice. No-op si no estaba.</summary>
    [WorkflowSignal]
    Task UnregisterAsync(PatchKey key);

    /// <summary>Estado vigente del índice: el set de claves conocidas más el instante de la última modificación.</summary>
    [WorkflowQuery]
    PatchRegistryState List();
}
