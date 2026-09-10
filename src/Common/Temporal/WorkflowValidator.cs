using Temporalio.Client;
using Temporalio.Exceptions;
namespace Common
{
    public static class WorkflowValidator
    {
        public static async Task<(bool Exists, WorkflowExecutionDescription? Info, string? Error)>
            ValidateWorkflowAsync(TemporalClient client, string workflowId)
        {
            try
            {
                var handle = client.GetWorkflowHandle(workflowId);

                // DescribeAsync valida existencia y estado
                var info = await handle.DescribeAsync();

                return (true, info, null);
            }
            catch (RpcException ex) when (ex.Message.Contains("no rows in result set"))
            {
                return (false, null, "Workflow no encontrado en Temporal");
            }
            catch (Exception ex)
            {
                return (false, null, $"Error inesperado: {ex.Message}");
            }
        }
    }
}