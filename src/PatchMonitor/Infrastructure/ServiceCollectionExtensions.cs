using Contracts.Discovery;
using Contracts.Domain.Gates;
using Contracts.Phase;
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

        // Resolución de fase (spec 04). Cómputo puro sin I/O ⇒ singleton. El override vive en
        // memoria hasta el spec 05; TimeProvider.System da el reloj real y FakeTimeProvider lo
        // sustituye en tests. PhaseActivities se resuelve por tipo concreto desde WorkerHost.
        services.AddSingleton(_ => PhaseOptions.FromEnvironment());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPhaseOverrideStore, InMemoryPhaseOverrideStore>();
        services.AddSingleton<IPhaseResolver, PhaseResolver>();
        services.AddSingleton<PhaseActivities>();

        // INotifier, IDecisionSink llegan en los specs 05+.
        return services;
    }
}
