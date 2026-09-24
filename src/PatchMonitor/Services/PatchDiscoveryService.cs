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
/// <remarks>
/// Spec 13: una ejecución se atribuye como <see cref="MarkerPresence.Absent"/> (o
/// <see cref="MarkerPresence.Unknown"/> si su historia no se pudo leer) a cada
/// <see cref="PatchKey"/> ya descubierto de su <c>workflowType</c> para el cual esa ejecución no
/// trajo marker propio — no solo a las ejecuciones sin ningún marker de ningún patch. Así un
/// <c>workflowType</c> con varios patches activos a la vez puede seguir infiriendo que uno de
/// ellos ya está limpio, aunque los demás sigan emitiendo su marker.
/// </remarks>
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

        // Toda ejecución del barrido, con los patchId que trajo marker propio (por atributo o
        // historia) y si su historia se pudo leer completa. Sirve para la segunda pasada, que
        // atribuye Absent/Unknown a los patches hermanos que esa ejecución no cubrió.
        var allItems = new List<(ExecutionListItem Item, HashSet<string> OwnPatchIds, bool HistoryReadable)>();

        var historiesRead = 0;

        foreach (var item in page.Items)
        {
            var attributePatchIds = PatchIdsFromAttribute(item.ChangeVersions);
            var ownPatchIds = new HashSet<string>(StringComparer.Ordinal);

            IReadOnlyList<PatchMarker> markers;
            bool historyReadable;
            if (historiesRead < _options.MaxHistories)
            {
                historiesRead++;
                (markers, historyReadable) = await ReadHistoryAsync(item, ct).ConfigureAwait(false);
            }
            else
            {
                // Tope MaxHistories agotado: la ejecución queda sin inspeccionar.
                (markers, historyReadable) = (Array.Empty<PatchMarker>(), false);
            }

            if (markers.Count > 0)
            {
                // Tier 2: los markers de la historia mandan sobre presencia y flag deprecated.
                foreach (var marker in markers)
                {
                    var presence = marker.Deprecated
                        ? MarkerPresence.PresentDeprecated
                        : MarkerPresence.Present;
                    Put(byKey, Key(item, marker.PatchId), item, presence);
                    ownPatchIds.Add(marker.PatchId);
                }
            }
            else if (attributePatchIds.Count > 0)
            {
                // Tier 1: el atributo declara el patch pero la historia no aportó markers
                // (vacía o no legible). Se confía en el atributo ⇒ Present, nunca Unknown.
                foreach (var patchId in attributePatchIds)
                {
                    Put(byKey, Key(item, patchId), item, MarkerPresence.Present);
                    ownPatchIds.Add(patchId);
                }
            }

            allItems.Add((item, ownPatchIds, historyReadable));
        }

        AttributeAbsence(byKey, allItems);

        return byKey
            .Select(kv => new PatchDiscoveryResult(
                kv.Key,
                new ExecutionSnapshotSet(kv.Value.Values.ToArray(), IsTruncated(page, kv.Value.Values))))
            .ToArray();
    }

    /// <summary>
    /// Un patch sale truncado si el listado topeó en <c>MaxExecutions</c> (<c>LimitReached</c>,
    /// que afecta a todos los patches por igual) o si alguna de sus ejecuciones quedó en
    /// <see cref="MarkerPresence.Unknown"/> —por el tope <c>MaxHistories</c> o por una lectura
    /// de historia fallida—. Los gates del spec 02 traducen ese <c>IsTruncated</c> a
    /// <c>Inconclusive</c> en vez de un <c>Ready</c> falso.
    /// </summary>
    private static bool IsTruncated(ExecutionListPage page, IEnumerable<ExecutionSnapshot> executions) =>
        page.LimitReached || executions.Any(s => s.Marker == MarkerPresence.Unknown);

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

    /// <summary>
    /// Segunda pasada (spec 13): cada ejecución del barrido se atribuye como
    /// <see cref="MarkerPresence.Absent"/> (o <see cref="MarkerPresence.Unknown"/> si su historia
    /// no se pudo leer) a todo <see cref="PatchKey"/> ya descubierto de su <c>workflowType</c>
    /// para el que esa ejecución no aportó marker propio. <c>overwrite: false</c> asegura que un
    /// marker propio (<c>Present</c>/<c>PresentDeprecated</c>) puesto en la primera pasada nunca
    /// se pise acá, y el filtro <c>!OwnPatchIds.Contains</c> evita attribuir ausencia al propio
    /// patch de la ejecución. Corre después de que todas las ejecuciones pasaron por la primera
    /// pasada, así <c>byKey</c> ya tiene el set completo de patches de este barrido — nunca crea
    /// un <see cref="PatchKey"/> nuevo por ausencia.
    /// </summary>
    private static void AttributeAbsence(
        Dictionary<PatchKey, Dictionary<string, ExecutionSnapshot>> byKey,
        IReadOnlyList<(ExecutionListItem Item, HashSet<string> OwnPatchIds, bool HistoryReadable)> allItems)
    {
        if (byKey.Count == 0)
        {
            return;
        }

        foreach (var (item, ownPatchIds, historyReadable) in allItems)
        {
            var presence = historyReadable ? MarkerPresence.Absent : MarkerPresence.Unknown;

            foreach (var key in byKey.Keys
                .Where(k => k.WorkflowType == item.WorkflowType && !ownPatchIds.Contains(k.PatchId))
                .ToArray())
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
