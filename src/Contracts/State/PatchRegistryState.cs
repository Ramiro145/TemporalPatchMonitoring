using Contracts.Domain;

namespace Contracts.State;

/// <summary>
/// Estado del registry singleton (spec 05): el set de <see cref="PatchKey"/> conocidas más
/// el instante de la última modificación. Es el índice explícito que el spec 08 usa para
/// listar todos los patches sin depender de Visibility (restricción #1).
/// </summary>
public sealed record PatchRegistryState(
    IReadOnlyList<PatchKey> Keys,
    DateTimeOffset UpdatedAt);
