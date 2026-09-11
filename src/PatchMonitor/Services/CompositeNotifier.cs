using Contracts.Notification;

namespace PatchMonitor.Services;

/// <summary>
/// Fan-out sobre todos los <see cref="INotifier"/> registrados por DI: invoca cada uno de
/// forma independiente, acumula los mensajes de los que fallaron y lanza solo si
/// <b>todos</b> fallaron. Que un destino esté caído no puede tapar que otro sí notificó.
/// </summary>
public sealed class CompositeNotifier : INotifier
{
    private readonly IReadOnlyList<INotifier> _notifiers;

    public CompositeNotifier(IEnumerable<INotifier> notifiers)
    {
        _notifiers = notifiers.ToArray();
    }

    public string Name => "composite";

    public async Task NotifyAsync(VerdictChangeNotification notification, CancellationToken ct = default)
    {
        var failures = new List<string>();

        foreach (var notifier in _notifiers)
        {
            try
            {
                await notifier.NotifyAsync(notification, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{notifier.Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0 && failures.Count == _notifiers.Count)
        {
            throw new InvalidOperationException(
                $"Todos los notificadores fallaron: {string.Join("; ", failures)}");
        }
    }
}
