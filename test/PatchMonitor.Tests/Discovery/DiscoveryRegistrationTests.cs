using Contracts.Discovery;
using Microsoft.Extensions.DependencyInjection;
using PatchMonitor.Activities;
using PatchMonitor.Infrastructure;
using Xunit;

namespace PatchMonitor.Tests.Discovery;

public class DiscoveryRegistrationTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection().AddPatchMonitorServices().BuildServiceProvider();

    [Fact]
    public void AddPatchMonitorServices_resuelve_el_puerto_de_descubrimiento_y_su_Activity()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IPatchDiscovery>());
        Assert.NotNull(provider.GetService<IExecutionSource>());
        Assert.NotNull(provider.GetService<DiscoveryActivities>());
        Assert.NotNull(provider.GetService<DiscoveryOptions>());
    }
}
