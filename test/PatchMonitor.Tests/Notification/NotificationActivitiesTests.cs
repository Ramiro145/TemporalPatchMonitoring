using Contracts.Domain;
using Contracts.Notification;
using Contracts.State;
using PatchMonitor.Activities;
using PatchMonitor.Services;
using PatchMonitor.Tests.Monitor;
using Xunit;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// El orden consultar → enviar → reclamar de <see cref="NotificationActivities"/> (spec 14): un
/// envío fallido nunca deja la revisión reclamada, así el siguiente intento puede reenviar.
/// </summary>
public class NotificationActivitiesTests
{
    private static readonly PatchKey Key = new("default", "OrderWorkflow", "order-v2");

    private static VerdictChangeNotification NotificationFor(int revision) => new(
        $"{Key.ToWorkflowId()}#{revision}",
        Key,
        revision,
        PatchPhase.Unknown,
        PatchPhase.Coexistence,
        null,
        GateOutcome.Blocked,
        PatchPhase.Deprecated,
        0,
        Array.Empty<string>(),
        "alta",
        DateTimeOffset.UtcNow);

    private static FakePatchStateStore StoreWith(int revision, int notifiedRevision)
    {
        var store = new FakePatchStateStore();
        store.Seed(Key, PatchState.Initial(Key) with { Revision = revision, NotifiedRevision = notifiedRevision });
        return store;
    }

    [Fact]
    public async Task Envio_exitoso_devuelve_true_y_reclama_la_revision()
    {
        var store = StoreWith(revision: 2, notifiedRevision: 0);
        var notifier = new FakeNotifier("log");
        var activities = new NotificationActivities(store, new CompositeNotifier(new INotifier[] { notifier }));

        var sent = await activities.NotifyVerdictChangeAsync(NotificationFor(2));

        Assert.True(sent);
        Assert.Single(notifier.Calls);
        Assert.Equal(2, (await store.GetStateAsync(Key))!.NotifiedRevision);
    }

    [Fact]
    public async Task Envio_fallido_lanza_y_deja_la_revision_sin_reclamar()
    {
        var store = StoreWith(revision: 2, notifiedRevision: 0);
        var webhook = new FakeNotifier("webhook", fails: true);
        var activities = new NotificationActivities(
            store, new CompositeNotifier(new INotifier[] { new FakeNotifier("log"), webhook }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => activities.NotifyVerdictChangeAsync(NotificationFor(2)));

        Assert.Equal(0, (await store.GetStateAsync(Key))!.NotifiedRevision);
    }

    [Fact]
    public async Task Tras_un_fallo_el_siguiente_intento_reenvia_y_reclama()
    {
        var store = StoreWith(revision: 2, notifiedRevision: 0);
        var webhook = new FakeNotifier("webhook", fails: true);
        var activities = new NotificationActivities(store, new CompositeNotifier(new INotifier[] { webhook }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => activities.NotifyVerdictChangeAsync(NotificationFor(2)));

        webhook.Fails = false;
        var sent = await activities.NotifyVerdictChangeAsync(NotificationFor(2));

        Assert.True(sent);
        Assert.Equal(2, webhook.Calls.Count);
        Assert.Equal(2, (await store.GetStateAsync(Key))!.NotifiedRevision);
    }

    [Fact]
    public async Task Revision_ya_notificada_devuelve_false_sin_invocar_al_notificador()
    {
        var store = StoreWith(revision: 2, notifiedRevision: 2);
        var notifier = new FakeNotifier("log");
        var activities = new NotificationActivities(store, new CompositeNotifier(new INotifier[] { notifier }));

        var sent = await activities.NotifyVerdictChangeAsync(NotificationFor(2));

        Assert.False(sent);
        Assert.Empty(notifier.Calls);
    }
}
