using Contracts.Monitor;
using Contracts.Notification;
using Microsoft.Extensions.DependencyInjection;
using PatchMonitor.Activities;
using PatchMonitor.Infrastructure;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddPatchMonitorServices"/> tiene que dejar resoluble
/// <see cref="MonitorOptions"/> (spec 06). No se conecta a ningún cluster.
/// </summary>
public class MonitorRegistrationTests
{
    [Fact]
    public void AddPatchMonitorServices_resuelve_MonitorOptions()
    {
        using var provider = new ServiceCollection().AddPatchMonitorServices().BuildServiceProvider();

        Assert.NotNull(provider.GetService<MonitorOptions>());
    }

    [Fact]
    public void AddPatchMonitorServices_resuelve_ConfigActivities_con_la_configuracion_de_la_pasada()
    {
        using var provider = new ServiceCollection().AddPatchMonitorServices().BuildServiceProvider();

        var activities = provider.GetService<ConfigActivities>();

        Assert.NotNull(activities);
        var config = activities.GetMonitorRunConfig();
        Assert.Equal(provider.GetRequiredService<MonitorOptions>().MaxPatchesPerRun, config.MaxPatchesPerRun);
        Assert.Equal(provider.GetRequiredService<NotificationOptions>().Enabled, config.NotificationsEnabled);
        Assert.Equal(provider.GetRequiredService<NotificationOptions>().MaxAttempts, config.NotifierMaxAttempts);
    }
}
