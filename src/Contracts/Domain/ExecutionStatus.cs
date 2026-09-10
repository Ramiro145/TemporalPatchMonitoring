namespace Contracts.Domain;

/// <summary>
/// Estado de una ejecución de workflow de Temporal, en la forma que el dominio necesita.
/// No depende de <c>Temporalio</c>: el spec 03 mapea el enum del SDK a este.
/// </summary>
public enum ExecutionStatus
{
    Unknown = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Canceled = 4,
    Terminated = 5,
    ContinuedAsNew = 6,
    TimedOut = 7,
}

/// <summary>Extensiones de <see cref="ExecutionStatus"/>.</summary>
public static class ExecutionStatusExtensions
{
    /// <summary>
    /// <c>true</c> si la ejecución sigue abierta. Solo <see cref="ExecutionStatus.Running"/> y
    /// <see cref="ExecutionStatus.ContinuedAsNew"/> cuentan como abiertas; el resto (incluido
    /// <see cref="ExecutionStatus.Unknown"/>) se trata como cerrado.
    /// </summary>
    public static bool IsOpen(this ExecutionStatus status) =>
        status is ExecutionStatus.Running or ExecutionStatus.ContinuedAsNew;
}
