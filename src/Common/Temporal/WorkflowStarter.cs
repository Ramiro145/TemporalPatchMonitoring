using System;
using System.Threading.Tasks;
using Temporalio.Client;
using System.Linq.Expressions;

namespace Common
{
    public static class WorkflowStarter
    {
        // Para workflows que devuelven resultado. Devuelve el workflowId generado para que
        // el llamador (p. ej. MonitorApi) lo exponga en su respuesta HTTP (spec 01).
        // Recibe el cliente ya conectado (spec 15, M-2): abrir uno por llamada sin cerrarlo
        // agotaba las conexiones del proceso.
        public static async Task<string> StartAsync<TWorkflow, TResult>(
            ITemporalClient client,
            string taskQueue,
            Expression<Func<TWorkflow, Task<TResult>>> workflowCall,
            string workflowIdPrefix)
            where TWorkflow : class
        {
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
            ITemporalClient client,
            string taskQueue,
            Expression<Func<TWorkflow, Task>> workflowCall,
            string workflowIdPrefix)
            where TWorkflow : class
        {
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
