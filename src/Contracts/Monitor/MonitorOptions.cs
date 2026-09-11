namespace Contracts.Monitor;

/// <summary>
/// Parámetros del Temporal Schedule que dispara <c>MonitorWorkflow</c> y de la pasada que este
/// ejecuta en cada tick: el id del Schedule, cada cuánto dispara, la ventana de recuperación de
/// ticks perdidos, el tope de patches procesados por corrida y la task queue donde corre. Los
/// valores salen de variables de entorno (ver <see cref="FromEnvironment"/>); un valor ausente,
/// no numérico o no positivo cae al default sin lanzar, igual que
/// <see cref="Discovery.DiscoveryOptions"/> y <see cref="Phase.PhaseOptions"/>.
/// </summary>
public sealed record MonitorOptions(
    string ScheduleId,
    TimeSpan Interval,
    TimeSpan CatchupWindow,
    int MaxPatchesPerRun,
    string TaskQueue)
{
    /// <summary>Id del Schedule por defecto cuando <c>MONITOR_SCHEDULE_ID</c> no está.</summary>
    public const string DefaultScheduleId = "patch-monitor-schedule";

    /// <summary>Intervalo por defecto, en minutos, entre corridas.</summary>
    public const int DefaultIntervalMinutes = 5;

    /// <summary>Ventana por defecto, en minutos, para recuperar ticks perdidos.</summary>
    public const int DefaultCatchupWindowMinutes = 10;

    /// <summary>Tope por defecto de patches procesados por corrida.</summary>
    public const int DefaultMaxPatchesPerRun = 50;

    /// <summary>
    /// Lee <c>MONITOR_SCHEDULE_ID</c>, <c>MONITOR_INTERVAL_MINUTES</c>,
    /// <c>MONITOR_CATCHUP_WINDOW_MINUTES</c>, <c>MONITOR_MAX_PATCHES_PER_RUN</c> y
    /// <c>MONITOR_TASK_QUEUE</c>. Cualquiera que falte, no parsee como entero o no sea positiva
    /// usa su default (<see cref="TaskQueues.PatchMonitor"/> para la task queue). Nunca lanza.
    /// </summary>
    public static MonitorOptions FromEnvironment()
    {
        var scheduleId = Environment.GetEnvironmentVariable("MONITOR_SCHEDULE_ID");
        scheduleId = string.IsNullOrWhiteSpace(scheduleId) ? DefaultScheduleId : scheduleId.Trim();

        var taskQueue = Environment.GetEnvironmentVariable("MONITOR_TASK_QUEUE");
        taskQueue = string.IsNullOrWhiteSpace(taskQueue) ? TaskQueues.PatchMonitor : taskQueue.Trim();

        return new MonitorOptions(
            scheduleId,
            TimeSpan.FromMinutes(PositiveIntOrDefault("MONITOR_INTERVAL_MINUTES", DefaultIntervalMinutes)),
            TimeSpan.FromMinutes(PositiveIntOrDefault("MONITOR_CATCHUP_WINDOW_MINUTES", DefaultCatchupWindowMinutes)),
            PositiveIntOrDefault("MONITOR_MAX_PATCHES_PER_RUN", DefaultMaxPatchesPerRun),
            taskQueue);
    }

    private static int PositiveIntOrDefault(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
