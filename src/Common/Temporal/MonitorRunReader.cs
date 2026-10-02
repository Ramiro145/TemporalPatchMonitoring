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

        private const string MonitorWorkflowType = "MonitorWorkflow";

        // Ventana de la primera pasada (spec 15, M-8): con el tick de 5 minutos son ~288 corridas
        // por día, por debajo del tope de escaneo, así que las más recientes siempre entran.
        private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

        private readonly ResettableAsyncLazy<ITemporalClient> _client;

        public TemporalMonitorRunReader(ResettableAsyncLazy<ITemporalClient> client)
        {
            _client = client;
        }

        public async Task<IReadOnlyList<MonitorRunView>> ListRecentAsync(int limit, CancellationToken ct = default)
        {
            var client = await _client.GetValueAsync().ConfigureAwait(false);
            var scanned = new Dictionary<string, WorkflowExecution>(StringComparer.Ordinal);

            // Primera pasada: query simple por StartTime (las compuestas con WorkflowType están
            // prohibidas en la standard visibility, Construction.md §4 restricción #1). El tipo se
            // filtra en memoria.
            var examined = 0;
            await foreach (var execution in client
                .ListWorkflowsAsync(BuildWindowQuery(DateTimeOffset.UtcNow, RecentWindow))
                .WithCancellation(ct))
            {
                examined++;
                if (execution.WorkflowType == MonitorWorkflowType)
                {
                    scanned[execution.RunId] = execution;
                }

                if (examined >= MaxScanned)
                {
                    break;
                }
            }

            // Respaldo: con el Schedule pausado la ventana puede juntar pocas corridas; se
            // completa con el escaneo por WorkflowType, sin duplicar RunId.
            if (scanned.Count < limit)
            {
                await foreach (var execution in client
                    .ListWorkflowsAsync($"WorkflowType = '{MonitorWorkflowType}'")
                    .WithCancellation(ct))
                {
                    scanned.TryAdd(execution.RunId, execution);
                    if (scanned.Count >= MaxScanned)
                    {
                        break;
                    }
                }
            }

            var views = new List<MonitorRunView>(limit);
            foreach (var execution in scanned.Values.OrderByDescending(e => e.StartTime).Take(limit))
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

        /// <summary>
        /// Query simple de Visibility para las corridas de los últimos <paramref name="window"/>:
        /// <c>StartTime BETWEEN since AND until</c>, con un día de margen hacia adelante por
        /// desfasaje de reloj, igual que el listado del descubrimiento.
        /// </summary>
        public static string BuildWindowQuery(DateTimeOffset now, TimeSpan window)
        {
            var since = now.UtcDateTime - window;
            var until = now.UtcDateTime.AddDays(1);
            return $"StartTime BETWEEN '{since:yyyy-MM-ddTHH:mm:ssZ}' AND '{until:yyyy-MM-ddTHH:mm:ssZ}'";
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
