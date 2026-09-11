using Contracts.Monitor;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// <see cref="MonitorOptions.FromEnvironment"/> lee variables de entorno del proceso. Cada test
/// guarda y restaura las cinco variables para no filtrar estado entre tests ni al entorno de
/// desarrollo. Ningún otro test de esta suite toca estos nombres.
/// </summary>
public class MonitorOptionsTests
{
    private static readonly string[] Vars =
    {
        "MONITOR_SCHEDULE_ID",
        "MONITOR_INTERVAL_MINUTES",
        "MONITOR_CATCHUP_WINDOW_MINUTES",
        "MONITOR_MAX_PATCHES_PER_RUN",
        "MONITOR_TASK_QUEUE",
    };

    private static MonitorOptions WithEnv(IDictionary<string, string?> values)
    {
        var saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Vars)
            {
                Environment.SetEnvironmentVariable(
                    name, values.TryGetValue(name, out var v) ? v : null);
            }

            return MonitorOptions.FromEnvironment();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void Todas_ausentes_devuelve_los_defaults()
    {
        var options = WithEnv(new Dictionary<string, string?>());

        Assert.Equal(MonitorOptions.DefaultScheduleId, options.ScheduleId);
        Assert.Equal(TimeSpan.FromMinutes(MonitorOptions.DefaultIntervalMinutes), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(MonitorOptions.DefaultCatchupWindowMinutes), options.CatchupWindow);
        Assert.Equal(MonitorOptions.DefaultMaxPatchesPerRun, options.MaxPatchesPerRun);
        Assert.Equal(Contracts.TaskQueues.PatchMonitor, options.TaskQueue);
    }

    [Fact]
    public void Cada_variable_seteada_con_valor_valido_se_respeta()
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["MONITOR_SCHEDULE_ID"] = "custom-schedule",
            ["MONITOR_INTERVAL_MINUTES"] = "15",
            ["MONITOR_CATCHUP_WINDOW_MINUTES"] = "30",
            ["MONITOR_MAX_PATCHES_PER_RUN"] = "10",
            ["MONITOR_TASK_QUEUE"] = "custom-task-queue",
        });

        Assert.Equal("custom-schedule", options.ScheduleId);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(30), options.CatchupWindow);
        Assert.Equal(10, options.MaxPatchesPerRun);
        Assert.Equal("custom-task-queue", options.TaskQueue);
    }

    [Theory]
    [InlineData("no-soy-un-numero")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Valor_basura_o_no_positivo_en_un_entero_cae_al_default(string basura)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["MONITOR_INTERVAL_MINUTES"] = basura,
            ["MONITOR_CATCHUP_WINDOW_MINUTES"] = basura,
            ["MONITOR_MAX_PATCHES_PER_RUN"] = basura,
        });

        Assert.Equal(TimeSpan.FromMinutes(MonitorOptions.DefaultIntervalMinutes), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(MonitorOptions.DefaultCatchupWindowMinutes), options.CatchupWindow);
        Assert.Equal(MonitorOptions.DefaultMaxPatchesPerRun, options.MaxPatchesPerRun);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ScheduleId_y_TaskQueue_vacios_o_en_blanco_caen_al_default(string blanco)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["MONITOR_SCHEDULE_ID"] = blanco,
            ["MONITOR_TASK_QUEUE"] = blanco,
        });

        Assert.Equal(MonitorOptions.DefaultScheduleId, options.ScheduleId);
        Assert.Equal(Contracts.TaskQueues.PatchMonitor, options.TaskQueue);
    }
}
