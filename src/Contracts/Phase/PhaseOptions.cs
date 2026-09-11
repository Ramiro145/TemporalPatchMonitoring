namespace Contracts.Phase;

/// <summary>
/// Parámetros de la resolución de fase. Por ahora solo el margen de gracia que una ejecución
/// sin marker debe superar sobre el último marker para leerse como fase 3 (código limpio). El
/// valor sale de una variable de entorno (ver <see cref="FromEnvironment"/>); un valor
/// ausente, no numérico o no positivo cae al default sin lanzar, igual que
/// <see cref="Discovery.DiscoveryOptions"/>.
/// </summary>
public sealed record PhaseOptions(TimeSpan CleanGrace)
{
    /// <summary>Margen de gracia por defecto cuando <c>PHASE_CLEAN_GRACE_HOURS</c> no está.</summary>
    public static readonly TimeSpan DefaultCleanGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// Lee <c>PHASE_CLEAN_GRACE_MINUTES</c> como un entero de minutos; si no está presente o no
    /// es positiva, cae a <c>PHASE_CLEAN_GRACE_HOURS</c> (entero de horas). Ausente, no numérico
    /// o no positivo en ambas ⇒ <see cref="DefaultCleanGrace"/>. Nunca lanza.
    /// </summary>
    public static PhaseOptions FromEnvironment()
    {
        var minutesRaw = Environment.GetEnvironmentVariable("PHASE_CLEAN_GRACE_MINUTES");
        if (int.TryParse(minutesRaw, out var minutes) && minutes > 0)
        {
            return new PhaseOptions(TimeSpan.FromMinutes(minutes));
        }

        var hoursRaw = Environment.GetEnvironmentVariable("PHASE_CLEAN_GRACE_HOURS");
        var cleanGrace = int.TryParse(hoursRaw, out var hours) && hours > 0
            ? TimeSpan.FromHours(hours)
            : DefaultCleanGrace;

        return new PhaseOptions(cleanGrace);
    }
}
