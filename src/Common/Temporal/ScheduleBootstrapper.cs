using System;
using System.Threading.Tasks;
using Contracts.Monitor;
using Contracts.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace Common
{
    /// <summary>
    /// Plumbing genérico del Temporal Schedule que dispara <c>MonitorWorkflow</c> (spec 06).
    /// Tres builders puros —testeables sin cluster— y un método de efecto que crea el Schedule
    /// de forma idempotente.
    /// </summary>
    public static class ScheduleBootstrapper
    {
        /// <summary>Prefijo fijo del <c>WorkflowId</c> de cada corrida disparada por el Schedule.</summary>
        public const string RunWorkflowIdPrefix = "patch-monitor-run";

        public static ScheduleSpec BuildSpec(MonitorOptions options) =>
            new()
            {
                Intervals = new[] { new ScheduleIntervalSpec(options.Interval) },
            };

        public static SchedulePolicy BuildPolicy(MonitorOptions options) =>
            new()
            {
                Overlap = ScheduleOverlapPolicy.Skip,
                CatchupWindow = options.CatchupWindow,
            };

        public static ScheduleActionStartWorkflow BuildAction(MonitorOptions options) =>
            ScheduleActionStartWorkflow.Create(
                (IMonitorWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions(RunWorkflowIdPrefix, options.TaskQueue));

        /// <summary>
        /// Crea el Schedule si todavía no existe. Idempotente: un segundo arranque del worker
        /// captura <see cref="ScheduleAlreadyRunningException"/> y devuelve <c>false</c>;
        /// cualquier otro error de RPC propaga.
        /// </summary>
        /// <returns><c>true</c> si lo creó en esta llamada, <c>false</c> si ya existía.</returns>
        public static async Task<bool> EnsureScheduleAsync(ITemporalClient client, MonitorOptions options)
        {
            var schedule = new Schedule(BuildAction(options), BuildSpec(options))
            {
                Policy = BuildPolicy(options),
            };

            try
            {
                await client.CreateScheduleAsync(options.ScheduleId, schedule).ConfigureAwait(false);
                return true;
            }
            catch (ScheduleAlreadyRunningException)
            {
                return false;
            }
        }
    }
}
