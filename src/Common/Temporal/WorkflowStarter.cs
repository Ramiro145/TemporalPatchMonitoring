using System;
using System.Threading.Tasks;
using Contracts.Monitor;
using Temporalio.Client;
using System.Linq.Expressions;

namespace Common
{
    public static class WorkflowStarter
    {
        // Para workflows que devuelven resultado. Devuelve el workflowId generado para que
        // el llamador (p. ej. MonitorApi) lo exponga en su respuesta HTTP (spec 01).
        public static async Task<string> StartAsync<TWorkflow, TResult>(
            string taskQueue,
            Expression<Func<TWorkflow, Task<TResult>>> workflowCall,
            string workflowIdPrefix)
            where TWorkflow : class
        {
            var clusterOptions = MonitorClusterOptions.FromEnvironment();
            var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
            {
                TargetHost = clusterOptions.Host,
                Namespace = clusterOptions.Namespace
            });

            var workflowId = $"{workflowIdPrefix}-{Guid.NewGuid()}";

            var handle = await client.StartWorkflowAsync<TWorkflow, TResult>(
                workflowCall,
                // WorkflowOptions(string id, string taskQueue): el id va primero.
                new WorkflowOptions(workflowId, taskQueue)
            );

            Console.WriteLine($"Workflow started: {handle.Id}");
            return handle.Id;
        }

        // Para workflows que NO devuelven resultado. Devuelve el workflowId generado.
        public static async Task<string> StartAsync<TWorkflow>(
            string taskQueue,
            Expression<Func<TWorkflow, Task>> workflowCall,
            string workflowIdPrefix)
            where TWorkflow : class
        {
            var clusterOptions = MonitorClusterOptions.FromEnvironment();
            var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
            {
                TargetHost = clusterOptions.Host,
                Namespace = clusterOptions.Namespace
            });

            var workflowId = $"{workflowIdPrefix}-{Guid.NewGuid()}";

            var handle = await client.StartWorkflowAsync<TWorkflow>(
                workflowCall,
                // WorkflowOptions(string id, string taskQueue): el id va primero.
                new WorkflowOptions(workflowId, taskQueue)
            );

            Console.WriteLine($"Workflow started: {handle.Id}");
            return handle.Id;
        }
    }
}