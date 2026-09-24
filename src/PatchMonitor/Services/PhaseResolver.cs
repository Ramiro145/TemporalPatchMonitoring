using Contracts.Discovery;
using Contracts.Domain;
using Contracts.Phase;

namespace PatchMonitor.Services;

/// <summary>
/// Implementación de <see cref="IPhaseResolver"/>: interpreta los markers de los snapshots del
/// spec 03 como una fase del ciclo de vida del patch. Cómputo puro, sin acoplarse al SDK de
/// Temporal y sin I/O contra el cluster.
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
        var inferred = Infer(result, now);

        // Caso 1: un override vigente del operador gana siempre, sin validarse contra la fase
        // inferida ni contra PhaseTransition.IsLegal (eso es del spec 08, al escribirlo por
        // HTTP). La inferida viaja en el Reason para que el desacuerdo sea visible. El store ya
        // descarta los vencidos contra DateTimeOffset.UtcNow; acá se vuelve a chequear con el
        // reloj inyectado, que es el autoritativo, con IsActiveAt(now).
        var ov = _overrides.Get(result.Key);
        if (ov is not null && ov.IsActiveAt(now))
        {
            return new PhaseResolution(
                ov.Phase,
                PhaseSource.Override,
                $"override de {ov.DeclaredBy}; la inferida era {inferred.Phase}",
                now);
        }

        return inferred;
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

        // Caso 3: código limpio. Exige (spec 04 + Capa 1 y Capa 2 del spec 13):
        // - Que no quede ninguna ejecución ABIERTA con marker (si queda, el patch sigue en fase 2).
        // - Capa 1 (no saltar fases): al menos un marker PresentDeprecated en la ventana — un
        //   patch que nunca pasó por Deprecated no puede saltar directo a Clean.
        // - Capa 2 (evidencia mínima adaptativa): ver EnoughAbsentEvidence. Sin esto, un patch
        //   cuyas ejecuciones con marker simplemente drenaron, o que vive en una rama de código
        //   poco ejercida, se leería como Clean de más ("falso Clean").
        if (withMarker.Count > 0
            && !withMarker.Any(s => s.Status.IsOpen())
            && withMarker.Any(s => s.Marker == MarkerPresence.PresentDeprecated))
        {
            var lastMarkerStart = withMarker.Max(s => s.StartTime);
            var cutoff = lastMarkerStart + _options.CleanGrace;

            var (isClean, requiredAbsences, p, seenAbsences) =
                EnoughAbsentEvidence(snaps, cutoff, _options.CleanConfidence);

            if (isClean)
            {
                return Inferred(
                    PatchPhase.Clean,
                    $"sin marker desde {lastMarkerStart:o}; código limpio",
                    now);
            }

            var newestPending = NewestWithMarker(withMarker);
            var missing = requiredAbsences - seenAbsences;
            return newestPending.Marker == MarkerPresence.PresentDeprecated
                ? Inferred(
                    PatchPhase.Deprecated,
                    $"faltan {missing} ejecuciones sin marker para confirmar Clean "
                        + $"(p={p:0.00}, N={requiredAbsences}, vistas={seenAbsences})",
                    now)
                : Inferred(
                    PatchPhase.Coexistence,
                    $"faltan {missing} ejecuciones sin marker para confirmar Clean "
                        + $"(p={p:0.00}, N={requiredAbsences}, vistas={seenAbsences})",
                    now);
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
    /// Capa 2 del spec 13: cuántas ejecuciones <see cref="MarkerPresence.Absent"/> arrancadas
    /// después de <paramref name="cutoff"/> hacen falta para confirmar Clean, y si ya se
    /// juntaron. <c>p</c> es la tasa de aparición del marker en la ventana observada — cuántas de
    /// las ejecuciones con evidencia (con marker o <c>Absent</c>) arrancadas hasta el
    /// <paramref name="cutoff"/> traían el marker propio. Cuanto más rara es la aparición
    /// (<c>p</c> chico, patch en una rama de código poco ejercida), más ejecuciones limpias
    /// seguidas hacen falta para que el azar de "no pasó por esa rama" sea improbable con la
    /// <paramref name="confidence"/> pedida. Con <c>p = 1</c> (todas las ejecuciones previas
    /// traían el marker, el caso común) da <c>N = 1</c>, igual que el comportamiento previo al
    /// spec 13. <c>p</c> nunca puede ser 0 acá: <paramref name="cutoff"/> es posterior al
    /// <c>StartTime</c> de toda ejecución con marker, así que esas ejecuciones siempre entran en
    /// el numerador y el denominador.
    /// </summary>
    private static (bool IsClean, int RequiredAbsences, double P, int SeenAbsences) EnoughAbsentEvidence(
        IReadOnlyList<ExecutionSnapshot> snaps, DateTimeOffset cutoff, double confidence)
    {
        var withEvidence = snaps.Where(s => s.StartTime <= cutoff && HasMarkerOrAbsent(s)).ToList();
        var withMarkerBeforeCutoff = withEvidence.Count(HasMarker);
        var p = (double)withMarkerBeforeCutoff / withEvidence.Count;

        var requiredAbsences = p >= 1.0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(Math.Log(1 - confidence) / Math.Log(1 - p)));

        var seenAbsences = snaps.Count(s => s.Marker == MarkerPresence.Absent && s.StartTime > cutoff);

        return (seenAbsences >= requiredAbsences, requiredAbsences, p, seenAbsences);
    }

    /// <summary>"Con evidencia" para la Capa 2 = con marker propio o confirmadamente ausente.</summary>
    private static bool HasMarkerOrAbsent(ExecutionSnapshot s) =>
        s.Marker is MarkerPresence.Present or MarkerPresence.PresentDeprecated or MarkerPresence.Absent;

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
