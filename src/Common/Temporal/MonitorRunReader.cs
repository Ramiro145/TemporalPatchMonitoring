using Contracts.Monitor;
using Temporalio.Client;

namespace Common.Temporal
{
    /// <summary>
    /// Implementación de <see cref="IMonitorRunReader"/> sobre
    /// <see cref="ITemporalClient.ListWorkflowsAsync(string, WorkflowListOptions?)"/>, mismo
    /// patrón que <see cref="TemporalScheduleController"/>: consulta Visibility del cluster
    /// propio del monitor, nunca el observado.
    /// </summary>
    public sealed class TemporalMonitorRunReader : IMonitorRunReader
    {
        // La visibility "standard" (Postgres/SQLite sin ElasticSearch, la que trae el
        // docker-compose de este repo) rechaza `ORDER BY` en la query de List con
        // "order by not allowed for standard visibility" (verificado contra el stack real en el
        // spec 11). Se ordena en memoria en su lugar, acotando el escaneo con el mismo criterio
        // que DiscoveryOptions.MaxExecutions (tope defensivo, nunca ilimitado).
        private const int MaxScanned = 500;

        private readonly Lazy<Task<ITemporalClient>> _client;

        public TemporalMonitorRunReader(Lazy<Task<ITemporalClient>> client)
        {
            _client = client;
        }

        public async Task<IReadOnlyList<MonitorRunView>> ListRecentAsync(int limit, CancellationToken ct = default)
        {
            var client = await _client.Value.ConfigureAwait(false);
            var scanned = new List<WorkflowExecution>(Math.Min(limit, MaxScanned));

            await foreach (var execution in client
                .ListWorkflowsAsync("WorkflowType = 'MonitorWorkflow'")
                .WithCancellation(ct))
            {
                scanned.Add(execution);
                if (scanned.Count >= MaxScanned)
                {
                    break;
                }
            }

            var views = new List<MonitorRunView>(limit);
            foreach (var execution in scanned.OrderByDescending(e => e.StartTime).Take(limit))
            {
                views.Add(new MonitorRunView(
                    execution.Id,
                    execution.RunId,
                    execution.StartTime,
                    execution.CloseTime,
                    execution.Status.ToString(),
                    await TryGetSummaryAsync(client, execution).ConfigureAwait(false)));
            }

            return views;
        }

        // Solo se intenta para corridas que cerraron con éxito; cualquier error de
        // deserialización o historia purgada se traga devolviendo null, nunca propaga.
        private static async Task<MonitorRunSummary?> TryGetSummaryAsync(
            ITemporalClient client, WorkflowExecution execution)
        {
            if (execution.Status != Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Completed)
            {
                return null;
            }

            try
            {
                var handle = client.GetWorkflowHandle(execution.Id, execution.RunId);
                return await handle.GetResultAsync<MonitorRunSummary>(followRuns: false).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
