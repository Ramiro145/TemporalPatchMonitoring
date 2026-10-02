using Contracts.Notification;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="INotifier"/> en memoria para <c>CompositeNotifierTests</c> y
/// <c>MonitorWorkflowTests</c>: el constructor decide si <see cref="NotifyAsync"/> tira o no, y
/// cada llamada recibida queda en <see cref="Calls"/> para asertar.
/// </summary>
public sealed class FakeNotifier : INotifier
{
    public FakeNotifier(string name, bool fails = false)
    {
        Name = name;
        Fails = fails;
    }

    public string Name { get; }

    /// <summary>Si <c>true</c>, <see cref="NotifyAsync"/> tira; se puede cambiar entre pasadas.</summary>
    public bool Fails { get; set; }

    public List<VerdictChangeNotification> Calls { get; } = new();

    public Task NotifyAsync(VerdictChangeNotification notification, CancellationToken ct = default)
    {
        Calls.Add(notification);
        if (Fails)
        {
            throw new InvalidOperationException($"fallo simulado de {Name}");
        }

        return Task.CompletedTask;
    }
}
