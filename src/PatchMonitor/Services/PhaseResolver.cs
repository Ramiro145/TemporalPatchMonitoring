using Contracts.Discovery;
using Contracts.Domain;
using Contracts.Phase;

namespace PatchMonitor.Services;

/// <summary>
/// Implementación de <see cref="IPhaseResolver"/>: interpreta los markers de los snapshots del
/// spec 03 como una fase del ciclo de vida del patch. Cómputo puro, sin <c>using Temporalio</c>
/// y sin I/O contra el cluster.
/// </summary>
/// <remarks>
/// Un redeploy que ya pasó a <c>DeprecatePatch</c> pero todavía no arrancó ninguna ejecución
/// nueva se sigue leyendo como fase 1: no hay evidencia aún del código nuevo. Es correcto por
/// diseño; el operador puede forzar fase 2 con un <see cref="PhaseOverride"/>.
/// </remarks>
public sealed class PhaseResolver : IPhaseResolver
{
    private readonly IPhaseOverrideStore _overrides;
    private readonly PhaseOptions _options;
    private readonly TimeProvider _clock;

    public PhaseResolver(IPhaseOverrideStore overrides, PhaseOptions options, TimeProvider clock)
    {
        _overrides = overrides;
        _options = options;
        _clock = clock;
    }

    public PhaseResolution Resolve(PatchDiscoveryResult result)
    {
        var now = _clock.GetUtcNow();
        return Infer(result, now);
    }

    /// <summary>
    /// Casos 2 a 6 de la tabla de decisión del spec 04. Gana el primero que aplica.
    /// </summary>
    private PhaseResolution Infer(PatchDiscoveryResult result, DateTimeOffset now)
    {
        var snaps = result.Executions.Snapshots;

        // Caso 2: sin evidencia de marker (conjunto vacío o todo Unknown).
        if (snaps.Count == 0 || snaps.All(s => s.Marker == MarkerPresence.Unknown))
        {
            return Inferred(PatchPhase.Unknown, "sin evidencia de marker", now);
        }

        var withMarker = snaps.Where(HasMarker).ToList();

        // Caso 3: código limpio. Exige AMBAS condiciones: que no quede ninguna ejecución
        // ABIERTA con marker (si queda, el patch sigue en fase 2) y evidencia POSITIVA de
        // código nuevo — una ejecución sin marker arrancada más de CleanGrace después del
        // último marker. Sin la segunda condición, un patch cuyas ejecuciones con marker
        // simplemente drenaron se leería como Clean de más.
        if (withMarker.Count > 0 && !withMarker.Any(s => s.Status.IsOpen()))
        {
            var lastMarkerStart = withMarker.Max(s => s.StartTime);
            var cutoff = lastMarkerStart + _options.CleanGrace;

            if (snaps.Any(s => s.Marker == MarkerPresence.Absent && s.StartTime > cutoff))
            {
                return Inferred(
                    PatchPhase.Clean,
                    $"sin marker desde {lastMarkerStart:o}; código limpio",
                    now);
            }
        }

        if (withMarker.Count > 0)
        {
            var newest = NewestWithMarker(withMarker);

            // Caso 4: el marker más reciente está deprecado.
            // Caso 5: el marker más reciente no está deprecado.
            return newest.Marker == MarkerPresence.PresentDeprecated
                ? Inferred(PatchPhase.Deprecated, "marker más reciente deprecado", now)
                : Inferred(PatchPhase.Coexistence, "marker más reciente sin deprecar", now);
        }

        // Caso 6: hay ejecuciones pero ninguna lleva el marker (todas Absent).
        return Inferred(PatchPhase.Unknown, "ninguna ejecución lleva el marker", now);
    }

    /// <summary>"Con marker" = <see cref="MarkerPresence.Present"/> o <see cref="MarkerPresence.PresentDeprecated"/>.</summary>
    private static bool HasMarker(ExecutionSnapshot s) =>
        s.Marker is MarkerPresence.Present or MarkerPresence.PresentDeprecated;

    /// <summary>
    /// La ejecución con marker de <c>StartTime</c> máximo. En empate exacto de <c>StartTime</c>
    /// entre una <see cref="MarkerPresence.Present"/> y una <see cref="MarkerPresence.PresentDeprecated"/>,
    /// gana la deprecada: el ciclo solo avanza, nunca retrocede.
    /// </summary>
    private static ExecutionSnapshot NewestWithMarker(IEnumerable<ExecutionSnapshot> withMarker) =>
        withMarker
            .OrderByDescending(s => s.StartTime)
            .ThenByDescending(s => s.Marker == MarkerPresence.PresentDeprecated)
            .First();

    private static PhaseResolution Inferred(PatchPhase phase, string reason, DateTimeOffset now) =>
        new(phase, PhaseSource.Inferred, reason, now);
}
