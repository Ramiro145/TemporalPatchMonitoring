namespace Contracts;

/// <summary>
/// Constantes de task queue compartidas entre el cliente (MonitorApi) y el worker (PatchMonitor).
/// </summary>
public static class TaskQueues
{
    public const string PatchMonitor = "patch-monitor-task-queue";
}
