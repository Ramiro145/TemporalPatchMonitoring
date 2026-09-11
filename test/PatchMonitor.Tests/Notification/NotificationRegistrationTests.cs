using Contracts.Notification;
using Microsoft.Extensions.DependencyInjection;
using PatchMonitor.Activities;
using PatchMonitor.Infrastructure;
using PatchMonitor.Services;
using Xunit;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddPatchMonitorServices"/> tiene que dejar resoluble
/// el grafo del spec 07: un solo <see cref="INotifier"/> (el <see cref="CompositeNotifier"/>),
/// cuyo fan-out interno depende de si <c>NOTIFIER_WEBHOOK_URL</c> está configurada.
/// </summary>
public class NotificationRegistrationTests
{
    private static ServiceProvider BuildProvider(string? webhookUrl)
    {
        var saved = Environment.GetEnvironmentVariable("NOTIFIER_WEBHOOK_URL");
        try
        {
            Environment.SetEnvironmentVariable("NOTIFIER_WEBHOOK_URL", webhookUrl);
            return new ServiceCollection().AddPatchMonitorServices().BuildServiceProvider();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOTIFIER_WEBHOOK_URL", saved);
        }
    }

    [Fact]
    public void Sin_webhook_url_el_fan_out_solo_tiene_el_notificador_de_log()
    {
        using var provider = BuildProvider(null);

        var notifier = Assert.IsType<CompositeNotifier>(provider.GetRequiredService<INotifier>());
        Assert.Single(notifier.Notifiers);
        Assert.IsType<StructuredLogNotifier>(notifier.Notifiers[0]);
    }

    [Fact]
    public void Con_webhook_url_el_fan_out_tiene_log_y_webhook()
    {
        using var provider = BuildProvider("https://example.org/hook");

        var notifier = Assert.IsType<CompositeNotifier>(provider.GetRequiredService<INotifier>());
        Assert.Equal(2, notifier.Notifiers.Count);
        Assert.Contains(notifier.Notifiers, n => n is StructuredLogNotifier);
        Assert.Contains(notifier.Notifiers, n => n is WebhookNotifier);
    }

    [Fact]
    public void AddPatchMonitorServices_resuelve_INotifier_y_NotificationActivities()
    {
        using var provider = BuildProvider(null);

        Assert.NotNull(provider.GetService<INotifier>());
        Assert.NotNull(provider.GetService<NotificationActivities>());
    }
}
