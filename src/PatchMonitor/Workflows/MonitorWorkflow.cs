using Contracts.Discovery;
using Contracts.Monitor;
using Contracts.State;
using Contracts.Workflows;
using PatchMonitor.Activities;
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
        var options = MonitorOptions.FromEnvironment();
        var startedAt = Workflow.UtcNow;

        var overridesLoaded = await Workflow
            .ExecuteActivityAsync((PatchStateActivities a) => a.LoadPhaseOverridesAsync(), Options)
            .ConfigureAwait(true);

        var discovered = await Workflow
            .ExecuteActivityAsync((DiscoveryActivities a) => a.DiscoverPatchesAsync(), Options)
            .ConfigureAwait(true);

        var patchesAssessed = 0;
        var verdictsChanged = 0;
        var errors = new List<string>();

        foreach (var patch in discovered.Take(options.MaxPatchesPerRun))
        {
            try
            {
                var before = await Workflow
                    .ExecuteActivityAsync((PatchStateActivities a) => a.GetPatchStateAsync(patch.Key), Options)
                    .ConfigureAwait(true);
                var revisionBefore = before?.Revision ?? 0;

                var assessment = await Workflow
                    .ExecuteActivityAsync((PhaseActivities a) => a.AssessPatch(patch), Options)
                    .ConfigureAwait(true);

                var input = new PatchAssessmentInput(
                    patch.Key, assessment.Resolution, assessment.Verdict, Workflow.UtcNow);

                var state = await Workflow
                    .ExecuteActivityAsync((PatchStateActivities a) => a.RecordAssessmentAsync(input), Options)
                    .ConfigureAwait(true);

                if (state.Revision > revisionBefore)
                {
                    verdictsChanged++;
                }

                patchesAssessed++;
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
            errors);
    }
}
