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
    /// Lee <c>PHASE_CLEAN_GRACE_HOURS</c> como un entero de horas. Ausente, no numérico o
    /// no positivo ⇒ <see cref="DefaultCleanGrace"/>. Nunca lanza.
    /// </summary>
    public static PhaseOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("PHASE_CLEAN_GRACE_HOURS");
        var cleanGrace = int.TryParse(raw, out var hours) && hours > 0
            ? TimeSpan.FromHours(hours)
            : DefaultCleanGrace;

        return new PhaseOptions(cleanGrace);
    }
}
