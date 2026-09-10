using Contracts.Discovery;
using Contracts.Domain;

namespace PatchMonitor.Services;

/// <summary>
/// Implementación de <see cref="IPatchDiscovery"/> en dos niveles sobre <see cref="IExecutionSource"/>.
/// Tier 1: lee el search attribute <c>TemporalChangeVersion</c> que ya viene en el listado, sin
/// query compuesta. Tier 2 (pasos siguientes de la spec): cae a la Event History donde el
/// atributo no alcanza y resuelve el flag <c>deprecated</c>. Agrupa las ejecuciones por
/// <see cref="PatchKey"/> (namespace de <see cref="DiscoveryOptions"/> + workflowType de la
/// ejecución + patchId).
/// </summary>
public sealed class PatchDiscoveryService : IPatchDiscovery
{
    private readonly IExecutionSource _source;
    private readonly DiscoveryOptions _options;

    public PatchDiscoveryService(IExecutionSource source, DiscoveryOptions options)
    {
        _source = source;
        _options = options;
    }

    public async Task<IReadOnlyList<PatchDiscoveryResult>> DiscoverAsync(CancellationToken ct = default)
    {
        var filter = new ExecutionListFilter(
            OpenOnly: false, _options.LookbackDays, _options.MaxExecutions);
        var page = await _source.ListExecutionsAsync(filter, ct).ConfigureAwait(false);

        var byKey = new Dictionary<PatchKey, List<ExecutionSnapshot>>();

        foreach (var item in page.Items)
        {
            // Tier 1: cada entrada del search attribute declara un patchId presente en esta
            // ejecución. El flag deprecated no viaja en el atributo ⇒ MarkerPresence.Present.
            foreach (var patchId in PatchIdsFromAttribute(item.ChangeVersions))
            {
                var key = new PatchKey(_options.Namespace, item.WorkflowType, patchId);
                AddSnapshot(byKey, key, ToSnapshot(item, MarkerPresence.Present));
            }
        }

        return byKey
            .Select(kv => new PatchDiscoveryResult(
                kv.Key, new ExecutionSnapshotSet(kv.Value, IsTruncated: false)))
            .ToArray();
    }

    private static IEnumerable<string> PatchIdsFromAttribute(IReadOnlyList<string> changeVersions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in changeVersions)
        {
            var patchId = ParsePatchId(entry);
            if (patchId.Length > 0 && seen.Add(patchId))
            {
                yield return patchId;
            }
        }
    }

    /// <summary>
    /// Una entrada de <c>TemporalChangeVersion</c> es <c>&lt;patchId&gt;-&lt;version&gt;</c>.
    /// El patchId puede llevar guiones, así que se quita solo el sufijo <c>-&lt;dígitos&gt;</c>
    /// final. Sin ese sufijo, la entrada entera es el patchId.
    /// </summary>
    private static string ParsePatchId(string entry)
    {
        var dash = entry.LastIndexOf('-');
        if (dash <= 0 || dash == entry.Length - 1)
        {
            return entry;
        }

        var suffix = entry.AsSpan(dash + 1);
        foreach (var c in suffix)
        {
            if (!char.IsDigit(c))
            {
                return entry;
            }
        }

        return entry[..dash];
    }

    private static ExecutionSnapshot ToSnapshot(ExecutionListItem item, MarkerPresence marker) =>
        new(item.WorkflowId, item.RunId, item.WorkflowType, item.Status, marker, item.StartTime);

    private static void AddSnapshot(
        Dictionary<PatchKey, List<ExecutionSnapshot>> byKey, PatchKey key, ExecutionSnapshot snapshot)
    {
        if (!byKey.TryGetValue(key, out var list))
        {
            byKey[key] = list = new List<ExecutionSnapshot>();
        }

        list.Add(snapshot);
    }
}
