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
    /// <param name="options">
    /// Opciones del estado durable con las que corre esta ejecución (spec 14). El
    /// <c>Continue-As-New</c> las arrastra. <c>null</c> solo en ejecuciones vivas arrancadas antes
    /// de este argumento: esas no leen el entorno (spec 17) ni hacen <c>Continue-As-New</c> hasta
    /// recibir <see cref="MigrateOptionsAsync"/>.
    /// </param>
    [WorkflowRun]
    Task RunAsync(PatchKey key, PatchState? carryover, StateOptions? options);

    /// <summary>
    /// Spec 17: graba las opciones en una ejecución arrancada sin ellas (anterior al spec 14). Es
    /// idempotente: si la ejecución ya tiene opciones no hace nada. Desde que las graba, la
    /// ejecución respeta el umbral de <c>Continue-As-New</c> y lo arrastra, así que queda
    /// determinística sin leer el entorno.
    /// </summary>
    [WorkflowSignal]
    Task MigrateOptionsAsync(StateOptions options);

    /// <summary>
    /// <c>true</c> si la ejecución ya tiene opciones grabadas (arrancó con ellas o las recibió por
    /// <see cref="MigrateOptionsAsync"/>); <c>false</c> en una ejecución antigua sin migrar.
    /// </summary>
    [WorkflowQuery]
    bool HasRecordedOptions();

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

    /// <summary>
    /// Reclama el derecho a notificar <paramref name="revision"/>: devuelve <c>true</c> y avanza
    /// <see cref="PatchState.NotifiedRevision"/> solo si <paramref name="revision"/> es mayor que
    /// el vigente; si no, devuelve <c>false</c> sin tocar el estado. Va por
    /// <c>[WorkflowUpdate]</c>, no por signal, porque el llamador necesita la respuesta
    /// sincrónica para decidir si además dispara el envío.
    /// </summary>
    [WorkflowUpdate]
    Task<bool> TryClaimNotificationAsync(int revision);
}
