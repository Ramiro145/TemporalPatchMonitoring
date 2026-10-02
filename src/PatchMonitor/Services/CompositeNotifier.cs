using Contracts.Notification;

namespace PatchMonitor.Services;

/// <summary>
/// Fan-out sobre todos los <see cref="INotifier"/> registrados por DI: invoca cada uno de
/// forma independiente (que uno falle no impide que los demás reciban la llamada), acumula los
/// mensajes de los que fallaron y lanza si falló <b>cualquiera</b>. El log local nunca falla, así
/// que exigir que fallen todos dejaba a un webhook caído reportado como enviado (spec 14).
/// </summary>
public sealed class CompositeNotifier : INotifier
{
    private readonly IReadOnlyList<INotifier> _notifiers;

    public CompositeNotifier(IEnumerable<INotifier> notifiers)
    {
        _notifiers = notifiers.ToArray();
    }

    public string Name => "composite";

    /// <summary>Los notificadores del fan-out, para inspección en tests de registro DI.</summary>
    public IReadOnlyList<INotifier> Notifiers => _notifiers;

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

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Fallaron {failures.Count} de {_notifiers.Count} notificadores: {string.Join("; ", failures)}");
        }
    }
}
