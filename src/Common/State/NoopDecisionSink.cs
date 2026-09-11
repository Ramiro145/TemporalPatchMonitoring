using Contracts.Domain;
using Contracts.State;

namespace Common.State;

/// <summary>
/// Implementación no-op de <see cref="IDecisionSink"/>, registrada por default. Un adaptador
/// real (SQL u otro) para la auditoría histórica de largo plazo es trabajo futuro; hasta
/// entonces el monitor no vuelca nada a ningún store externo.
/// </summary>
public sealed class NoopDecisionSink : IDecisionSink
{
    public Task RecordAsync(PatchKey key, PatchState state, CancellationToken ct = default) =>
        Task.CompletedTask;
}
