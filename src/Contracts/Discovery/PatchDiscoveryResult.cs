using Contracts.Domain;

namespace Contracts.Discovery;

/// <summary>
/// Un patch descubierto y el conjunto de ejecuciones relevantes para él, con
/// <see cref="ExecutionSnapshot.Marker"/> ya resuelto en cada una. El spec 06 le pasa
/// <see cref="Executions"/> al <c>PhaseEvaluator</c> del spec 02; el spec 04 interpreta los
/// snapshots como fase 1 / 2 / 3 sin volver a leer la Event History.
/// </summary>
public sealed record PatchDiscoveryResult(PatchKey Key, ExecutionSnapshotSet Executions);
