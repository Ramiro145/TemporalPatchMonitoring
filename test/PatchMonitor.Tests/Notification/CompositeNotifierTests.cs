using Contracts.Domain;
using Contracts.Notification;
using PatchMonitor.Services;
using Xunit;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="CompositeNotifier"/> hace fan-out sobre <see cref="INotifier"/>: cada uno se
/// invoca de forma independiente y solo lanza si todos fallaron.
/// </summary>
public class CompositeNotifierTests
{
    private static readonly VerdictChangeNotification Notification = new(
        "wf#1",
        new PatchKey("default", "OrderWorkflow", "order-v2"),
        1,
        PatchPhase.Unknown,
        PatchPhase.Coexistence,
        null,
        GateOutcome.Blocked,
        PatchPhase.Deprecated,
        0,
        Array.Empty<string>(),
        "alta",
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task Un_notificador_falla_el_otro_igual_recibe_la_llamada_y_no_lanza()
    {
        var ok = new FakeNotifier("ok");
        var failing = new FakeNotifier("failing", fails: true);
        var composite = new CompositeNotifier(new INotifier[] { ok, failing });

        await composite.NotifyAsync(Notification);

        Assert.Single(ok.Calls);
        Assert.Single(failing.Calls);
    }

    [Fact]
    public async Task Los_dos_notificadores_fallan_lanza_con_ambos_mensajes()
    {
        var first = new FakeNotifier("first", fails: true);
        var second = new FakeNotifier("second", fails: true);
        var composite = new CompositeNotifier(new INotifier[] { first, second });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composite.NotifyAsync(Notification));

        Assert.Contains("first", ex.Message);
        Assert.Contains("second", ex.Message);
    }
}
