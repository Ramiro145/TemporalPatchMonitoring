using Contracts.Domain.Gates;
using Microsoft.Extensions.DependencyInjection;

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

        // IPatchDiscovery, IPhaseResolver, INotifier, IDecisionSink llegan en los specs 03+.
        return services;
    }
}
