using Contracts.Discovery;
using Contracts.Domain;
using PatchMonitor.Services;
using Xunit;

namespace PatchMonitor.Tests.Discovery;

public class PatchDiscoveryServiceTests
{
    private static readonly DiscoveryOptions Options =
        new("default", LookbackDays: 7, MaxExecutions: 500, MaxHistories: 200);

    private static PatchDiscoveryService Build(FakeExecutionSource source) =>
        new(source, Options);

    [Fact]
    public async Task Tier1_un_patchId_en_dos_workflowTypes_da_dos_resultados()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"));

        var results = await Build(source).DiscoverAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Key == new PatchKey("default", "OrderWorkflow", "core-patch"));
        Assert.Contains(results, r => r.Key == new PatchKey("default", "ShippingWorkflow", "core-patch"));
    }

    [Fact]
    public async Task Tier1_agrupa_varias_ejecuciones_del_mismo_patch_en_un_resultado()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow").WithWorkflowId("wf-1"),
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow").WithWorkflowId("wf-2"));

        var results = await Build(source).DiscoverAsync();

        var result = Assert.Single(results);
        Assert.Equal(2, result.Executions.Snapshots.Count);
        Assert.All(result.Executions.Snapshots, s => Assert.Equal(MarkerPresence.Present, s.Marker));
        Assert.False(result.Executions.IsTruncated);
    }

    [Fact]
    public async Task Ejecucion_sin_search_attribute_todavia_no_aparece()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            ExecutionFixture.Open("OrderWorkflow").WithoutMarkers());

        var results = await Build(source).DiscoverAsync();

        var result = Assert.Single(results);
        Assert.Equal("core-patch", result.Key.PatchId);
        Assert.Single(result.Executions.Snapshots);
    }

    [Fact]
    public async Task Tier1_no_lee_la_Event_History()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"));

        await Build(source).DiscoverAsync();

        Assert.Equal(0, source.HistoryReadCount);
    }

    [Theory]
    [InlineData("core-patch-3", "core-patch")]
    [InlineData("v2", "v2")]
    [InlineData("my-feature-flag-12", "my-feature-flag")]
    [InlineData("nodash", "nodash")]
    public async Task El_patchId_se_extrae_quitando_solo_el_sufijo_de_version(string entry, string expected)
    {
        var source = new FakeExecutionSource().Seed(
            ExecutionFixture.Open("OrderWorkflow").WithChangeVersion(entry));

        var results = await Build(source).DiscoverAsync();

        Assert.Equal(expected, Assert.Single(results).Key.PatchId);
    }

    [Fact]
    public async Task El_snapshot_conserva_workflowId_status_y_startTime_de_la_ejecucion()
    {
        var started = DateTimeOffset.UnixEpoch.AddDays(3);
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow")
                .WithWorkflowId("order-42")
                .StartedAt(started));

        var results = await Build(source).DiscoverAsync();

        var snapshot = Assert.Single(Assert.Single(results).Executions.Snapshots);
        Assert.Equal("order-42", snapshot.WorkflowId);
        Assert.Equal(ExecutionStatus.Running, snapshot.Status);
        Assert.Equal(started, snapshot.StartTime);
    }

    [Fact]
    public async Task Sin_ejecuciones_no_hay_resultados()
    {
        var results = await Build(new FakeExecutionSource()).DiscoverAsync();

        Assert.Empty(results);
    }
}
