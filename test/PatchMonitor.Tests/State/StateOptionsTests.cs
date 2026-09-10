using Contracts;
using Contracts.State;
using Xunit;

namespace PatchMonitor.Tests.State;

/// <summary>
/// <see cref="StateOptions.FromEnvironment"/> lee <c>PATCH_STATE_CAN_THRESHOLD</c>,
/// <c>PATCH_STATE_HISTORY_LIMIT</c> y <c>MONITOR_TASK_QUEUE</c> del proceso. Cada test guarda
/// y restaura las tres variables para no filtrar estado entre tests ni al entorno de
/// desarrollo.
/// </summary>
[Collection(EnvVarCollection.Name)]
public class StateOptionsTests
{
    private const string ThresholdVar = "PATCH_STATE_CAN_THRESHOLD";
    private const string HistoryVar = "PATCH_STATE_HISTORY_LIMIT";
    private const string TaskQueueVar = "MONITOR_TASK_QUEUE";

    private static StateOptions WithEnv(string? threshold, string? history, string? taskQueue)
    {
        var savedThreshold = Environment.GetEnvironmentVariable(ThresholdVar);
        var savedHistory = Environment.GetEnvironmentVariable(HistoryVar);
        var savedTaskQueue = Environment.GetEnvironmentVariable(TaskQueueVar);
        try
        {
            Environment.SetEnvironmentVariable(ThresholdVar, threshold);
            Environment.SetEnvironmentVariable(HistoryVar, history);
            Environment.SetEnvironmentVariable(TaskQueueVar, taskQueue);
            return StateOptions.FromEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ThresholdVar, savedThreshold);
            Environment.SetEnvironmentVariable(HistoryVar, savedHistory);
            Environment.SetEnvironmentVariable(TaskQueueVar, savedTaskQueue);
        }
    }

    [Fact]
    public void Variables_ausentes_devuelven_los_defaults()
    {
        var options = WithEnv(null, null, null);

        Assert.Equal(500, options.ContinueAsNewThreshold);
        Assert.Equal(20, options.HistoryLimit);
        Assert.Equal(TaskQueues.PatchMonitor, options.TaskQueue);
        Assert.Equal("patch-monitor-task-queue", options.TaskQueue);
    }

    [Fact]
    public void Valores_validos_se_respetan()
    {
        var options = WithEnv("3", "5", "otra-task-queue");

        Assert.Equal(3, options.ContinueAsNewThreshold);
        Assert.Equal(5, options.HistoryLimit);
        Assert.Equal("otra-task-queue", options.TaskQueue);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Threshold_no_numerico_o_no_positivo_cae_al_default(string basura)
    {
        var options = WithEnv(basura, null, null);

        Assert.Equal(500, options.ContinueAsNewThreshold);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-3")]
    public void HistoryLimit_no_numerico_o_no_positivo_cae_al_default(string basura)
    {
        var options = WithEnv(null, basura, null);

        Assert.Equal(20, options.HistoryLimit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TaskQueue_ausente_o_en_blanco_cae_al_default(string? blanco)
    {
        var options = WithEnv(null, null, blanco);

        Assert.Equal(TaskQueues.PatchMonitor, options.TaskQueue);
    }
}
