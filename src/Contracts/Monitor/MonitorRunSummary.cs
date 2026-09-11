namespace Contracts.Monitor;

/// <summary>
/// Resultado de una pasada de <c>MonitorWorkflow</c>: lo que Temporal deja visible en la UI
/// como resultado de la ejecución y lo que los tests asertan. <see cref="VerdictsChanged"/>
/// cuenta los patches cuyo <c>Revision</c> (spec 05) avanzó durante esta pasada, es decir, cuyo
/// veredicto cambió.
/// </summary>
public sealed record MonitorRunSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int PatchesDiscovered,
    int PatchesAssessed,
    int VerdictsChanged,
    int OverridesLoaded,
    IReadOnlyList<string> Errors);
