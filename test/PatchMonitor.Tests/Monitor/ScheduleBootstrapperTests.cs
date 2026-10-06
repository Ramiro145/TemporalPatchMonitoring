using Common;
using Contracts.Monitor;
using Temporalio.Api.Enums.V1;
using Temporalio.Client.Schedules;
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
        "patch-monitor-task-queue",
        TimeSpan.FromMinutes(MonitorOptions.DefaultRunTimeoutMinutes));

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

    [Fact]
    public void BuildAction_lleva_el_ExecutionTimeout_igual_al_RunTimeout()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), ScheduleBootstrapper.BuildAction(DefaultOptions).Options.ExecutionTimeout);

        var custom = DefaultOptions with { RunTimeout = TimeSpan.FromMinutes(40) };

        Assert.Equal(TimeSpan.FromMinutes(40), ScheduleBootstrapper.BuildAction(custom).Options.ExecutionTimeout);
    }

    // Schedule tal como lo dejaría EnsureScheduleAsync al crearlo con `options` (spec 15, M-3).
    private static Schedule ScheduleFor(MonitorOptions options) =>
        new(ScheduleBootstrapper.BuildAction(options), ScheduleBootstrapper.BuildSpec(options))
        {
            Policy = ScheduleBootstrapper.BuildPolicy(options),
        };

    [Fact]
    public void Differs_es_false_cuando_el_schedule_coincide_con_la_configuracion()
    {
        Assert.False(ScheduleBootstrapper.Differs(ScheduleFor(DefaultOptions), DefaultOptions));
    }

    [Fact]
    public void Differs_es_true_si_cambia_el_intervalo()
    {
        var current = ScheduleFor(DefaultOptions);
        var desired = DefaultOptions with { Interval = TimeSpan.FromMinutes(15) };

        Assert.True(ScheduleBootstrapper.Differs(current, desired));
    }

    [Fact]
    public void Differs_es_true_si_cambia_el_catchup_window()
    {
        var current = ScheduleFor(DefaultOptions);
        var desired = DefaultOptions with { CatchupWindow = TimeSpan.FromMinutes(30) };

        Assert.True(ScheduleBootstrapper.Differs(current, desired));
    }

    [Fact]
    public void Differs_es_true_si_cambia_la_task_queue()
    {
        var current = ScheduleFor(DefaultOptions);
        var desired = DefaultOptions with { TaskQueue = "otra-task-queue" };

        Assert.True(ScheduleBootstrapper.Differs(current, desired));
    }

    [Fact]
    public void Differs_es_true_si_cambia_el_RunTimeout()
    {
        var current = ScheduleFor(DefaultOptions);
        var desired = DefaultOptions with { RunTimeout = TimeSpan.FromMinutes(30) };

        Assert.True(ScheduleBootstrapper.Differs(current, desired));
    }

    [Fact]
    public void Differs_es_true_si_el_schedule_vigente_no_tiene_ExecutionTimeout()
    {
        // Schedule creado antes del spec 18: la acción no fija ExecutionTimeout.
        var baseline = ScheduleFor(DefaultOptions);
        var legacyAction = ScheduleActionStartWorkflow.Create(
            (Contracts.Workflows.IMonitorWorkflow wf) => wf.RunAsync(),
            new Temporalio.Client.WorkflowOptions(
                ScheduleBootstrapper.RunWorkflowIdPrefix, DefaultOptions.TaskQueue));
        var current = baseline with { Action = legacyAction };

        Assert.True(ScheduleBootstrapper.Differs(current, DefaultOptions));
    }

    [Fact]
    public void Differs_es_true_si_la_politica_de_overlap_no_es_Skip()
    {
        var baseline = ScheduleFor(DefaultOptions);
        var current = baseline with
        {
            Policy = baseline.Policy with { Overlap = ScheduleOverlapPolicy.AllowAll },
        };

        Assert.True(ScheduleBootstrapper.Differs(current, DefaultOptions));
    }

    [Fact]
    public void Differs_ignora_el_estado_pausado_y_la_nota()
    {
        var current = ScheduleFor(DefaultOptions) with
        {
            State = new ScheduleState { Paused = true, Note = "pausado por el operador" },
        };

        Assert.False(ScheduleBootstrapper.Differs(current, DefaultOptions));
    }
}
