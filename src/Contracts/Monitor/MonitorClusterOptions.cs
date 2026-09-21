namespace Contracts.Monitor;

/// <summary>
/// Host y namespace de la conexión propia del monitor (donde vive su estado), distinta del
/// namespace observado que describe <see cref="Contracts.Discovery.DiscoveryOptions"/>. Los
/// valores salen de variables de entorno (ver <see cref="FromEnvironment"/>); un valor ausente
/// o en blanco cae al default sin lanzar.
/// </summary>
public sealed record MonitorClusterOptions(string Host, string Namespace)
{
    /// <summary>Host por defecto cuando <c>TEMPORAL_HOST</c> no está.</summary>
    public const string DefaultHost = "temporal:7233";

    /// <summary>Namespace por defecto cuando <c>TEMPORAL_NAMESPACE</c> no está.</summary>
    public const string DefaultNamespace = "monitor";

    /// <summary>
    /// Lee <c>TEMPORAL_HOST</c> y <c>TEMPORAL_NAMESPACE</c>. Cualquiera ausente o en blanco usa
    /// su default. Nunca lanza.
    /// </summary>
    public static MonitorClusterOptions FromEnvironment()
    {
        var host = Environment.GetEnvironmentVariable("TEMPORAL_HOST");
        host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim();

        var ns = Environment.GetEnvironmentVariable("TEMPORAL_NAMESPACE");
        ns = string.IsNullOrWhiteSpace(ns) ? DefaultNamespace : ns.Trim();

        return new MonitorClusterOptions(host, ns);
    }
}
