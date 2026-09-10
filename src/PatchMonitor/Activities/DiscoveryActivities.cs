using Contracts.Discovery;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace PatchMonitor.Activities;

/// <summary>
/// Envoltura <c>[Activity]</c> del descubrimiento de patches. El spec 06 la invoca desde
/// <c>MonitorWorkflow</c>. Un error de configuración (namespace inexistente, credenciales
/// inválidas) se relanza como <see cref="ApplicationFailureException"/> no reintentable —no
/// tiene sentido que Temporal lo repita cada 5 minutos—; un fallo de RPC transitorio se deja
/// propagar para que la política de reintentos de la Activity haga su trabajo.
/// </summary>
public class DiscoveryActivities
{
    private readonly IPatchDiscovery _discovery;

    public DiscoveryActivities(IPatchDiscovery discovery) => _discovery = discovery;

    [Activity]
    public async Task<IReadOnlyList<PatchDiscoveryResult>> DiscoverPatchesAsync()
    {
        try
        {
            return await _discovery
                .DiscoverAsync(ActivityExecutionContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException ex) when (IsConfigurationError(ex.Code))
        {
            throw new ApplicationFailureException(
                $"Descubrimiento mal configurado ({ex.Code}): {ex.Message}",
                errorType: "DiscoveryConfigurationError",
                nonRetryable: true);
        }
    }

    private static bool IsConfigurationError(RpcException.StatusCode code) => code is
        RpcException.StatusCode.NotFound or
        RpcException.StatusCode.InvalidArgument or
        RpcException.StatusCode.PermissionDenied or
        RpcException.StatusCode.Unauthenticated;
}
