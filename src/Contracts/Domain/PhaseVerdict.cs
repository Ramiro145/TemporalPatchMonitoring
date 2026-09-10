namespace Contracts.Domain;

/// <summary>
/// Resultado de evaluar un gate de salto de fase para un patch. Lleva lo que la API
/// (spec 08) y el notificador (spec 07) necesitan sin volver a consultar Temporal:
/// el conteo total de ejecuciones bloqueantes y una muestra acotada de sus
/// <c>workflowId</c>.
/// </summary>
public sealed record PhaseVerdict(
    GateOutcome Outcome,
    PatchPhase CurrentPhase,
    PatchPhase? NextPhase,
    int BlockingExecutionCount,
    IReadOnlyList<string> BlockingSample,
    string Reason,
    DateTimeOffset EvaluatedAt)
{
    /// <summary>Tope de elementos en <see cref="BlockingSample"/>.</summary>
    public const int MaxBlockingSample = 5;

    /// <summary>El salto está habilitado: no queda ninguna ejecución bloqueante y los datos son completos.</summary>
    public static PhaseVerdict Ready(
        PatchPhase currentPhase,
        PatchPhase nextPhase,
        string reason,
        DateTimeOffset evaluatedAt) =>
        new(GateOutcome.Ready, currentPhase, nextPhase, 0, Array.Empty<string>(), reason, evaluatedAt);

    /// <summary>Hay ejecuciones que impiden el salto. <paramref name="blockingSample"/> se recorta a <see cref="MaxBlockingSample"/>.</summary>
    public static PhaseVerdict Blocked(
        PatchPhase currentPhase,
        PatchPhase? nextPhase,
        int blockingExecutionCount,
        IReadOnlyList<string> blockingSample,
        string reason,
        DateTimeOffset evaluatedAt) =>
        new(
            GateOutcome.Blocked,
            currentPhase,
            nextPhase,
            blockingExecutionCount,
            Cap(blockingSample),
            reason,
            evaluatedAt);

    /// <summary>Los datos disponibles no alcanzan para afirmar ni bloquear el salto.</summary>
    public static PhaseVerdict Inconclusive(
        PatchPhase currentPhase,
        PatchPhase? nextPhase,
        string reason,
        DateTimeOffset evaluatedAt) =>
        new(GateOutcome.Inconclusive, currentPhase, nextPhase, 0, Array.Empty<string>(), reason, evaluatedAt);

    private static IReadOnlyList<string> Cap(IReadOnlyList<string> sample) =>
        sample.Count <= MaxBlockingSample
            ? sample
            : sample.Take(MaxBlockingSample).ToArray();
}
