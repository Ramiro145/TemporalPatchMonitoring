using Contracts.Domain;

namespace Contracts.State;

/// <summary>
/// Puerto aditivo para volcar cada veredicto persistido a un store externo (SQL u otro) para
/// auditoría histórica de largo plazo. No-op por default (<c>NoopDecisionSink</c>); un
/// adaptador real es trabajo futuro. Se invoca desde la Activity y su fallo se traga: un sink
/// externo caído no puede tirar abajo el monitoreo.
/// </summary>
public interface IDecisionSink
{
    /// <summary>Registra el estado durable recién escrito de un patch en el sink externo.</summary>
    Task RecordAsync(PatchKey key, PatchState state, CancellationToken ct = default);
}
