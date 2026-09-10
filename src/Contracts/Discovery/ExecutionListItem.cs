using Contracts.Domain;

namespace Contracts.Discovery;

/// <summary>
/// Foto cruda de una ejecución tal como la devuelve el listado de Temporal, antes de que el
/// descubrimiento la interprete. <see cref="ChangeVersions"/> son las entradas del search
/// attribute <c>TemporalChangeVersion</c> (<c>&lt;patchId&gt;-&lt;version&gt;</c>), ya
/// parseadas; vacío si la ejecución no lo tiene o si el listado no lo trajo. El tier 1 del
/// descubrimiento se apoya en este campo para no leer la historia de ejecuciones que
/// claramente no tienen el patch.
/// </summary>
public sealed record ExecutionListItem(
    string WorkflowId,
    string RunId,
    string WorkflowType,
    ExecutionStatus Status,
    DateTimeOffset StartTime,
    IReadOnlyList<string> ChangeVersions);

/// <summary>
/// Filtro que <see cref="IExecutionSource.ListExecutionsAsync"/> traduce a una query
/// <b>simple</b> de standard visibility (nunca compuesta sobre <c>TemporalChangeVersion</c>,
/// que se cuelga sobre Postgres — <c>Construction.md</c> §4 restricción #1).
/// <see cref="OpenOnly"/> <c>true</c> lista solo ejecuciones en curso; <c>false</c> lista las
/// cerradas cuyo <c>StartTime</c> cae dentro de <see cref="LookbackDays"/>.
/// <see cref="Limit"/> es el tope de ejecuciones a traer.
/// </summary>
public sealed record ExecutionListFilter(bool OpenOnly, int LookbackDays, int Limit);

/// <summary>
/// Página de resultados de <see cref="IExecutionSource.ListExecutionsAsync"/>.
/// <see cref="LimitReached"/> es <c>true</c> cuando el listado se cortó por
/// <see cref="ExecutionListFilter.Limit"/> y hay más ejecuciones sin traer: el descubrimiento
/// marca entonces todos los <see cref="Domain.ExecutionSnapshotSet.IsTruncated"/> en <c>true</c>.
/// </summary>
public sealed record ExecutionListPage(
    IReadOnlyList<ExecutionListItem> Items,
    bool LimitReached);
