using Common.State;
using Contracts.Discovery;
using Contracts.Domain.Gates;
using Contracts.Monitor;
using Contracts.Notification;
using Contracts.Phase;
using Contracts.State;
using Microsoft.Extensions.DependencyInjection;
using PatchMonitor.Activities;
using PatchMonitor.Services;
using Temporalio.Client;

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

        // Estado durable en entity workflows (spec 05). StateOptions desde el entorno; el
        // TemporalClient propio del monitor (TEMPORAL_HOST, no el namespace observado) como
        // Lazy<Task<...>> con la misma forma que TemporalExecutionSource. El sink es no-op por
        // default; un adaptador real (SQL u otro) es trabajo futuro.
        services.AddSingleton(_ => StateOptions.FromEnvironment());
        services.AddSingleton(_ => new Lazy<Task<ITemporalClient>>(async () =>
            (ITemporalClient)await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
            {
                TargetHost = Environment.GetEnvironmentVariable("TEMPORAL_HOST") ?? "temporal:7233",
            })));
        services.AddSingleton<IDecisionSink, NoopDecisionSink>();
        services.AddSingleton<IPatchStateStore, TemporalPatchStateStore>();
        services.AddSingleton<PatchStateActivities>();

        // Pasada de monitoreo y Temporal Schedule que la dispara (spec 06).
        services.AddSingleton(_ => MonitorOptions.FromEnvironment());

        // Notificador pluggable (spec 07). NotificationOptions se lee una sola vez, acá y no
        // por factory diferida como el resto de las *Options, porque la decisión de registrar
        // o no WebhookNotifier depende de WebhookUrl y tiene que tomarse ahora, no al resolver.
        var notificationOptions = NotificationOptions.FromEnvironment();
        services.AddSingleton(notificationOptions);
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton<StructuredLogNotifier>();

        if (notificationOptions.WebhookUrl is not null)
        {
            services.AddSingleton<WebhookNotifier>();
        }

        // El fan-out arma su lista de destinos a mano (log siempre, webhook si está
        // configurado) en vez de dejar que el contenedor resuelva IEnumerable<INotifier>: así
        // se puede registrar el propio CompositeNotifier como el único INotifier que ven los
        // consumidores externos al fan-out (spec 08 y NotificationActivities) sin la recursión
        // que traería incluirse a sí mismo en esa misma colección.
        services.AddSingleton(sp =>
        {
            var leaves = new List<INotifier> { sp.GetRequiredService<StructuredLogNotifier>() };
            if (sp.GetService<WebhookNotifier>() is { } webhook)
            {
                leaves.Add(webhook);
            }

            return new CompositeNotifier(leaves);
        });
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<CompositeNotifier>());
        services.AddSingleton<NotificationActivities>();

        return services;
    }
}
