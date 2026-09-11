using Microsoft.AspNetCore.Http.HttpResults;
using MonitorApi.Endpoints;
using Xunit;

namespace PatchMonitor.Tests.Api;

/// <summary>
/// Los cuatro handlers de <see cref="ScheduleEndpoints"/> sobre un <see cref="FakeScheduleController"/>,
/// sin host HTTP.
/// </summary>
public class ScheduleEndpointsTests
{
    [Fact]
    public async Task Describe_con_Schedule_presente_devuelve_200_con_el_status()
    {
        var controller = new FakeScheduleController(exists: true, paused: false);

        var result = await ScheduleEndpoints.DescribeAsync(controller);

        var ok = Assert.IsType<Ok<Contracts.Monitor.ScheduleStatus>>(result.Result);
        Assert.False(ok.Value!.Paused);
    }

    [Fact]
    public async Task Describe_con_Schedule_ausente_devuelve_503()
    {
        var controller = new FakeScheduleController(exists: false);

        var result = await ScheduleEndpoints.DescribeAsync(controller);

        var statusCode = Assert.IsType<StatusCodeHttpResult>(result.Result);
        Assert.Equal(503, statusCode.StatusCode);
    }

    [Fact]
    public async Task Pause_devuelve_200_con_Paused_true_y_registra_la_nota()
    {
        var controller = new FakeScheduleController(exists: true, paused: false);

        var result = await ScheduleEndpoints.PauseAsync("mantenimiento", controller);

        var ok = Assert.IsType<Ok<Contracts.Monitor.ScheduleStatus>>(result.Result);
        Assert.True(ok.Value!.Paused);
        Assert.Equal("mantenimiento", controller.LastPauseNote);
    }

    [Fact]
    public async Task Pause_con_Schedule_ausente_devuelve_503()
    {
        var controller = new FakeScheduleController(exists: false);

        var result = await ScheduleEndpoints.PauseAsync(null, controller);

        var statusCode = Assert.IsType<StatusCodeHttpResult>(result.Result);
        Assert.Equal(503, statusCode.StatusCode);
    }

    [Fact]
    public async Task Unpause_devuelve_200_con_Paused_false()
    {
        var controller = new FakeScheduleController(exists: true, paused: true);

        var result = await ScheduleEndpoints.UnpauseAsync(null, controller);

        var ok = Assert.IsType<Ok<Contracts.Monitor.ScheduleStatus>>(result.Result);
        Assert.False(ok.Value!.Paused);
    }

    [Fact]
    public async Task Unpause_con_Schedule_ausente_devuelve_503()
    {
        var controller = new FakeScheduleController(exists: false);

        var result = await ScheduleEndpoints.UnpauseAsync(null, controller);

        var statusCode = Assert.IsType<StatusCodeHttpResult>(result.Result);
        Assert.Equal(503, statusCode.StatusCode);
    }

    [Fact]
    public async Task Trigger_devuelve_202_y_dispara_una_unica_vez()
    {
        var controller = new FakeScheduleController(exists: true);

        var result = await ScheduleEndpoints.TriggerAsync(controller);

        var accepted = Assert.IsType<Accepted<TriggerResponse>>(result.Result);
        Assert.True(accepted.Value!.Triggered);
        Assert.Equal(1, controller.TriggerCount);
    }

    [Fact]
    public async Task Trigger_con_Schedule_ausente_devuelve_503()
    {
        var controller = new FakeScheduleController(exists: false);

        var result = await ScheduleEndpoints.TriggerAsync(controller);

        var statusCode = Assert.IsType<StatusCodeHttpResult>(result.Result);
        Assert.Equal(503, statusCode.StatusCode);
    }
}
