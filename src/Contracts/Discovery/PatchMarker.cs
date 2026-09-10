namespace Contracts.Discovery;

/// <summary>
/// Resultado de leer un evento <c>MarkerRecorded</c> de nombre <c>core_patch</c> en la Event
/// History de una ejecución. <see cref="Deprecated"/> sale del payload del marker: <c>false</c>
/// mientras el patch está en convivencia, <c>true</c> una vez que el workflow llamó a
/// <c>Workflow.DeprecatePatch</c>. El search attribute <c>TemporalChangeVersion</c> no expone
/// este flag; por eso solo el tier 2 lo puede resolver.
/// </summary>
public sealed record PatchMarker(string PatchId, bool Deprecated);
