using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Worker;

namespace Common
{
    public static class WorkerHost
    {
        // Cuánto espera el Worker a que terminen las Activities en vuelo antes de
        // cerrar tras recibir la señal de apagado (spec 04). El stop_grace_period
        // de docker-compose debe ser mayor que esto para que Docker no mande SIGKILL
        // antes de que el drenaje termine.
        public static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Registra un workflow y múltiples clases de actividades resueltas desde el contenedor DI.
        /// Cada instancia de actividad se resuelve vía GetRequiredService para respetar el ciclo de vida configurado.
        /// </summary>
        public static async Task RunAsync<TWorkflow>(
            string taskQueue,
            IServiceProvider serviceProvider,
            IEnumerable<Type> activityTypes,
            IEnumerable<Type>? additionalWorkflowTypes = null,
            CancellationToken cancellationToken = default)
            where TWorkflow : class
        {
            var client = await ConnectAsync();

            var options = new TemporalWorkerOptions(taskQueue)
            {
                GracefulShutdownTimeout = GracefulShutdownTimeout
            };
            options.AddWorkflow<TWorkflow>();

            // Child Workflows deben registrarse en el mismo Worker que los ejecuta
            // (aquí, la misma task queue del parent) para que puedan resolverse.
            if (additionalWorkflowTypes is not null)
                foreach (var workflowType in additionalWorkflowTypes)
                    options.AddWorkflow(workflowType);

            foreach (var activityType in activityTypes)
            {
                var instance = serviceProvider.GetRequiredService(activityType);
                options.AddAllActivities(activityType, instance);
            }

            using var worker = new TemporalWorker(client, options);
            await RunWithGracefulShutdownAsync(worker, taskQueue, cancellationToken);
        }

        /// <summary>
        /// Overload original para compatibilidad con workers que tienen una sola clase de actividades.
        /// </summary>
        public static async Task RunAsync<TWorkflow, TActivities>(
            string taskQueue,
            TActivities activities,
            CancellationToken cancellationToken = default)
            where TWorkflow : class
            where TActivities : class
        {
            var client = await ConnectAsync();

            var options = new TemporalWorkerOptions(taskQueue)
            {
                GracefulShutdownTimeout = GracefulShutdownTimeout
            };
            options.AddAllActivities(activities);
            options.AddWorkflow<TWorkflow>();

            using var worker = new TemporalWorker(client, options);
            await RunWithGracefulShutdownAsync(worker, taskQueue, cancellationToken);
        }

        private static async Task<TemporalClient> ConnectAsync()
        {
            var temporalTarget = Environment.GetEnvironmentVariable("TEMPORAL_HOST") ?? "temporal:7233";

            return await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
            {
                TargetHost = temporalTarget
            });
        }

        /// <summary>
        /// Corre el Worker hasta recibir una señal de apagado (Ctrl+C en local, SIGTERM
        /// desde <c>docker compose stop</c>) y entonces drena: deja de tomar tareas nuevas
        /// y espera hasta <see cref="GracefulShutdownTimeout"/> a que terminen las
        /// Activities en vuelo, en vez de morir de golpe (spec 04).
        /// Público para que workers que arman su propio <see cref="TemporalWorker"/>
        /// (p. ej. OrderReport, que preconstruye el <see cref="TemporalClient"/> para DI)
        /// reusen el mismo drenaje sin pasar por <see cref="RunAsync{TWorkflow}"/>.
        /// Recordá fijar <c>GracefulShutdownTimeout = WorkerHost.GracefulShutdownTimeout</c>
        /// en las <see cref="TemporalWorkerOptions"/> antes de construir el Worker.
        /// </summary>
        public static async Task RunWithGracefulShutdownAsync(
            TemporalWorker worker, string taskQueue, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            void Drain(string source)
            {
                if (cts.IsCancellationRequested)
                    return;

                Console.WriteLine(
                    $"Worker draining ({source} received), waiting up to " +
                    $"{GracefulShutdownTimeout.TotalSeconds:0}s for in-flight activities...");
                cts.Cancel();
            }

            void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true; // evita que el runtime mate el proceso antes de drenar
                Drain("Ctrl+C");
            }

            Console.CancelKeyPress += OnCancelKeyPress;

            // SIGTERM no existe en Windows; PosixSignalRegistration lo tolera en local
            // y solo importa dentro del contenedor (docker compose stop).
            PosixSignalRegistration? sigterm = null;
            try
            {
                sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    Drain("SIGTERM");
                });
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException)
            {
                // Plataforma sin SIGTERM: Ctrl+C sigue cubriendo el apagado local.
            }

            try
            {
                Console.WriteLine($"Worker listening on '{taskQueue}'...");
                await worker.ExecuteAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Drenaje solicitado: el Worker ya esperó a las Activities en vuelo.
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
                sigterm?.Dispose();
            }

            Console.WriteLine("Worker stopped cleanly.");
        }
    }
}
