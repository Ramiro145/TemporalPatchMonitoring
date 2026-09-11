using Contracts.Discovery;
using Contracts.Domain;
using Temporalio.Exceptions;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// <see cref="IExecutionSource"/> que siempre lanza un <see cref="RpcException"/> de
/// configuración (<c>NotFound</c>) al listar, para ejercitar el camino de
/// <c>DiscoveryConfigurationError</c> no reintentable de <c>DiscoveryActivities</c>.
/// </summary>
public sealed class FailingExecutionSource : IExecutionSource
{
    public Task<ExecutionListPage> ListExecutionsAsync(
        ExecutionListFilter filter, CancellationToken ct = default) =>
        throw new RpcException(RpcException.StatusCode.NotFound, "namespace inexistente", null);

    public Task<IReadOnlyList<PatchMarker>> ReadPatchMarkersAsync(
        string workflowId, string runId, CancellationToken ct = default) =>
        throw new InvalidOperationException("No debería llegar a leer historia.");
}
