using Contracts.Discovery;
using Contracts.Domain.Gates;
using Microsoft.Extensions.DependencyInjection;
using PatchMonitor.Activities;
using PatchMonitor.Services;

namespace PatchMonitor.Infrastructure;

/// <summary>
/// Extensiones de <see cref="IServiceCollection"/> para registrar los servicios de dominio
/// del worker PatchMonitor.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPatchMonitorServices(this IServiceCollection services)
    {
        // Gates de salto de fase (spec 02). Puros y sin estado ⇒ singleton.
        services.AddSingleton<IPhaseGate, CoexistenceToDeprecatedGate>();
        services.AddSingleton<IPhaseGate, DeprecatedToCleanGate>();
        services.AddSingleton<PhaseEvaluator>();

        // Descubrimiento en dos niveles (spec 03). Singleton: TemporalExecutionSource cachea
        // la conexión al cluster. La Activity se resuelve por tipo concreto desde WorkerHost.
        services.AddSingleton(_ => DiscoveryOptions.FromEnvironment());
        services.AddSingleton<IExecutionSource, TemporalExecutionSource>();
        services.AddSingleton<IPatchDiscovery, PatchDiscoveryService>();
        services.AddSingleton<DiscoveryActivities>();

        // IPhaseResolver, INotifier, IDecisionSink llegan en los specs 04+.
        return services;
    }
}
