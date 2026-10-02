using Temporalio.Exceptions;

namespace PatchMonitor.Activities;

/// <summary>
/// Convierte un <see cref="WorkflowQueryFailedException"/> en un fallo no reintentable (spec 17).
/// Una query que falla porque el replay del entity no coincide con su historia (por ejemplo un
/// <c>Nondeterminism error</c>) no se arregla reintentando: con la política por defecto la
/// Activity reintentaba sin tope y colgaba el tick entero de <c>MonitorWorkflow</c>. Con el fallo
/// no reintentable, el patch afectado cae al <c>catch (ActivityFailureException)</c> del workflow,
/// queda en <c>errors</c> del run y el tick sigue con los demás.
/// </summary>
public static class QueryFailureGuard
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (WorkflowQueryFailedException ex)
        {
            throw new ApplicationFailureException(
                ex.Message, ex, errorType: nameof(WorkflowQueryFailedException), nonRetryable: true);
        }
    }
}
