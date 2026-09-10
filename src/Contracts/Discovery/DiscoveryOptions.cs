namespace Contracts.Discovery;

/// <summary>
/// Parámetros de una corrida de descubrimiento: contra qué namespace apunta, qué ventana de
/// ejecuciones cerradas mira y los dos topes que acotan el trabajo por corrida. Los valores
/// salen de variables de entorno (ver <see cref="FromEnvironment"/>); un valor ausente,
/// no numérico o fuera de rango cae al default sin lanzar.
/// </summary>
public sealed record DiscoveryOptions(
    string Namespace,
    int LookbackDays,
    int MaxExecutions,
    int MaxHistories)
{
    /// <summary>Namespace por defecto cuando <c>TARGET_TEMPORAL_NAMESPACE</c> no está.</summary>
    public const string DefaultNamespace = "default";

    /// <summary>Ventana por defecto, en días, para incluir ejecuciones cerradas.</summary>
    public const int DefaultLookbackDays = 7;

    /// <summary>Tope por defecto de ejecuciones listadas por corrida.</summary>
    public const int DefaultMaxExecutions = 500;

    /// <summary>Tope por defecto de historias leídas en tier 2 por corrida.</summary>
    public const int DefaultMaxHistories = 200;

    /// <summary>
    /// Lee <c>TARGET_TEMPORAL_NAMESPACE</c>, <c>DISCOVERY_LOOKBACK_DAYS</c>,
    /// <c>DISCOVERY_MAX_EXECUTIONS</c> y <c>DISCOVERY_MAX_HISTORIES</c>. Cualquiera que falte,
    /// no parsee como entero o no sea positiva usa su default. Nunca lanza.
    /// </summary>
    public static DiscoveryOptions FromEnvironment()
    {
        var ns = Environment.GetEnvironmentVariable("TARGET_TEMPORAL_NAMESPACE");
        ns = string.IsNullOrWhiteSpace(ns) ? DefaultNamespace : ns.Trim();

        return new DiscoveryOptions(
            ns,
            PositiveIntOrDefault("DISCOVERY_LOOKBACK_DAYS", DefaultLookbackDays),
            PositiveIntOrDefault("DISCOVERY_MAX_EXECUTIONS", DefaultMaxExecutions),
            PositiveIntOrDefault("DISCOVERY_MAX_HISTORIES", DefaultMaxHistories));
    }

    private static int PositiveIntOrDefault(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
