namespace Contracts.State;

/// <summary>
/// Parámetros del estado durable (spec 05): cada cuántos assessments el entity workflow hace
/// <c>Continue-As-New</c>, cuántas entradas del ring buffer de cambios sobreviven a ese salto
/// y en qué task queue el store arranca los entity workflows. Los valores salen de variables
/// de entorno (ver <see cref="FromEnvironment"/>); un valor ausente, no numérico o no positivo
/// cae al default sin lanzar, igual que <see cref="Discovery.DiscoveryOptions"/> y
/// <see cref="Phase.PhaseOptions"/>.
/// </summary>
public sealed record StateOptions(
    int ContinueAsNewThreshold,
    int HistoryLimit,
    string TaskQueue)
{
    /// <summary>Assessments recibidos antes de hacer <c>Continue-As-New</c> cuando <c>PATCH_STATE_CAN_THRESHOLD</c> no está.</summary>
    public const int DefaultContinueAsNewThreshold = 500;

    /// <summary>Tope del ring buffer de cambios cuando <c>PATCH_STATE_HISTORY_LIMIT</c> no está.</summary>
    public const int DefaultHistoryLimit = 20;

    /// <summary><c>WorkflowId</c> fijo y único del registry singleton.</summary>
    public const string RegistryWorkflowId = "patch-registry";

    /// <summary>
    /// Lee <c>PATCH_STATE_CAN_THRESHOLD</c>, <c>PATCH_STATE_HISTORY_LIMIT</c> y
    /// <c>MONITOR_TASK_QUEUE</c>. Cualquiera que falte, no parsee como entero o no sea
    /// positiva usa su default (<see cref="TaskQueues.PatchMonitor"/> para la task queue).
    /// Nunca lanza.
    /// </summary>
    public static StateOptions FromEnvironment()
    {
        var taskQueue = Environment.GetEnvironmentVariable("MONITOR_TASK_QUEUE");
        taskQueue = string.IsNullOrWhiteSpace(taskQueue) ? TaskQueues.PatchMonitor : taskQueue.Trim();

        return new StateOptions(
            PositiveIntOrDefault("PATCH_STATE_CAN_THRESHOLD", DefaultContinueAsNewThreshold),
            PositiveIntOrDefault("PATCH_STATE_HISTORY_LIMIT", DefaultHistoryLimit),
            taskQueue);
    }

    private static int PositiveIntOrDefault(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
