namespace Contracts.Monitor;

/// <summary>
/// Puerto de operación del Temporal Schedule que dispara <c>MonitorWorkflow</c> (spec 06):
/// describir su estado, pausarlo/reanudarlo y disparar una corrida fuera de ciclo. Lo que el
/// <c>ScheduleBootstrapper</c> no cubre porque solo sabe crearlo.
///
/// "No existe" se devuelve como <c>null</c>/<c>false</c> —nunca como excepción— con el mismo
/// criterio que <c>WorkflowValidator</c>: distinguir "el cluster está vivo pero eso no existe" de
/// "el cluster es inalcanzable". Lo segundo sí propaga.
/// </summary>
public interface IScheduleController
{
    /// <summary>Estado vigente del Schedule, o null si no existe todavía.</summary>
    Task<ScheduleStatus?> DescribeAsync(CancellationToken ct = default);

    /// <summary>Pausa el Schedule; false si no existe. Idempotente.</summary>
    Task<bool> PauseAsync(string? note, CancellationToken ct = default);

    /// <summary>Reanuda el Schedule; false si no existe. Idempotente.</summary>
    Task<bool> UnpauseAsync(string? note, CancellationToken ct = default);

    /// <summary>Dispara una corrida fuera de ciclo; false si el Schedule no existe.</summary>
    Task<bool> TriggerAsync(CancellationToken ct = default);
}

public sealed record ScheduleStatus(
    string ScheduleId,
    bool Paused,
    string? Note,
    TimeSpan Interval,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    int RunningCount,
    long NumActions);
