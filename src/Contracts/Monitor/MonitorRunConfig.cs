namespace Contracts.Monitor;

/// <summary>
/// Configuración que <c>MonitorWorkflow</c> necesita en cada pasada, resuelta por una Activity
/// (<c>ConfigActivities.GetMonitorRunConfig</c>) para que quede grabada en la Event History: leer
/// el entorno dentro del código de workflow no es determinístico (spec 14).
/// </summary>
public sealed record MonitorRunConfig(
    int MaxPatchesPerRun,
    bool NotificationsEnabled,
    int NotifierMaxAttempts);
