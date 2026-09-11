using Contracts.Monitor;

namespace PatchMonitor.Tests.Api;

/// <summary>
/// <see cref="IScheduleController"/> en memoria para <c>ScheduleEndpointsTests</c>: configurable
/// como "existe" / "no existe" y registra las llamadas de pausa/reanudación/disparo.
/// </summary>
public sealed class FakeScheduleController : IScheduleController
{
    private bool _exists;
    private bool _paused;
    private string? _note;

    public int TriggerCount { get; private set; }
    public string? LastPauseNote { get; private set; }
    public string? LastUnpauseNote { get; private set; }

    public FakeScheduleController(bool exists = true, bool paused = false)
    {
        _exists = exists;
        _paused = paused;
    }

    public void SetExists(bool exists) => _exists = exists;

    public Task<ScheduleStatus?> DescribeAsync(CancellationToken ct = default) =>
        Task.FromResult(_exists
            ? new ScheduleStatus(
                "patch-monitor-schedule", _paused, _note, TimeSpan.FromMinutes(5),
                LastRunAt: null, NextRunAt: null, RunningCount: 0, NumActions: 0)
            : null);

    public Task<bool> PauseAsync(string? note, CancellationToken ct = default)
    {
        if (!_exists)
        {
            return Task.FromResult(false);
        }

        _paused = true;
        _note = note;
        LastPauseNote = note;
        return Task.FromResult(true);
    }

    public Task<bool> UnpauseAsync(string? note, CancellationToken ct = default)
    {
        if (!_exists)
        {
            return Task.FromResult(false);
        }

        _paused = false;
        _note = note;
        LastUnpauseNote = note;
        return Task.FromResult(true);
    }

    public Task<bool> TriggerAsync(CancellationToken ct = default)
    {
        if (!_exists)
        {
            return Task.FromResult(false);
        }

        TriggerCount++;
        return Task.FromResult(true);
    }
}
