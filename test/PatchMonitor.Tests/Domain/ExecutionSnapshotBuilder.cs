using Contracts.Domain;

namespace PatchMonitor.Tests.Domain;

/// <summary>
/// Helper fluido para armar <see cref="ExecutionSnapshot"/> y <see cref="ExecutionSnapshotSet"/>
/// en los tests de los gates, de forma que se lean como la tabla de decisión de la spec:
/// <c>ExecutionSnapshotBuilder.Open().WithoutMarker()</c>, <c>Closed().WithMarker()</c>, etc.
/// </summary>
public sealed class ExecutionSnapshotBuilder
{
    private string _workflowId = "wf-" + Guid.NewGuid().ToString("N")[..8];
    private string _runId = Guid.NewGuid().ToString("N");
    private string _workflowType = "ObservedWorkflow";
    private ExecutionStatus _status = ExecutionStatus.Running;
    private MarkerPresence _marker = MarkerPresence.Unknown;
    private DateTimeOffset _startTime = DateTimeOffset.UnixEpoch;

    public static ExecutionSnapshotBuilder Open() =>
        new() { _status = ExecutionStatus.Running };

    public static ExecutionSnapshotBuilder Closed(ExecutionStatus status = ExecutionStatus.Completed)
    {
        if (status.IsOpen())
        {
            throw new ArgumentException($"{status} está abierta", nameof(status));
        }

        return new ExecutionSnapshotBuilder { _status = status };
    }

    public ExecutionSnapshotBuilder WithoutMarker() => Marker(MarkerPresence.Absent);

    public ExecutionSnapshotBuilder WithMarker() => Marker(MarkerPresence.Present);

    public ExecutionSnapshotBuilder WithDeprecatedMarker() => Marker(MarkerPresence.PresentDeprecated);

    public ExecutionSnapshotBuilder Uninspected() => Marker(MarkerPresence.Unknown);

    public ExecutionSnapshotBuilder Marker(MarkerPresence marker)
    {
        _marker = marker;
        return this;
    }

    public ExecutionSnapshotBuilder WithWorkflowId(string workflowId)
    {
        _workflowId = workflowId;
        return this;
    }

    public ExecutionSnapshotBuilder OfType(string workflowType)
    {
        _workflowType = workflowType;
        return this;
    }

    public ExecutionSnapshotBuilder StartedAt(DateTimeOffset startTime)
    {
        _startTime = startTime;
        return this;
    }

    public ExecutionSnapshot Build() =>
        new(_workflowId, _runId, _workflowType, _status, _marker, _startTime);

    public static implicit operator ExecutionSnapshot(ExecutionSnapshotBuilder builder) =>
        builder.Build();

    /// <summary>Arma un <see cref="ExecutionSnapshotSet"/> no truncado a partir de builders.</summary>
    public static ExecutionSnapshotSet Set(params ExecutionSnapshotBuilder[] executions) =>
        new(executions.Select(e => e.Build()).ToArray(), IsTruncated: false);

    /// <summary>Arma un <see cref="ExecutionSnapshotSet"/> marcado como truncado.</summary>
    public static ExecutionSnapshotSet TruncatedSet(params ExecutionSnapshotBuilder[] executions) =>
        new(executions.Select(e => e.Build()).ToArray(), IsTruncated: true);
}
