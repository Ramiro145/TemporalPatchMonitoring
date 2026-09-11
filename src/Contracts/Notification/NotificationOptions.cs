namespace Contracts.Notification;

/// <summary>
/// Parámetros del envío de notificaciones de cambio de veredicto: si está habilitado, a qué
/// webhook (si hay alguno) apuntar, con qué timeout y cuántos intentos permite la
/// <c>RetryPolicy</c> de <c>NotifyVerdictChangeAsync</c>. Los valores salen de variables de
/// entorno (ver <see cref="FromEnvironment"/>); un valor ausente, no numérico o no positivo cae
/// al default sin lanzar, igual que <see cref="Discovery.DiscoveryOptions"/>,
/// <see cref="Phase.PhaseOptions"/> y <see cref="Monitor.MonitorOptions"/>. <see cref="Enabled"/>
/// es la excepción: solo distingue <c>"false"</c> (sin importar mayúsculas) de todo lo demás.
/// </summary>
public sealed record NotificationOptions(
    bool Enabled,
    string? WebhookUrl,
    string? WebhookAuthHeader,
    TimeSpan WebhookTimeout,
    int MaxAttempts)
{
    /// <summary>Timeout por defecto, en segundos, del <c>POST</c> al webhook.</summary>
    public const int DefaultWebhookTimeoutSeconds = 10;

    /// <summary>Intentos por defecto de <c>RetryPolicy.MaximumAttempts</c> de la Activity.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>
    /// Lee <c>NOTIFIER_ENABLED</c>, <c>NOTIFIER_WEBHOOK_URL</c>,
    /// <c>NOTIFIER_WEBHOOK_AUTH_HEADER</c>, <c>NOTIFIER_WEBHOOK_TIMEOUT_SECONDS</c> y
    /// <c>NOTIFIER_MAX_ATTEMPTS</c>. Cualquiera de las dos últimas que falte, no parsee como
    /// entero o no sea positiva usa su default. <c>NOTIFIER_ENABLED</c> ausente o distinto de
    /// <c>"false"</c> (sin importar mayúsculas) vale <c>true</c>. <c>NOTIFIER_WEBHOOK_URL</c> y
    /// <c>NOTIFIER_WEBHOOK_AUTH_HEADER</c> ausentes o en blanco quedan en <c>null</c>. Nunca
    /// lanza.
    /// </summary>
    public static NotificationOptions FromEnvironment()
    {
        var enabledRaw = Environment.GetEnvironmentVariable("NOTIFIER_ENABLED");
        var enabled = !string.Equals(enabledRaw, "false", StringComparison.OrdinalIgnoreCase);

        var webhookUrl = Environment.GetEnvironmentVariable("NOTIFIER_WEBHOOK_URL");
        webhookUrl = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();

        var webhookAuthHeader = Environment.GetEnvironmentVariable("NOTIFIER_WEBHOOK_AUTH_HEADER");
        webhookAuthHeader = string.IsNullOrWhiteSpace(webhookAuthHeader) ? null : webhookAuthHeader.Trim();

        return new NotificationOptions(
            enabled,
            webhookUrl,
            webhookAuthHeader,
            TimeSpan.FromSeconds(PositiveIntOrDefault("NOTIFIER_WEBHOOK_TIMEOUT_SECONDS", DefaultWebhookTimeoutSeconds)),
            PositiveIntOrDefault("NOTIFIER_MAX_ATTEMPTS", DefaultMaxAttempts));
    }

    private static int PositiveIntOrDefault(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
