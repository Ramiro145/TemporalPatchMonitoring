using Common.State;
using Contracts.State;
using Microsoft.Extensions.DependencyInjection;
using PatchMonitor.Activities;
using PatchMonitor.Infrastructure;
using Xunit;

namespace PatchMonitor.Tests.State;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddPatchMonitorServices"/> tiene que dejar todo el
/// grafo del spec 05 resoluble. No se conecta a ningún cluster: el <c>TemporalClient</c> propio
/// es un <see cref="Lazy{T}"/> que no se evalúa al construir el <see cref="ServiceProvider"/>.
/// </summary>
public class PatchStateRegistrationTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection().AddPatchMonitorServices().BuildServiceProvider();

    [Fact]
    public void AddPatchMonitorServices_resuelve_las_opciones_el_store_el_sink_y_la_activity()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<StateOptions>());
        Assert.NotNull(provider.GetService<IPatchStateStore>());
        Assert.NotNull(provider.GetService<IDecisionSink>());
        Assert.NotNull(provider.GetService<PatchStateActivities>());
    }

    [Fact]
    public void El_sink_registrado_por_default_es_el_noop()
    {
        using var provider = BuildProvider();

        Assert.IsType<NoopDecisionSink>(provider.GetRequiredService<IDecisionSink>());
    }
}
