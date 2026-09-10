using Google.Protobuf;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Contracts.Discovery;
using DomainStatus = Contracts.Domain.ExecutionStatus;
using GrpcWorkflowExecution = Temporalio.Api.Common.V1.WorkflowExecution;

namespace PatchMonitor.Services;

/// <summary>
/// Adaptador real de <see cref="IExecutionSource"/> sobre <see cref="TemporalClient"/>. Es el
/// único archivo del worker que referencia <c>Temporalio</c>: todo lo demás del descubrimiento
/// trabaja contra el puerto con DTOs propios.
///
/// <para>
/// El listado usa siempre queries <b>simples</b> de standard visibility: consultar
/// <c>TemporalChangeVersion</c> (un <c>KeywordList</c>) por query compuesta se cuelga sobre
/// Postgres (<c>Construction.md</c> §4 restricción #1). El search attribute se lee del propio
/// resultado del listado, y el flag <c>deprecated</c> del marker se resuelve leyendo la Event
/// History con la llamada gRPC cruda (<c>Temporalio</c> 1.9.0 no la expone desde el handle).
/// </para>
/// </summary>
public sealed class TemporalExecutionSource : IExecutionSource
{
    private const string PatchMarkerName = "core_patch";
    private const string PatchIdKey = "patch_id";
    private const string DeprecatedKey = "deprecated";
    private const string ChangeVersionAttribute = "TemporalChangeVersion";

    private static readonly SearchAttributeKey<IReadOnlyCollection<string>> ChangeVersionKey =
        SearchAttributeKey.CreateKeywordList(ChangeVersionAttribute);

    private readonly Lazy<Task<ITemporalClient>> _client;
    private readonly string _namespace;

    public TemporalExecutionSource(DiscoveryOptions options)
    {
        _namespace = options.Namespace;
        _client = new Lazy<Task<ITemporalClient>>(ConnectAsync);
    }

    private async Task<ITemporalClient> ConnectAsync()
    {
        var target = Environment.GetEnvironmentVariable("TARGET_TEMPORAL_HOST") ?? "temporal:7233";
        return await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
        {
            TargetHost = target,
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
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, filter.LookbackDays));
            queries.Add(
                $"ExecutionStatus != 'Running' AND StartTime > '{since:yyyy-MM-ddTHH:mm:ssZ}'");
        }

        var limit = filter.Limit > 0 ? filter.Limit : int.MaxValue;
        var items = new List<ExecutionListItem>();
        var seenRunIds = new HashSet<string>(StringComparer.Ordinal);
        var limitReached = false;

        foreach (var query in queries)
        {
            await foreach (var exec in client.ListWorkflowsAsync(query)
                .WithCancellation(ct).ConfigureAwait(false))
            {
                if (!seenRunIds.Add(exec.RunId))
                {
                    continue;
                }

                if (items.Count >= limit)
                {
                    limitReached = true;
                    break;
                }

                items.Add(ToListItem(exec));
            }

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
        var converter = DataConverter.Default.PayloadConverter;

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

                if (!TryReadPatchId(marker, converter, out var patchId))
                {
                    continue;
                }

                var deprecated = ReadDeprecated(marker, converter);
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

    private static ExecutionListItem ToListItem(WorkflowExecution exec)
    {
        IReadOnlyList<string> changeVersions = Array.Empty<string>();
        if (exec.TypedSearchAttributes.TryGetValue(ChangeVersionKey, out var values) && values is not null)
        {
            changeVersions = values.ToArray();
        }

        return new ExecutionListItem(
            exec.Id,
            exec.RunId,
            exec.WorkflowType,
            MapStatus(exec.Status),
            new DateTimeOffset(DateTime.SpecifyKind(exec.StartTime, DateTimeKind.Utc)),
            changeVersions);
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

    private static bool TryReadPatchId(
        MarkerRecordedEventAttributes marker, IPayloadConverter converter, out string patchId)
    {
        patchId = string.Empty;
        if (!marker.Details.TryGetValue(PatchIdKey, out var payloads) || payloads.Payloads_.Count == 0)
        {
            return false;
        }

        try
        {
            patchId = converter.ToValue<string>(payloads.Payloads_[0]) ?? string.Empty;
        }
        catch
        {
            return false;
        }

        return patchId.Length > 0;
    }

    private static bool ReadDeprecated(
        MarkerRecordedEventAttributes marker, IPayloadConverter converter)
    {
        if (!marker.Details.TryGetValue(DeprecatedKey, out var payloads) || payloads.Payloads_.Count == 0)
        {
            return false;
        }

        try
        {
            return converter.ToValue<bool>(payloads.Payloads_[0]);
        }
        catch
        {
            return false;
        }
    }
}
