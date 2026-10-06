using System;
using System.Linq;
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

        /// <summary>
        /// La acción del Schedule. Fija <c>ExecutionTimeout</c> = <see cref="MonitorOptions.RunTimeout"/>
        /// (spec 18): con <c>Overlap = Skip</c>, una corrida que queda abierta para siempre haría que
        /// el Schedule salte todos los ticks siguientes; con el tope se cierra sola.
        /// </summary>
        public static ScheduleActionStartWorkflow BuildAction(MonitorOptions options) =>
            ScheduleActionStartWorkflow.Create(
                (IMonitorWorkflow wf) => wf.RunAsync(),
                new WorkflowOptions(RunWorkflowIdPrefix, options.TaskQueue)
                {
                    ExecutionTimeout = options.RunTimeout,
                });

        /// <summary>
        /// Deja el Schedule como lo describe <paramref name="options"/> (spec 15, M-3): lo crea si
        /// no existe; si ya existía y difiere (<see cref="Differs"/>), lo actualiza conservando su
        /// estado (pausado y nota); si coincide, no escribe nada. Cualquier otro error de RPC
        /// propaga.
        /// </summary>
        public static async Task<ScheduleEnsureResult> EnsureScheduleAsync(
            ITemporalClient client, MonitorOptions options)
        {
            var desired = new Schedule(BuildAction(options), BuildSpec(options))
            {
                Policy = BuildPolicy(options),
            };

            try
            {
                await client.CreateScheduleAsync(options.ScheduleId, desired).ConfigureAwait(false);
                return ScheduleEnsureResult.Created;
            }
            catch (ScheduleAlreadyRunningException)
            {
                // Ya existía: se compara contra el vigente más abajo.
            }

            var handle = client.GetScheduleHandle(options.ScheduleId);
            var description = await handle.DescribeAsync().ConfigureAwait(false);
            if (!Differs(description.Schedule, options))
            {
                return ScheduleEnsureResult.Unchanged;
            }

            await handle.UpdateAsync(input => new ScheduleUpdate(new Schedule(desired.Action, desired.Spec)
            {
                Policy = desired.Policy,
                // Pausa y nota las maneja el operador (API de control), no la configuración.
                State = input.Description.Schedule.State,
            })).ConfigureAwait(false);
            return ScheduleEnsureResult.Updated;
        }

        /// <summary>
        /// Pura y sin cluster: <c>true</c> si el Schedule vigente se aparta de lo que gobierna
        /// <paramref name="desired"/> — intervalo, <c>CatchupWindow</c>, <c>Overlap</c>, task queue,
        /// workflow type y <c>ExecutionTimeout</c> de la acción. Un Schedule creado antes del spec 18
        /// no lo tiene (<c>null</c>), así que difiere y se actualiza solo. No compara el estado
        /// (pausa y nota).
        /// </summary>
        public static bool Differs(Schedule current, MonitorOptions desired)
        {
            var intervals = current.Spec.Intervals;
            if (intervals is null || intervals.Count != 1 || intervals.First().Every != desired.Interval)
            {
                return true;
            }

            if (current.Policy.CatchupWindow != desired.CatchupWindow ||
                current.Policy.Overlap != ScheduleOverlapPolicy.Skip)
            {
                return true;
            }

            if (current.Action is not ScheduleActionStartWorkflow action)
            {
                return true;
            }

            var expected = BuildAction(desired);
            return action.Workflow != expected.Workflow ||
                   action.Options.TaskQueue != desired.TaskQueue ||
                   action.Options.ExecutionTimeout != desired.RunTimeout;
        }
    }

    /// <summary>Resultado de <see cref="ScheduleBootstrapper.EnsureScheduleAsync"/>.</summary>
    public enum ScheduleEnsureResult
    {
        Created,
        Updated,
        Unchanged,
    }
}
