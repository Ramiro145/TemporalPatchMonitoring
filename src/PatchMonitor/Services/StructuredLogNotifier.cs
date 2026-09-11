using System.Text.Json;
using Contracts.Notification;

namespace PatchMonitor.Services;

/// <summary>
/// Notificador siempre registrado, sin condición de configuración: serializa la notificación
/// con <see cref="System.Text.Json"/> y la escribe con <see cref="Console.WriteLine(string)"/>
/// como una sola línea JSON. No hay <c>Microsoft.Extensions.Logging</c> en el proyecto;
/// <c>docker compose logs</c> es el canal ya establecido para el resto del worker.
/// </summary>
public sealed class StructuredLogNotifier : INotifier
{
    public string Name => "log";

    public Task NotifyAsync(VerdictChangeNotification notification, CancellationToken ct = default)
    {
        Console.WriteLine(JsonSerializer.Serialize(notification));
        return Task.CompletedTask;
    }
}
