using Microsoft.Extensions.DependencyInjection;

namespace PatchMonitor.Infrastructure;

/// <summary>
/// Extensiones de <see cref="IServiceCollection"/> para registrar los servicios de dominio
/// del worker PatchMonitor.
/// Placeholder en el spec 01: todavía no hay servicios de dominio (IPatchDiscovery,
/// IPhaseResolver, INotifier, IDecisionSink llegan en los specs 03+). Existe ahora para
/// fijar el patrón de DI del repo de referencia y que Program.cs no cambie de forma después.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPatchMonitorServices(this IServiceCollection services)
    {
        // Sin registros todavía (specs 03+).
        return services;
    }
}
