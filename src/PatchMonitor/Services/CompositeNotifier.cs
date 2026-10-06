using Contracts.Notification;
using Temporalio.Exceptions;

namespace PatchMonitor.Services;

/// <summary>
/// Fan-out sobre todos los <see cref="INotifier"/> registrados por DI: invoca cada uno de
/// forma independiente (que uno falle no impide que los demás reciban la llamada), acumula los
/// mensajes de los que fallaron y lanza si falló <b>cualquiera</b>. El log local nunca falla, así
/// que exigir que fallen todos dejaba a un webhook caído reportado como enviado (spec 14).
/// </summary>
/// <remarks>
/// Spec 18: el error lanzado es no reintentable (<c>ApplicationFailureException</c>,
/// <c>NotificationRejected</c>) solo si todos los fallos lo eran; si no, es reintentable. Un timeout
/// de un destino (<see cref="OperationCanceledException"/> con el <c>ct</c> vivo) no corta el fan-out.
/// </remarks>
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
        var failures = new List<(string Name, Exception Error)>();

        foreach (var notifier in _notifiers)
        {
            try
            {
                await notifier.NotifyAsync(notification, ct).ConfigureAwait(false);
            }
            // Solo la cancelación del ct propio corta el fan-out. Una OperationCanceledException
            // con el ct vivo es un timeout del destino: cuenta como fallo de ese destino y los
            // demás igual reciben la llamada (spec 18).
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                failures.Add((notifier.Name, ex));
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        var message =
            $"Fallaron {failures.Count} de {_notifiers.Count} notificadores: "
            + string.Join("; ", failures.Select(f => $"{f.Name}: {f.Error.Message}"));

        // No reintentable solo si TODOS los fallos lo son: con uno reintentable mezclado, el
        // reintento sigue siendo necesario para ese destino (spec 18).
        if (failures.All(f => f.Error is ApplicationFailureException { NonRetryable: true }))
        {
            throw new ApplicationFailureException(message, errorType: "NotificationRejected", nonRetryable: true);
        }

        throw new InvalidOperationException(message);
    }
}
