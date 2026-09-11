using Contracts.Domain;

namespace Contracts.Api;

/// <summary>
/// Cuerpo de <c>POST /patches/{ns}/{type}/{patchId}/override</c>. <see cref="ExpiresAt"/>
/// ausente ⇒ <c>DeclaredAt + ApiOptions.OverrideDefaultTtl</c>: un override permanente sigue
/// siendo posible pasando un <see cref="ExpiresAt"/> lejano explícito, lo que no es posible es
/// declararlo permanente por olvido.
/// </summary>
public sealed record SetOverrideRequest(
    PatchPhase Phase, string DeclaredBy, DateTimeOffset? ExpiresAt);
