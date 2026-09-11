using Common.State;
using Common.Temporal;
using Contracts.Api;
using Contracts.Monitor;
using Contracts.State;
using Temporalio.Client;

namespace MonitorApi.Infrastructure;

/// <summary>
/// Extensiones de <see cref="IServiceCollection"/> para registrar los servicios de la API de
/// control (spec 08). Espejo de <c>AddPatchMonitorServices</c> del worker: mismas <c>*Options</c>
/// desde el entorno, mismo store, pero sin ningún tipo del worker.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMonitorApiServices(this IServiceCollection services)
    {
        services.AddSingleton(_ => StateOptions.FromEnvironment());
        services.AddSingleton(_ => MonitorOptions.FromEnvironment());
        services.AddSingleton(_ => ApiOptions.FromEnvironment());

        services.AddSingleton<IDecisionSink, NoopDecisionSink>();

        // Envuelve el TemporalClient singleton que la API ya conecta de forma ansiosa
        // (Program.cs): ningún segundo ConnectAsync, misma conexión que sirve /health.
        services.AddSingleton(sp => new Lazy<Task<ITemporalClient>>(
            () => Task.FromResult<ITemporalClient>(sp.GetRequiredService<TemporalClient>())));

        services.AddSingleton<IPatchStateStore, TemporalPatchStateStore>();
        services.AddSingleton<IScheduleController, TemporalScheduleController>();

        return services;
    }
}
