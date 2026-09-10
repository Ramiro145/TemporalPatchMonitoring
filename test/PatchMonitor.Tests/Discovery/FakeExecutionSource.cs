using Contracts.Discovery;
using Contracts.Domain;

namespace PatchMonitor.Tests.Discovery;

/// <summary>
/// <see cref="IExecutionSource"/> en memoria para los tests de <c>PatchDiscoveryService</c>.
/// Se siembra con <see cref="ExecutionFixture"/> y registra qué filtros se listaron y qué
/// historias se leyeron, para poder afirmar sobre los topes y el fallback a tier 2 sin
/// levantar un cluster.
/// </summary>
public sealed class FakeExecutionSource : IExecutionSource
{
    private readonly List<ExecutionFixture> _fixtures = new();
    private bool _forceLimitReached;

    /// <summary>Filtros con los que se invocó <see cref="ListExecutionsAsync"/>, en orden.</summary>
    public List<ExecutionListFilter> ListCalls { get; } = new();

    /// <summary>Pares <c>(workflowId, runId)</c> cuya historia se leyó, en orden.</summary>
    public List<(string WorkflowId, string RunId)> HistoryReads { get; } = new();

    /// <summary>Cantidad total de lecturas de historia (tier 2).</summary>
    public int HistoryReadCount => HistoryReads.Count;

    public FakeExecutionSource Seed(params ExecutionFixture[] fixtures)
    {
        _fixtures.AddRange(fixtures);
        return this;
    }

    /// <summary>Fuerza <see cref="ExecutionListPage.LimitReached"/> a <c>true</c> pase lo que pase.</summary>
    public FakeExecutionSource ForceLimitReached()
    {
        _forceLimitReached = true;
        return this;
    }

    public Task<ExecutionListPage> ListExecutionsAsync(
        ExecutionListFilter filter, CancellationToken ct = default)
    {
        ListCalls.Add(filter);

        var matching = _fixtures
            .Select(f => f.Item)
            .Where(item => !filter.OpenOnly || item.Status.IsOpen())
            .ToArray();

        var limit = filter.Limit > 0 ? filter.Limit : matching.Length;
        var page = matching.Take(limit).ToArray();
        var limitReached = _forceLimitReached || matching.Length > limit;

        return Task.FromResult(new ExecutionListPage(page, limitReached));
    }

    public Task<IReadOnlyList<PatchMarker>> ReadPatchMarkersAsync(
        string workflowId, string runId, CancellationToken ct = default)
    {
        HistoryReads.Add((workflowId, runId));

        var fixture = _fixtures.FirstOrDefault(f =>
            f.Item.WorkflowId == workflowId && f.Item.RunId == runId);

        if (fixture?.HistoryError is { } error)
        {
            return Task.FromException<IReadOnlyList<PatchMarker>>(error);
        }

        IReadOnlyList<PatchMarker> markers = fixture?.History ?? Array.Empty<PatchMarker>();
        return Task.FromResult(markers);
    }
}
