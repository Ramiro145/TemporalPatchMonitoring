using Contracts.Notification;
using Contracts.State;
using PatchMonitor.Services;
using Temporalio.Activities;

namespace PatchMonitor.Activities;

/// <summary>
/// Única Activity de notificación (spec 07, orden corregido por el spec 14): consulta el estado
/// del patch, envía por el <see cref="INotifier"/> y <b>recién tras un envío exitoso</b> reclama
/// <see cref="VerdictChangeNotification.Revision"/> en el entity. Si el estado ya trae esa
/// revisión (o una mayor) notificada, no envía nada y devuelve <c>false</c>. Si el envío lanza,
/// la Activity falla sin reclamar: la <c>RetryPolicy</c> reintenta y, si se agotan los intentos,
/// el próximo tick vuelve a intentarlo porque <c>NotifiedRevision</c> sigue atrás.
/// </summary>
/// <remarks>
/// Es at-least-once: si el envío sale bien pero el claim falla, el reintento reenvía.
/// </remarks>
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
        var state = await _store.GetStateAsync(n.Key).ConfigureAwait(false);
        if (state is not null && state.NotifiedRevision >= n.Revision)
        {
            return false;
        }

        await _notifier.NotifyAsync(n).ConfigureAwait(false);

        // El resultado del claim no cambia lo que se informa: el aviso ya salió. Un false acá
        // solo significa que otra corrida reclamó en paralelo (duplicado aceptado).
        await _store.TryClaimNotificationAsync(n.Key, n.Revision).ConfigureAwait(false);
        return true;
    }
}
