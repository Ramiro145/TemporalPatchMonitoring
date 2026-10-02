using Contracts.Monitor;
using Contracts.Notification;
using Temporalio.Activities;

namespace PatchMonitor.Activities;

/// <summary>
/// Entrega a <c>MonitorWorkflow</c> la configuración de la pasada (spec 14). Un workflow no puede
/// leer variables de entorno sin riesgo de <c>NonDeterminismError</c> en un replay; la Activity sí,
/// y su resultado queda grabado en la historia.
/// </summary>
public class ConfigActivities
{
    private readonly MonitorOptions _monitorOptions;
    private readonly NotificationOptions _notificationOptions;

    public ConfigActivities(MonitorOptions monitorOptions, NotificationOptions notificationOptions)
    {
        _monitorOptions = monitorOptions;
        _notificationOptions = notificationOptions;
    }

    [Activity]
    public MonitorRunConfig GetMonitorRunConfig() =>
        new(
            _monitorOptions.MaxPatchesPerRun,
            _notificationOptions.Enabled,
            _notificationOptions.MaxAttempts);
}
