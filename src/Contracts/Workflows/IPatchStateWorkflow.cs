using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Temporalio.Workflows;

namespace Contracts.Workflows;

/// <summary>
/// Contrato compartido entre cliente y worker del entity workflow que persiste el estado
/// durable de un patch (spec 05). Uno por <see cref="PatchKey"/>, con
/// <c>WorkflowId = key.ToWorkflowId()</c>, arrancado por <c>signal-with-start</c> y mantenido
/// vivo indefinidamente por <c>Continue-As-New</c>.
/// </summary>
/// <remarks>
/// <see cref="RecordAssessmentAsync"/> es un signal (alta frecuencia, sin respuesta, no debe
/// bloquear la pasada de monitoreo); el override se escribe por <c>[WorkflowUpdate]</c> porque
/// necesita rechazo sincrónico de una fase inválida vía <c>[WorkflowUpdateValidator]</c>.
/// </remarks>
[Workflow]
public interface IPatchStateWorkflow
{
    /// <param name="key">Identidad del patch cuyo estado acumula este entity.</param>
    /// <param name="carryover">
    /// Estado arrastrado desde la ejecución anterior tras un <c>Continue-As-New</c>, o
    /// <c>null</c> en el primer arranque.
    /// </param>
    [WorkflowRun]
    Task RunAsync(PatchKey key, PatchState? carryover);

    /// <summary>Registra un assessment de la pasada de monitoreo; avanza <c>AssessmentCount</c> y, si el veredicto cambió, <c>Revision</c>.</summary>
    [WorkflowSignal]
    Task RecordAssessmentAsync(PatchAssessmentInput input);

    /// <summary>Estado durable vigente del patch.</summary>
    [WorkflowQuery]
    PatchState GetState();

    /// <summary>Impone un override de fase. El validator rechaza fases fuera de <c>{Coexistence, Deprecated, Clean}</c>.</summary>
    [WorkflowUpdate]
    Task<PatchState> SetOverrideAsync(PhaseOverride ov);

    /// <summary>Quita el override vigente; el estado vuelve a la fase inferida en el siguiente assessment.</summary>
    [WorkflowUpdate]
    Task<PatchState> ClearOverrideAsync();
}
