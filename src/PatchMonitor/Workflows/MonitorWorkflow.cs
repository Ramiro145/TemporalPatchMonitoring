using Contracts.Discovery;
using Contracts.Monitor;
using Contracts.Notification;
using Contracts.State;
using Contracts.Workflows;
using PatchMonitor.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace PatchMonitor.Workflows;

/// <summary>
/// La pasada de monitoreo (spec 06): une descubrimiento, resolución de fase, evaluación de
/// gate y persistencia en una sola corrida que un Temporal Schedule dispara cada 5 minutos.
/// Deliberadamente efímero — arranca, recorre los patches descubiertos y cierra devolviendo un
/// <see cref="MonitorRunSummary"/> — sin <c>while</c>, <c>Workflow.DelayAsync</c> ni
/// <c>Continue-As-New</c>: el estado que sobrevive entre pasadas vive en los entity workflows
/// del spec 05.
/// </summary>
[Workflow]
public class MonitorWorkflow : IMonitorWorkflow
{
    private static readonly ActivityOptions Options = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
    };

    [WorkflowRun]
    public async Task<MonitorRunSummary> RunAsync()
    {
        // La configuración llega por Activity (queda en la historia) y no del entorno: leerlo
        // acá no sería determinístico (spec 14).
        var config = await Workflow
            .ExecuteActivityAsync((ConfigActivities a) => a.GetMonitorRunConfig(), Options)
            .ConfigureAwait(true);
        var startedAt = Workflow.UtcNow;

        var overridesLoaded = await Workflow
            .ExecuteActivityAsync((PatchStateActivities a) => a.LoadPhaseOverridesAsync(), Options)
            .ConfigureAwait(true);

        var discovered = await Workflow
            .ExecuteActivityAsync((DiscoveryActivities a) => a.DiscoverPatchesAsync(), Options)
            .ConfigureAwait(true);

        var patchesAssessed = 0;
        var verdictsChanged = 0;
        var notificationsSent = 0;
        var notificationsFailed = 0;
        var errors = new List<string>();

        foreach (var patch in discovered.Take(config.MaxPatchesPerRun))
        {
            try
            {
                var before = await Workflow
                    .ExecuteActivityAsync((PatchStateActivities a) => a.GetPatchStateAsync(patch.Key), Options)
                    .ConfigureAwait(true);
                var revisionBefore = before?.Revision ?? 0;

                var assessment = await Workflow
                    .ExecuteActivityAsync((PhaseActivities a) => a.AssessPatch(patch, before), Options)
                    .ConfigureAwait(true);

                var input = new PatchAssessmentInput(
                    patch.Key, assessment.Resolution, assessment.Verdict, Workflow.UtcNow);

                var state = await Workflow
                    .ExecuteActivityAsync((PatchStateActivities a) => a.RecordAssessmentAsync(input), Options)
                    .ConfigureAwait(true);

                patchesAssessed++;

                if (state.Revision > revisionBefore)
                {
                    verdictsChanged++;
                }

                // Se notifica por "pendiente" y no solo cuando la Revision acaba de avanzar: la
                // Activity reclama la revisión recién después de un envío exitoso, así que un
                // fallo deja NotifiedRevision atrás y el tick siguiente lo reintenta (spec 14).
                // Se avisa solo el estado vigente, no cada revisión intermedia.
                if (config.NotificationsEnabled && state.NotifiedRevision < state.Revision)
                {
                    try
                    {
                        var notification = VerdictChangeNotification.FromState(state);
                        var notifyOptions = new ActivityOptions
                        {
                            StartToCloseTimeout = TimeSpan.FromMinutes(2),
                            RetryPolicy = new RetryPolicy { MaximumAttempts = config.NotifierMaxAttempts },
                        };

                        var sent = await Workflow
                            .ExecuteActivityAsync(
                                (NotificationActivities a) => a.NotifyVerdictChangeAsync(notification),
                                notifyOptions)
                            .ConfigureAwait(true);

                        // "false" significa que esa revisión ya estaba notificada (otra corrida
                        // se adelantó): no es un fallo ni un envío nuevo, no se cuenta.
                        if (sent)
                        {
                            notificationsSent++;
                        }
                    }
                    catch (ActivityFailureException ex)
                    {
                        // Un fallo de notificación no rompe el resto del procesamiento de
                        // este patch: el assessment y la persistencia ya ocurrieron y
                        // cuentan igual, así que no cae en el catch que aborta el patch.
                        notificationsFailed++;
                        errors.Add($"{patch.Key} (notificación): {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }
            catch (ActivityFailureException ex)
            {
                // Aísla el fallo de este patch y sigue con los demás; el mensaje de la causa
                // original (ApplicationFailureException u otra) es lo que interesa loguear.
                errors.Add($"{patch.Key}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        return new MonitorRunSummary(
            startedAt,
            Workflow.UtcNow,
            discovered.Count,
            patchesAssessed,
            verdictsChanged,
            overridesLoaded,
            errors,
            notificationsSent,
            notificationsFailed);
    }
}
