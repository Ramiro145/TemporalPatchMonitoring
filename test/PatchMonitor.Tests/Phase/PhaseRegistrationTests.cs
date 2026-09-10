using Contracts.Phase;
using Microsoft.Extensions.DependencyInjection;
using PatchMonitor.Activities;
using PatchMonitor.Infrastructure;
using Xunit;

namespace PatchMonitor.Tests.Phase;

public class PhaseRegistrationTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection().AddPatchMonitorServices().BuildServiceProvider();

    [Fact]
    public void AddPatchMonitorServices_resuelve_el_resolver_el_store_las_opciones_el_reloj_y_la_Activity()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IPhaseResolver>());
        Assert.NotNull(provider.GetService<IPhaseOverrideStore>());
        Assert.NotNull(provider.GetService<PhaseOptions>());
        Assert.NotNull(provider.GetService<TimeProvider>());
        Assert.NotNull(provider.GetService<PhaseActivities>());
    }
}
