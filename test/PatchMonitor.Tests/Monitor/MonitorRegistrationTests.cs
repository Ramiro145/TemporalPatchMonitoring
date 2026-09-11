using Contracts.Monitor;
using Microsoft.Extensions.DependencyInjection;
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
}
