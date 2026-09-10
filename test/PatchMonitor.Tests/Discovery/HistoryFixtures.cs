using Contracts.Discovery;
using Contracts.Domain;

namespace PatchMonitor.Tests.Discovery;

/// <summary>
/// Builder fluido de ejecuciones sintéticas para los tests del descubrimiento. Junta en un
/// solo objeto la <see cref="ExecutionListItem"/> (lo que devolvería el listado, incluido el
/// search attribute <c>TemporalChangeVersion</c> en <see cref="ExecutionListItem.ChangeVersions"/>)
/// y su Event History simulada (los <see cref="PatchMarker"/> que <c>ReadPatchMarkersAsync</c>
/// devolvería, o la excepción que lanzaría). Se siembra en <see cref="FakeExecutionSource"/>.
/// </summary>
public sealed class ExecutionFixture
{
    private string _workflowId = "wf-" + Guid.NewGuid().ToString("N")[..8];
    private string _runId = Guid.NewGuid().ToString("N");
    private readonly string _workflowType;
    private readonly ExecutionStatus _status;
    private DateTimeOffset _startTime = DateTimeOffset.UnixEpoch;
    private readonly List<string> _changeVersions = new();
    private readonly List<PatchMarker> _markers = new();
    private Exception? _historyError;
    private bool _historyConfigured;

    private ExecutionFixture(string workflowType, ExecutionStatus status)
    {
        _workflowType = workflowType;
        _status = status;
    }

    public static ExecutionFixture Open(string workflowType = "ObservedWorkflow") =>
        new(workflowType, ExecutionStatus.Running);

    public static ExecutionFixture Closed(
        string workflowType = "ObservedWorkflow",
        ExecutionStatus status = ExecutionStatus.Completed)
    {
        if (status.IsOpen())
        {
            throw new ArgumentException($"{status} está abierta", nameof(status));
        }

        return new ExecutionFixture(workflowType, status);
    }

    public ExecutionFixture WithWorkflowId(string workflowId)
    {
        _workflowId = workflowId;
        return this;
    }

    public ExecutionFixture WithRunId(string runId)
    {
        _runId = runId;
        return this;
    }

    public ExecutionFixture StartedAt(DateTimeOffset startTime)
    {
        _startTime = startTime;
        return this;
    }

    /// <summary>Agrega entradas crudas al search attribute (<c>&lt;patchId&gt;-&lt;version&gt;</c>).</summary>
    public ExecutionFixture WithChangeVersion(params string[] entries)
    {
        _changeVersions.AddRange(entries);
        return this;
    }

    /// <summary>Azúcar: agrega la entrada <c>&lt;patchId&gt;-&lt;version&gt;</c> al search attribute (tier 1).</summary>
    public ExecutionFixture WithPatchAttribute(string patchId, int version = 1) =>
        WithChangeVersion($"{patchId}-{version}");

    /// <summary>Agrega un marker <c>core_patch</c> a la Event History simulada (tier 2).</summary>
    public ExecutionFixture WithMarker(string patchId, bool deprecated = false)
    {
        _markers.Add(new PatchMarker(patchId, deprecated));
        _historyConfigured = true;
        return this;
    }

    /// <summary>Historia legible completa y sin ningún marker <c>core_patch</c>.</summary>
    public ExecutionFixture WithoutMarkers()
    {
        _historyConfigured = true;
        return this;
    }

    /// <summary>La lectura de la Event History falla (tope alcanzado, RPC roto, historia truncada).</summary>
    public ExecutionFixture WithBrokenHistory(Exception? error = null)
    {
        _historyError = error ?? new InvalidOperationException("history read failed (fixture)");
        _historyConfigured = true;
        return this;
    }

    internal ExecutionListItem Item => new(
        _workflowId, _runId, _workflowType, _status, _startTime, _changeVersions.ToArray());

    internal Exception? HistoryError => _historyError;

    /// <summary><c>null</c> si nunca se configuró historia; si no, la lista (posiblemente vacía) de markers.</summary>
    internal IReadOnlyList<PatchMarker>? History =>
        _historyConfigured ? _markers.ToArray() : null;
}

/// <summary>Fábricas cortas para que los tests se lean como la tabla de derivación de la spec.</summary>
public static class HistoryFixtures
{
    /// <summary>Ejecución abierta descubierta por tier 1: search attribute con el patch, sin historia configurada.</summary>
    public static ExecutionFixture OpenWithAttribute(string patchId, string workflowType = "ObservedWorkflow") =>
        ExecutionFixture.Open(workflowType).WithPatchAttribute(patchId);

    /// <summary>Ejecución abierta descubierta solo por tier 2: sin search attribute, con marker en la historia.</summary>
    public static ExecutionFixture OpenWithMarker(
        string patchId, bool deprecated = false, string workflowType = "ObservedWorkflow") =>
        ExecutionFixture.Open(workflowType).WithMarker(patchId, deprecated);

    /// <summary>Ejecución abierta pre-patch: sin search attribute y con historia sin markers.</summary>
    public static ExecutionFixture OpenPrePatch(string workflowType = "ObservedWorkflow") =>
        ExecutionFixture.Open(workflowType).WithoutMarkers();
}
