using Contracts.Notification;
using Contracts.State;
using PatchMonitor.Services;
using Temporalio.Activities;

namespace PatchMonitor.Activities;

/// <summary>
/// Única Activity de notificación (spec 07): reclama <see cref="VerdictChangeNotification.Revision"/>
/// en el entity del patch <b>antes</b> de invocar ningún <see cref="INotifier"/>. Si el claim
/// devuelve <c>false</c> (ya se notificó, o un <c>Revision</c> viejo llegó tarde por un
/// reintento at-least-once) no invoca nada y devuelve <c>false</c>.
/// </summary>
public class NotificationActivities
{
    private readonly IPatchStateStore _store;
    private readonly CompositeNotifier _notifier;

    public NotificationActivities(IPatchStateStore store, CompositeNotifier notifier)
    {
        _store = store;
        _notifier = notifier;
    }

    [Activity]
    public async Task<bool> NotifyVerdictChangeAsync(VerdictChangeNotification n)
    {
        var claimed = await _store.TryClaimNotificationAsync(n.Key, n.Revision).ConfigureAwait(false);
        if (!claimed)
        {
            return false;
        }

        await _notifier.NotifyAsync(n).ConfigureAwait(false);
        return true;
    }
}
