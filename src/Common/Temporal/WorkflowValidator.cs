using Temporalio.Client;
using Temporalio.Exceptions;
namespace Common
{
    public static class WorkflowValidator
    {
        /// <summary>
        /// Texto de <c>Error</c> cuando el workflow no existe. Es solo para mostrar: para
        /// distinguir "el cluster está vivo pero ese workflow no existe" de "el cluster es
        /// inalcanzable" los llamadores usan el booleano <c>NotFound</c>, no este string.
        /// </summary>
        public const string NotFoundError = "Workflow no encontrado en Temporal";

        /// <remarks>
        /// <c>NotFound</c> es <c>true</c> solo si el servidor respondió <c>StatusCode.NotFound</c>.
        /// Verificado en vivo (spec 16, B-8) contra Temporal con Postgres y SDK 1.9.0: un id
        /// inexistente da siempre ese código, así que no se compara texto de error.
        /// </remarks>
        public static async Task<(bool Exists, bool NotFound, WorkflowExecutionDescription? Info, string? Error)>
            ValidateWorkflowAsync(ITemporalClient client, string workflowId)
        {
            try
            {
                var handle = client.GetWorkflowHandle(workflowId);

                // DescribeAsync valida existencia y estado
                var info = await handle.DescribeAsync();

                return (true, false, info, null);
            }
            catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
            {
                return (false, true, null, NotFoundError);
            }
            catch (Exception ex)
            {
                return (false, false, null, $"Error inesperado: {ex.Message}");
            }
        }
    }
}
