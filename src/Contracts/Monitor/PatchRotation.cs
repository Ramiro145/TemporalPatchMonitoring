using Contracts.Discovery;

namespace Contracts.Monitor;

/// <summary>
/// Elige qué patches evalúa una pasada de <c>MonitorWorkflow</c> cuando hay más descubiertos que
/// <see cref="MonitorOptions.MaxPatchesPerRun"/> (spec 15, M-9). Orden estable por
/// <see cref="Domain.PatchKey"/> y una ventana rotativa derivada del número de tick, sin estado
/// nuevo: ticks consecutivos recorren la lista completa en <c>ceil(count / max)</c> pasadas, así
/// ningún patch queda sin evaluarse indefinidamente. Función pura y determinística.
/// </summary>
public static class PatchRotation
{
    public static IReadOnlyList<PatchDiscoveryResult> Select(
        IReadOnlyList<PatchDiscoveryResult> discovered, int maxPerRun, long tickIndex)
    {
        if (maxPerRun <= 0 || discovered.Count == 0)
        {
            return Array.Empty<PatchDiscoveryResult>();
        }

        var ordered = discovered
            .OrderBy(d => d.Key.Namespace, StringComparer.Ordinal)
            .ThenBy(d => d.Key.WorkflowType, StringComparer.Ordinal)
            .ThenBy(d => d.Key.PatchId, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length <= maxPerRun)
        {
            return ordered;
        }

        // (tick * max) % count sin desbordar: se reduce cada factor módulo count antes.
        var count = (long)ordered.Length;
        var start = (int)(Math.Abs(tickIndex % count) * (maxPerRun % count) % count);

        var selected = new PatchDiscoveryResult[maxPerRun];
        for (var i = 0; i < maxPerRun; i++)
        {
            selected[i] = ordered[(start + i) % ordered.Length];
        }

        return selected;
    }
}
