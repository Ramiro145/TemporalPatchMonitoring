namespace Contracts.Monitor;

/// <summary>
/// Puerto de lectura de las corridas recientes de <c>MonitorWorkflow</c> (spec 11): lo que hoy
/// solo se ve en la Event History del cluster propio (Temporal UI en <c>:8234</c>) o vía SDK.
/// Mismo estilo que <see cref="IScheduleController"/>: solo lectura, nunca lanza por una corrida
/// individual ilegible.
/// </summary>
public interface IMonitorRunReader
{
    /// <summary>
    /// Las <paramref name="limit"/> corridas más recientes de <c>MonitorWorkflow</c>, más
    /// nuevas primero.
    /// </summary>
    Task<IReadOnlyList<MonitorRunView>> ListRecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// Una corrida de <c>MonitorWorkflow</c>. <see cref="Summary"/> es <c>null</c> cuando la corrida
/// sigue <c>Running</c>, no cerró con éxito, o su Event History ya fue purgada por retención —
/// nunca se lanza una excepción por esto.
/// </summary>
public sealed record MonitorRunView(
    string WorkflowId,
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    string Status,
    MonitorRunSummary? Summary);
