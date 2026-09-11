using System.Text.Json;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Contracts.Discovery;
using DomainStatus = Contracts.Domain.ExecutionStatus;
using GrpcWorkflowExecution = Temporalio.Api.Common.V1.WorkflowExecution;

namespace PatchMonitor.Services;

/// <summary>
/// Adaptador real de <see cref="IExecutionSource"/> sobre <see cref="TemporalClient"/>. Es el
/// único archivo del descubrimiento que referencia <c>Temporalio</c>: todo lo demás trabaja
/// contra el puerto con DTOs propios.
///
/// <para>
/// Usa las llamadas gRPC crudas <c>WorkflowService.ListWorkflowExecutions</c> y
/// <c>GetWorkflowExecutionHistory</c>: el wrapper de alto nivel del SDK 1.9.0 no expone la
/// historia desde el handle ni deja ver los search attributes de sistema como
/// <c>TemporalChangeVersion</c> en el resultado del listado. Las queries son siempre
/// <b>simples</b> (la standard visibility del repo de referencia rechaza <c>!=</c>, exige
/// <c>StartTime BETWEEN</c> y se cuelga en compuestas sobre <c>TemporalChangeVersion</c> —
/// <c>Construction.md</c> §4 restricción #1).
/// </para>
/// </summary>
public sealed class TemporalExecutionSource : IExecutionSource
{
    private const string PatchMarkerName = "core_patch";
    private const string PatchDataKey = "patch-data";
    private const string ChangeVersionAttribute = "TemporalChangeVersion";
    private const int PageSize = 100;

    private readonly Lazy<Task<ITemporalClient>> _client;
    private readonly string _namespace;
    private readonly string _targetHost;

    public TemporalExecutionSource(DiscoveryOptions options)
    {
        _namespace = options.Namespace;
        _targetHost = options.TargetHost;
        _client = new Lazy<Task<ITemporalClient>>(ConnectAsync);
    }

    private async Task<ITemporalClient> ConnectAsync()
    {
        return await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
        {
            TargetHost = _targetHost,
            Namespace = _namespace,
        }).ConfigureAwait(false);
    }

    public async Task<ExecutionListPage> ListExecutionsAsync(
        ExecutionListFilter filter, CancellationToken ct = default)
    {
        var client = await _client.Value.ConfigureAwait(false);

        var queries = new List<string> { "ExecutionStatus = 'Running'" };
        if (!filter.OpenOnly)
        {
            var now = DateTimeOffset.UtcNow;
            var since = now.AddDays(-Math.Max(1, filter.LookbackDays));
            var until = now.AddDays(1); // margen por desfasaje de reloj
            queries.Add(
                $"StartTime BETWEEN '{since:yyyy-MM-ddTHH:mm:ssZ}' AND '{until:yyyy-MM-ddTHH:mm:ssZ}'");
        }

        var limit = filter.Limit > 0 ? filter.Limit : int.MaxValue;
        var items = new List<ExecutionListItem>();
        var seenRunIds = new HashSet<string>(StringComparer.Ordinal);
        var limitReached = false;

        foreach (var query in queries)
        {
            var pageToken = ByteString.Empty;
            do
            {
                ct.ThrowIfCancellationRequested();

                var response = await client.WorkflowService.ListWorkflowExecutionsAsync(
                    new ListWorkflowExecutionsRequest
                    {
                        Namespace = client.Options.Namespace,
                        PageSize = PageSize,
                        Query = query,
                        NextPageToken = pageToken,
                    }).ConfigureAwait(false);

                foreach (var info in response.Executions)
                {
                    if (!seenRunIds.Add(info.Execution.RunId))
                    {
                        continue;
                    }

                    if (items.Count >= limit)
                    {
                        limitReached = true;
                        break;
                    }

                    items.Add(ToListItem(info));
                }

                pageToken = response.NextPageToken;
            }
            while (!limitReached && !pageToken.IsEmpty);

            if (limitReached)
            {
                break;
            }
        }

        return new ExecutionListPage(items, limitReached);
    }

    public async Task<IReadOnlyList<PatchMarker>> ReadPatchMarkersAsync(
        string workflowId, string runId, CancellationToken ct = default)
    {
        var client = await _client.Value.ConfigureAwait(false);

        // deprecated es "pegajoso": una vez que una ejecución ve el flag puesto, se queda así.
        var deprecatedByPatch = new Dictionary<string, bool>(StringComparer.Ordinal);
        var pageToken = ByteString.Empty;

        do
        {
            ct.ThrowIfCancellationRequested();

            var response = await client.WorkflowService.GetWorkflowExecutionHistoryAsync(
                new GetWorkflowExecutionHistoryRequest
                {
                    Namespace = client.Options.Namespace,
                    Execution = new GrpcWorkflowExecution
                    {
                        WorkflowId = workflowId,
                        RunId = runId ?? string.Empty,
                    },
                    NextPageToken = pageToken,
                }).ConfigureAwait(false);

            foreach (var evt in response.History.Events)
            {
                var marker = evt.MarkerRecordedEventAttributes;
                if (marker is null || marker.MarkerName != PatchMarkerName)
                {
                    continue;
                }

                if (!TryReadPatchData(marker, out var patchId, out var deprecated))
                {
                    continue;
                }

                deprecatedByPatch[patchId] =
                    deprecatedByPatch.TryGetValue(patchId, out var previous)
                        ? previous || deprecated
                        : deprecated;
            }

            pageToken = response.NextPageToken;
        }
        while (!pageToken.IsEmpty);

        return deprecatedByPatch
            .Select(kv => new PatchMarker(kv.Key, kv.Value))
            .ToArray();
    }

    private static ExecutionListItem ToListItem(Temporalio.Api.Workflow.V1.WorkflowExecutionInfo info)
    {
        return new ExecutionListItem(
            info.Execution.WorkflowId,
            info.Execution.RunId,
            info.Type.Name,
            MapStatus(info.Status),
            info.StartTime?.ToDateTimeOffset() ?? default,
            ReadChangeVersions(info.SearchAttributes));
    }

    /// <summary>
    /// El search attribute <c>TemporalChangeVersion</c> es un <c>KeywordList</c>: en la
    /// respuesta gRPC llega como un payload <c>json/plain</c> con un array de strings
    /// (<c>["&lt;patchId&gt;", ...]</c>). Vacío o ilegible ⇒ lista vacía (la ejecución cae a tier 2).
    /// </summary>
    private static IReadOnlyList<string> ReadChangeVersions(SearchAttributes? searchAttributes)
    {
        if (searchAttributes is null ||
            !searchAttributes.IndexedFields.TryGetValue(ChangeVersionAttribute, out var payload))
        {
            return Array.Empty<string>();
        }

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(payload.Data.Span);
            return values is { Length: > 0 } ? values : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// El marker <c>core_patch</c> del sdk-core lleva un único detail <c>patch-data</c> con un
    /// payload <c>json/plain</c> de la forma <c>{"id":"&lt;patchId&gt;","deprecated":&lt;bool&gt;}</c>.
    /// </summary>
    private static bool TryReadPatchData(
        MarkerRecordedEventAttributes marker, out string patchId, out bool deprecated)
    {
        patchId = string.Empty;
        deprecated = false;

        if (!marker.Details.TryGetValue(PatchDataKey, out var payloads) ||
            payloads.Payloads_.Count == 0)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloads.Payloads_[0].Data.Memory);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                patchId = id.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("deprecated", out var dep) &&
                (dep.ValueKind == JsonValueKind.True || dep.ValueKind == JsonValueKind.False))
            {
                deprecated = dep.GetBoolean();
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return patchId.Length > 0;
    }

    private static DomainStatus MapStatus(WorkflowExecutionStatus status) => status switch
    {
        WorkflowExecutionStatus.Running => DomainStatus.Running,
        WorkflowExecutionStatus.Completed => DomainStatus.Completed,
        WorkflowExecutionStatus.Failed => DomainStatus.Failed,
        WorkflowExecutionStatus.Canceled => DomainStatus.Canceled,
        WorkflowExecutionStatus.Terminated => DomainStatus.Terminated,
        WorkflowExecutionStatus.ContinuedAsNew => DomainStatus.ContinuedAsNew,
        WorkflowExecutionStatus.TimedOut => DomainStatus.TimedOut,
        _ => DomainStatus.Unknown,
    };
}
