using System.Globalization;

namespace Contracts.Phase;

/// <summary>
/// Parámetros de la resolución de fase: el margen de gracia que una ejecución sin marker debe
/// superar sobre el último marker para leerse como fase 3 (código limpio), y la confianza
/// exigida a la evidencia mínima adaptativa de esa misma inferencia (spec 13). Los valores salen
/// de variables de entorno (ver <see cref="FromEnvironment"/>); un valor ausente, no numérico o
/// fuera de rango cae al default sin lanzar, igual que <see cref="Discovery.DiscoveryOptions"/>.
/// </summary>
public sealed record PhaseOptions(TimeSpan CleanGrace, double CleanConfidence = PhaseOptions.DefaultCleanConfidence)
{
    /// <summary>Margen de gracia por defecto cuando <c>PHASE_CLEAN_GRACE_HOURS</c> no está.</summary>
    public static readonly TimeSpan DefaultCleanGrace = TimeSpan.FromHours(24);

    /// <summary>Confianza por defecto cuando <c>PHASE_CLEAN_CONFIDENCE</c> no está.</summary>
    public const double DefaultCleanConfidence = 0.95;

    /// <summary>
    /// Lee <c>PHASE_CLEAN_GRACE_MINUTES</c> como un entero de minutos; si no está presente o no
    /// es positiva, cae a <c>PHASE_CLEAN_GRACE_HOURS</c> (entero de horas). Ausente, no numérico
    /// o no positivo en ambas ⇒ <see cref="DefaultCleanGrace"/>. Nunca lanza.
    ///
    /// Además lee <c>PHASE_CLEAN_CONFIDENCE</c> (double, cultura invariante); ausente, no
    /// numérico o fuera del rango abierto (0, 1) ⇒ <see cref="DefaultCleanConfidence"/>. Nunca
    /// lanza.
    /// </summary>
    public static PhaseOptions FromEnvironment()
    {
        var minutesRaw = Environment.GetEnvironmentVariable("PHASE_CLEAN_GRACE_MINUTES");
        TimeSpan cleanGrace;
        if (int.TryParse(minutesRaw, out var minutes) && minutes > 0)
        {
            cleanGrace = TimeSpan.FromMinutes(minutes);
        }
        else
        {
            var hoursRaw = Environment.GetEnvironmentVariable("PHASE_CLEAN_GRACE_HOURS");
            cleanGrace = int.TryParse(hoursRaw, out var hours) && hours > 0
                ? TimeSpan.FromHours(hours)
                : DefaultCleanGrace;
        }

        var confidenceRaw = Environment.GetEnvironmentVariable("PHASE_CLEAN_CONFIDENCE");
        var cleanConfidence = double.TryParse(
            confidenceRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
            && confidence > 0 && confidence < 1
                ? confidence
                : DefaultCleanConfidence;

        return new PhaseOptions(cleanGrace, cleanConfidence);
    }
}
