using System;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Common
{
    /// <summary>
    /// Crea el namespace propio del monitor si todavía no existe en el cluster al que se conecta
    /// (spec 12). Idempotente, mismo contrato de efecto que
    /// <see cref="ScheduleBootstrapper.EnsureScheduleAsync"/>: opera directo sobre
    /// <c>ITemporalClient.WorkflowService</c>, sin tipo de estado propio.
    /// </summary>
    public static class NamespaceBootstrapper
    {
        /// <summary>Retención por defecto, en días, cuando <c>MONITOR_NAMESPACE_RETENTION_DAYS</c> no está.</summary>
        public const int DefaultRetentionDays = 7;

        // DescribeNamespace confirma el namespace nuevo casi al instante, pero el registro que
        // usan matching/history para servicios que sí lo ejercitan (CreateSchedule, arrancar un
        // Workflow) se refresca en un ciclo propio y más lento: confiar solo en DescribeNamespace
        // deja una ventana real donde el namespace "existe" pero un CreateSchedule inmediato
        // después falla con NotFound (visto en la verificación e2e del spec 12). Por eso, tras
        // registrar, esperamos la ventana completa en vez de salir apenas DescribeNamespace lo ve.
        private static readonly TimeSpan PropagationRetryWindow = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PropagationRetryDelay = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Si el namespace ya existe, no hace nada y devuelve <c>false</c>. Si no existe, lo
        /// registra con la retención de <c>MONITOR_NAMESPACE_RETENTION_DAYS</c> (default 7 días,
        /// nunca lanza si falta o es inválida) y espera acotadamente a que quede visible antes de
        /// devolver <c>true</c>.
        /// </summary>
        public static async Task<bool> EnsureNamespaceAsync(ITemporalClient client, string @namespace)
        {
            if (await NamespaceExistsAsync(client, @namespace).ConfigureAwait(false))
                return false;

            var retentionDays = PositiveIntOrDefault(
                "MONITOR_NAMESPACE_RETENTION_DAYS", DefaultRetentionDays);

            try
            {
                await client.WorkflowService.RegisterNamespaceAsync(new RegisterNamespaceRequest
                {
                    Namespace = @namespace,
                    WorkflowExecutionRetentionPeriod =
                        Duration.FromTimeSpan(TimeSpan.FromDays(retentionDays)),
                }).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.AlreadyExists)
            {
                return false;
            }

            await WaitForPropagationAsync(client, @namespace).ConfigureAwait(false);
            return true;
        }

        private static async Task<bool> NamespaceExistsAsync(ITemporalClient client, string @namespace)
        {
            try
            {
                await client.WorkflowService.DescribeNamespaceAsync(
                    new DescribeNamespaceRequest { Namespace = @namespace }).ConfigureAwait(false);
                return true;
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
            {
                return false;
            }
        }

        private static async Task WaitForPropagationAsync(ITemporalClient client, string @namespace)
        {
            // Espera a que DescribeNamespace confirme el namespace (paso rápido) y después agota
            // el resto de la ventana igual, para darle tiempo al registro de matching/history.
            var deadline = DateTime.UtcNow + PropagationRetryWindow;
            while (DateTime.UtcNow < deadline)
            {
                if (await NamespaceExistsAsync(client, @namespace).ConfigureAwait(false))
                    break;

                await Task.Delay(PropagationRetryDelay).ConfigureAwait(false);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining).ConfigureAwait(false);
        }

        private static int PositiveIntOrDefault(string variable, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(variable);
            return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
        }
    }
}
