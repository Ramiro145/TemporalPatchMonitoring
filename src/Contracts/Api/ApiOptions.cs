namespace Contracts.Api;

/// <summary>
/// Parámetros de la API de control (spec 08): el tope de patches enriquecidos por
/// <c>GET /patches</c> y el vencimiento por defecto de un override declarado sin
/// <c>ExpiresAt</c> explícito. Los valores salen de variables de entorno (ver
/// <see cref="FromEnvironment"/>); un valor ausente, no numérico o no positivo cae al default
/// sin lanzar, igual que <see cref="Discovery.DiscoveryOptions"/>, <see cref="Phase.PhaseOptions"/>,
/// <see cref="Monitor.MonitorOptions"/>, <see cref="State.StateOptions"/> y
/// <see cref="Notification.NotificationOptions"/>.
/// </summary>
public sealed record ApiOptions(int MaxListPatches, TimeSpan OverrideDefaultTtl)
{
    /// <summary>Tope por defecto de patches enriquecidos cuando <c>API_MAX_LIST_PATCHES</c> no está.</summary>
    public const int DefaultMaxListPatches = 100;

    /// <summary>Horas por defecto de vencimiento cuando <c>API_OVERRIDE_DEFAULT_TTL_HOURS</c> no está.</summary>
    public const int DefaultOverrideTtlHours = 24;

    /// <summary>
    /// Lee <c>API_MAX_LIST_PATCHES</c> y <c>API_OVERRIDE_DEFAULT_TTL_HOURS</c>. Cualquiera que
    /// falte, no parsee como entero o no sea positiva usa su default. Nunca lanza.
    /// </summary>
    public static ApiOptions FromEnvironment()
    {
        return new ApiOptions(
            PositiveIntOrDefault("API_MAX_LIST_PATCHES", DefaultMaxListPatches),
            TimeSpan.FromHours(PositiveIntOrDefault("API_OVERRIDE_DEFAULT_TTL_HOURS", DefaultOverrideTtlHours)));
    }

    private static int PositiveIntOrDefault(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
