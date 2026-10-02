namespace Contracts.Monitor;

/// <summary>
/// Configuración que <c>MonitorWorkflow</c> necesita en cada pasada, resuelta por una Activity
/// (<c>ConfigActivities.GetMonitorRunConfig</c>) para que quede grabada en la Event History: leer
/// el entorno dentro del código de workflow no es determinístico (spec 14).
/// <see cref="IntervalMinutes"/> (spec 15) es la cadencia del Schedule; el workflow la usa para
/// numerar el tick y rotar los patches cuando hay más descubiertos que <see cref="MaxPatchesPerRun"/>.
/// Va al final y con default para que una historia grabada sin el campo siga deserializando.
/// </summary>
public sealed record MonitorRunConfig(
    int MaxPatchesPerRun,
    bool NotificationsEnabled,
    int NotifierMaxAttempts,
    int IntervalMinutes = MonitorOptions.DefaultIntervalMinutes);
