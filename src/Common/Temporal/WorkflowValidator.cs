using Temporalio.Client;
using Temporalio.Exceptions;
namespace Common
{
    public static class WorkflowValidator
    {
        /// <summary>
        /// Mensaje canónico de "no encontrado". Los llamadores lo comparan para distinguir
        /// "el cluster está vivo pero ese workflow no existe" de "el cluster es inalcanzable".
        /// </summary>
        public const string NotFoundError = "Workflow no encontrado en Temporal";

        public static async Task<(bool Exists, WorkflowExecutionDescription? Info, string? Error)>
            ValidateWorkflowAsync(ITemporalClient client, string workflowId)
        {
            try
            {
                var handle = client.GetWorkflowHandle(workflowId);

                // DescribeAsync valida existencia y estado
                var info = await handle.DescribeAsync();

                return (true, info, null);
            }
            catch (RpcException ex) when (
                ex.Code == RpcException.StatusCode.NotFound ||
                ex.Message.Contains("no rows in result set"))
            {
                // NotFound: test-server y clusters recientes. "no rows in result set": standard
                // visibility sobre Postgres del proyecto de referencia.
                return (false, null, NotFoundError);
            }
            catch (Exception ex)
            {
                return (false, null, $"Error inesperado: {ex.Message}");
            }
        }
    }
}
