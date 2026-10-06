using Contracts.Domain;
using Contracts.Notification;
using PatchMonitor.Services;
using Temporalio.Exceptions;
using Xunit;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="CompositeNotifier"/> hace fan-out sobre <see cref="INotifier"/>: cada uno se
/// invoca de forma independiente y lanza si falló cualquiera (spec 14).
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
    public async Task Un_solo_notificador_que_falla_lanza_y_el_otro_igual_recibe_la_llamada()
    {
        var ok = new FakeNotifier("ok");
        var failing = new FakeNotifier("failing", fails: true);
        var composite = new CompositeNotifier(new INotifier[] { ok, failing });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composite.NotifyAsync(Notification));

        Assert.Contains("failing", ex.Message);
        Assert.DoesNotContain("ok:", ex.Message);
        Assert.Single(ok.Calls);
        Assert.Single(failing.Calls);
    }

    [Fact]
    public async Task Si_el_que_falla_va_primero_el_siguiente_igual_recibe_la_llamada()
    {
        var failing = new FakeNotifier("failing", fails: true);
        var ok = new FakeNotifier("ok");
        var composite = new CompositeNotifier(new INotifier[] { failing, ok });

        await Assert.ThrowsAsync<InvalidOperationException>(() => composite.NotifyAsync(Notification));

        Assert.Single(ok.Calls);
    }

    [Fact]
    public async Task Si_ninguno_falla_no_lanza()
    {
        var first = new FakeNotifier("first");
        var second = new FakeNotifier("second");
        var composite = new CompositeNotifier(new INotifier[] { first, second });

        await composite.NotifyAsync(Notification);

        Assert.Single(first.Calls);
        Assert.Single(second.Calls);
    }

    // ── Spec 18: clasificación del error y fan-out ante timeouts ───────────────

    private static ApplicationFailureException Rejected(string message) =>
        new(message, errorType: "WebhookRejected", nonRetryable: true);

    [Fact]
    public async Task Si_todos_los_fallos_son_no_reintentables_lanza_ApplicationFailureException_no_reintentable()
    {
        var first = new FakeNotifier("first") { Throws = Rejected("rechazo A") };
        var second = new FakeNotifier("second") { Throws = Rejected("rechazo B") };
        var composite = new CompositeNotifier(new INotifier[] { first, second });

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => composite.NotifyAsync(Notification));

        Assert.True(ex.NonRetryable);
        Assert.Equal("NotificationRejected", ex.ErrorType);
        Assert.Contains("first: rechazo A", ex.Message);
        Assert.Contains("second: rechazo B", ex.Message);
    }

    [Fact]
    public async Task Un_solo_notificador_no_reintentable_lanza_no_reintentable()
    {
        var composite = new CompositeNotifier(new INotifier[]
        {
            new FakeNotifier("ok"),
            new FakeNotifier("webhook") { Throws = Rejected("400") },
        });

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => composite.NotifyAsync(Notification));

        Assert.True(ex.NonRetryable);
    }

    [Fact]
    public async Task Un_fallo_no_reintentable_mas_uno_reintentable_lanza_reintentable()
    {
        var rejected = new FakeNotifier("rejected") { Throws = Rejected("400") };
        var transient = new FakeNotifier("transient", fails: true);
        var composite = new CompositeNotifier(new INotifier[] { rejected, transient });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composite.NotifyAsync(Notification));

        Assert.Contains("rejected", ex.Message);
        Assert.Contains("transient", ex.Message);
    }

    [Fact]
    public async Task Un_timeout_de_un_destino_no_corta_el_fan_out()
    {
        var timedOut = new FakeNotifier("slow") { Throws = new TaskCanceledException("timeout simulado") };
        var ok = new FakeNotifier("ok");
        var composite = new CompositeNotifier(new INotifier[] { timedOut, ok });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composite.NotifyAsync(Notification));

        Assert.Contains("slow", ex.Message);
        Assert.Single(timedOut.Calls);
        Assert.Single(ok.Calls);
    }

    [Fact]
    public async Task Con_el_ct_cancelado_propaga_OperationCanceledException_y_no_sigue()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new FakeNotifier("first") { Throws = new OperationCanceledException(cts.Token) };
        var next = new FakeNotifier("next");
        var composite = new CompositeNotifier(new INotifier[] { cancelled, next });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => composite.NotifyAsync(Notification, cts.Token));

        Assert.Empty(next.Calls);
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
