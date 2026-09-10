using System;
using System.Threading.Tasks;
using Temporalio.Client;
using System.Linq.Expressions;

namespace Common
{
    public static class WorkflowStarter
    {
        // Para workflows que devuelven resultado
        public static async Task StartAsync<TWorkflow, TResult>(
            string taskQueue,
            Expression<Func<TWorkflow, Task<TResult>>> workflowCall,
            string workflowIdPrefix)
            where TWorkflow : class
        {
            var temporalTarget = Environment.GetEnvironmentVariable("TEMPORAL_HOST") ?? "temporal:7233";
            var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions { TargetHost = temporalTarget });

            var workflowId = $"{workflowIdPrefix}-{Guid.NewGuid()}";

            var handle = await client.StartWorkflowAsync<TWorkflow, TResult>(
                workflowCall,
                new WorkflowOptions(taskQueue, workflowId)
            );

            Console.WriteLine($"Workflow started: {handle.Id}");
        }

        // Para workflows que NO devuelven resultado
        public static async Task StartAsync<TWorkflow>(
            string taskQueue,
            Expression<Func<TWorkflow, Task>> workflowCall,
            string workflowIdPrefix)
            where TWorkflow : class
        {
            var temporalTarget = Environment.GetEnvironmentVariable("TEMPORAL_HOST") ?? "temporal:7233";
            var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions { TargetHost = temporalTarget });

            var workflowId = $"{workflowIdPrefix}-{Guid.NewGuid()}";

            var handle = await client.StartWorkflowAsync<TWorkflow>(
                workflowCall,
                new WorkflowOptions(taskQueue, workflowId)
            );

            Console.WriteLine($"Workflow started: {handle.Id}");
        }
    }
}