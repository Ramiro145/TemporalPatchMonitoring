using Contracts.Notification;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="INotifier"/> en memoria para <c>CompositeNotifierTests</c> y
/// <c>MonitorWorkflowTests</c>: el constructor decide si <see cref="NotifyAsync"/> tira o no, y
/// cada llamada recibida queda en <see cref="Calls"/> para asertar.
/// </summary>
public sealed class FakeNotifier : INotifier
{
    private readonly bool _fails;

    public FakeNotifier(string name, bool fails = false)
    {
        Name = name;
        _fails = fails;
    }

    public string Name { get; }

    public List<VerdictChangeNotification> Calls { get; } = new();

    public Task NotifyAsync(VerdictChangeNotification notification, CancellationToken ct = default)
    {
        Calls.Add(notification);
        if (_fails)
        {
            throw new InvalidOperationException($"fallo simulado de {Name}");
        }

        return Task.CompletedTask;
    }
}
