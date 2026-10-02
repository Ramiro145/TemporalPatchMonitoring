namespace Contracts.Domain;

/// <summary>
/// Estado de una ejecución de workflow, en la forma que el dominio necesita. No depende
/// del SDK: el spec 03 mapea el enum del cliente a este.
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
    /// <c>true</c> si la ejecución sigue abierta. Solo <see cref="ExecutionStatus.Running"/> cuenta
    /// como abierta; el resto (incluido <see cref="ExecutionStatus.Unknown"/>) se trata como
    /// cerrado. Una run <see cref="ExecutionStatus.ContinuedAsNew"/> está cerrada en Temporal: su
    /// sucesora aparece por separado como <c>Running</c> y se evalúa por su cuenta (spec 14).
    /// </summary>
    public static bool IsOpen(this ExecutionStatus status) =>
        status is ExecutionStatus.Running;
}
