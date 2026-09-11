using Common;
using Contracts.Monitor;
using Temporalio.Api.Enums.V1;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// Los tres builders puros de <see cref="ScheduleBootstrapper"/>, testeables sin cluster.
/// <c>EnsureScheduleAsync</c> no tiene cobertura unitaria: el test-server de time-skipping no
/// soporta Schedules (spec 06, paso 8: verificación e2e manual).
/// </summary>
public class ScheduleBootstrapperTests
{
    private static readonly MonitorOptions DefaultOptions = new(
        MonitorOptions.DefaultScheduleId,
        TimeSpan.FromMinutes(MonitorOptions.DefaultIntervalMinutes),
        TimeSpan.FromMinutes(MonitorOptions.DefaultCatchupWindowMinutes),
        MonitorOptions.DefaultMaxPatchesPerRun,
        "patch-monitor-task-queue");

    [Fact]
    public void BuildSpec_da_un_unico_intervalo_de_5_minutos_con_los_defaults()
    {
        var spec = ScheduleBootstrapper.BuildSpec(DefaultOptions);

        var interval = Assert.Single(spec.Intervals!);
        Assert.Equal(TimeSpan.FromMinutes(5), interval.Every);
    }

    [Fact]
    public void BuildSpec_respeta_un_intervalo_configurado()
    {
        var options = DefaultOptions with { Interval = TimeSpan.FromMinutes(15) };

        var spec = ScheduleBootstrapper.BuildSpec(options);

        var interval = Assert.Single(spec.Intervals!);
        Assert.Equal(TimeSpan.FromMinutes(15), interval.Every);
    }

    [Fact]
    public void BuildPolicy_da_Overlap_Skip_y_el_CatchupWindow_configurado()
    {
        var policy = ScheduleBootstrapper.BuildPolicy(DefaultOptions);

        Assert.Equal(ScheduleOverlapPolicy.Skip, policy.Overlap);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.CatchupWindow);
    }

    [Fact]
    public void BuildAction_apunta_a_IMonitorWorkflow_en_la_task_queue_configurada()
    {
        var action = ScheduleBootstrapper.BuildAction(DefaultOptions);

        Assert.Equal("patch-monitor-task-queue", action.Options.TaskQueue);
        Assert.Equal(ScheduleBootstrapper.RunWorkflowIdPrefix, action.Options.Id);
    }
}
