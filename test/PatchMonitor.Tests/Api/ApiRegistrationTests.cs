using Contracts.Api;
using Contracts.Monitor;
using Contracts.State;
using Microsoft.Extensions.DependencyInjection;
using MonitorApi.Infrastructure;
using Xunit;

namespace PatchMonitor.Tests.Api;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddMonitorApiServices"/> tiene que dejar resoluble
/// todo lo que la API de control necesita. No se conecta a ningún cluster: el
/// <c>TemporalClient</c> que el <see cref="Lazy{T}"/> del store espera no está registrado en
/// este <see cref="ServiceProvider"/> aislado, así que solo se resuelven los tipos que no lo
/// necesitan al construirse (los servicios en sí son singletons perezosos).
/// </summary>
public class ApiRegistrationTests
{
    [Fact]
    public void AddMonitorApiServices_resuelve_las_opciones_el_store_y_el_schedule_controller()
    {
        using var provider = new ServiceCollection()
            .AddMonitorApiServices()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<StateOptions>());
        Assert.NotNull(provider.GetService<MonitorOptions>());
        Assert.NotNull(provider.GetService<ApiOptions>());
        Assert.NotNull(provider.GetService<IPatchStateStore>());
        Assert.NotNull(provider.GetService<IScheduleController>());
    }

    [Fact]
    public void El_sink_registrado_por_default_es_el_noop()
    {
        using var provider = new ServiceCollection()
            .AddMonitorApiServices()
            .BuildServiceProvider();

        Assert.IsType<Common.State.NoopDecisionSink>(provider.GetRequiredService<IDecisionSink>());
    }
}
