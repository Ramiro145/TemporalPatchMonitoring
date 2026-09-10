using Contracts.Discovery;
using Contracts.Domain;

namespace PatchMonitor.Services;

/// <summary>
/// Implementación de <see cref="IPatchDiscovery"/> en dos niveles sobre <see cref="IExecutionSource"/>.
/// Tier 1: lee el search attribute <c>TemporalChangeVersion</c> que ya viene en el listado, sin
/// query compuesta; garantiza que un patch se descubra aunque su Event History no se pueda leer.
/// Tier 2: cae a la Event History para resolver el flag <c>deprecated</c> del marker y para
/// descubrir patches que el atributo no trae. Agrupa por <see cref="PatchKey"/> (namespace de
/// <see cref="DiscoveryOptions"/> + workflowType de la ejecución + patchId).
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

        // Un snapshot por (PatchKey, ejecución). runId identifica la ejecución dentro del patch.
        var byKey = new Dictionary<PatchKey, Dictionary<string, ExecutionSnapshot>>();

        // Ejecuciones sin patch propio (ni atributo ni markers): se atribuyen después, como
        // Absent (pre-patch) o Unknown (historia no leída), a cada patch de su mismo workflowType.
        var floating = new List<(ExecutionListItem Item, MarkerPresence Presence)>();

        foreach (var item in page.Items)
        {
            var attributePatchIds = PatchIdsFromAttribute(item.ChangeVersions);
            var (markers, historyReadable) = await ReadHistoryAsync(item, ct).ConfigureAwait(false);

            if (markers.Count > 0)
            {
                // Tier 2: los markers de la historia mandan sobre presencia y flag deprecated.
                foreach (var marker in markers)
                {
                    var presence = marker.Deprecated
                        ? MarkerPresence.PresentDeprecated
                        : MarkerPresence.Present;
                    Put(byKey, Key(item, marker.PatchId), item, presence);
                }
            }
            else if (attributePatchIds.Count > 0)
            {
                // Tier 1: el atributo declara el patch pero la historia no aportó markers
                // (vacía o no legible). Se confía en el atributo ⇒ Present, nunca Unknown.
                foreach (var patchId in attributePatchIds)
                {
                    Put(byKey, Key(item, patchId), item, MarkerPresence.Present);
                }
            }
            else
            {
                floating.Add((item, historyReadable ? MarkerPresence.Absent : MarkerPresence.Unknown));
            }
        }

        AttributeFloating(byKey, floating);

        return byKey
            .Select(kv => new PatchDiscoveryResult(
                kv.Key,
                new ExecutionSnapshotSet(kv.Value.Values.ToArray(), IsTruncated: false)))
            .ToArray();
    }

    /// <summary>
    /// Lee la Event History de una ejecución. Devuelve la lista de markers <c>core_patch</c> y
    /// si la lectura fue completa: una excepción deja <c>historyReadable = false</c> y la lista
    /// vacía, para que el llamador trate la ejecución como <see cref="MarkerPresence.Unknown"/>.
    /// </summary>
    private async Task<(IReadOnlyList<PatchMarker> Markers, bool HistoryReadable)> ReadHistoryAsync(
        ExecutionListItem item, CancellationToken ct)
    {
        try
        {
            var markers = await _source
                .ReadPatchMarkersAsync(item.WorkflowId, item.RunId, ct)
                .ConfigureAwait(false);
            return (markers, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (Array.Empty<PatchMarker>(), false);
        }
    }

    private PatchKey Key(ExecutionListItem item, string patchId) =>
        new(_options.Namespace, item.WorkflowType, patchId);

    private static void AttributeFloating(
        Dictionary<PatchKey, Dictionary<string, ExecutionSnapshot>> byKey,
        IReadOnlyList<(ExecutionListItem Item, MarkerPresence Presence)> floating)
    {
        if (floating.Count == 0 || byKey.Count == 0)
        {
            return;
        }

        foreach (var (item, presence) in floating)
        {
            foreach (var key in byKey.Keys.Where(k => k.WorkflowType == item.WorkflowType).ToArray())
            {
                Put(byKey, key, item, presence, overwrite: false);
            }
        }
    }

    private static void Put(
        Dictionary<PatchKey, Dictionary<string, ExecutionSnapshot>> byKey,
        PatchKey key,
        ExecutionListItem item,
        MarkerPresence presence,
        bool overwrite = true)
    {
        if (!byKey.TryGetValue(key, out var executions))
        {
            byKey[key] = executions = new Dictionary<string, ExecutionSnapshot>(StringComparer.Ordinal);
        }

        if (!overwrite && executions.ContainsKey(item.RunId))
        {
            return;
        }

        executions[item.RunId] = new ExecutionSnapshot(
            item.WorkflowId, item.RunId, item.WorkflowType, item.Status, presence, item.StartTime);
    }

    private static IReadOnlyList<string> PatchIdsFromAttribute(IReadOnlyList<string> changeVersions)
    {
        if (changeVersions.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var entry in changeVersions)
        {
            var patchId = ParsePatchId(entry);
            if (patchId.Length > 0 && seen.Add(patchId))
            {
                result.Add(patchId);
            }
        }

        return result;
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

        foreach (var c in entry.AsSpan(dash + 1))
        {
            if (!char.IsDigit(c))
            {
                return entry;
            }
        }

        return entry[..dash];
    }
}
