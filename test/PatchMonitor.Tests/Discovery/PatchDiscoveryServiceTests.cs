using Contracts.Discovery;
using Contracts.Domain;
using PatchMonitor.Services;
using Xunit;

namespace PatchMonitor.Tests.Discovery;

public class PatchDiscoveryServiceTests
{
    private static readonly DiscoveryOptions Options =
        new("default", TargetHost: "temporal:7233", LookbackDays: 7, MaxExecutions: 500, MaxHistories: 200);

    private static PatchDiscoveryService Build(FakeExecutionSource source) => new(source, Options);

    private static PatchDiscoveryService Build(FakeExecutionSource source, DiscoveryOptions options) =>
        new(source, options);

    private static PatchDiscoveryResult ForType(IEnumerable<PatchDiscoveryResult> results, string workflowType) =>
        results.Single(r => r.Key.WorkflowType == workflowType);

    private static ExecutionSnapshot Only(PatchDiscoveryResult result) =>
        Assert.Single(result.Executions.Snapshots);

    // ---- Tier 1: search attribute -------------------------------------------------

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
    public async Task Tier1_con_atributo_e_historia_sin_markers_resuelve_Present()
    {
        var source = new FakeExecutionSource().Seed(
            ExecutionFixture.Open("OrderWorkflow").WithPatchAttribute("core-patch").WithoutMarkers());

        var results = await Build(source).DiscoverAsync();

        Assert.Equal(MarkerPresence.Present, Only(Assert.Single(results)).Marker);
    }

    [Fact]
    public async Task Tier1_sin_markers_legibles_igual_descubre_el_patch_como_Present()
    {
        // La historia se rompe, pero el atributo alcanza para no perder el patch.
        var source = new FakeExecutionSource().Seed(
            ExecutionFixture.Open("OrderWorkflow").WithPatchAttribute("core-patch").WithBrokenHistory());

        var results = await Build(source).DiscoverAsync();

        Assert.Equal(MarkerPresence.Present, Only(Assert.Single(results)).Marker);
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

        var snapshot = Only(Assert.Single(results));
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

    // ---- Tier 2: Event History --------------------------------------------------

    [Fact]
    public async Task Tier2_descubre_un_patch_presente_solo_en_la_historia()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithMarker("core-patch", deprecated: false, "OrderWorkflow"));

        var results = await Build(source).DiscoverAsync();

        var result = Assert.Single(results);
        Assert.Equal(new PatchKey("default", "OrderWorkflow", "core-patch"), result.Key);
        Assert.Equal(MarkerPresence.Present, Only(result).Marker);
    }

    [Fact]
    public async Task Tier2_marker_con_deprecated_true_da_PresentDeprecated()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithMarker("core-patch", deprecated: true, "OrderWorkflow"));

        var results = await Build(source).DiscoverAsync();

        Assert.Equal(MarkerPresence.PresentDeprecated, Only(Assert.Single(results)).Marker);
    }

    [Fact]
    public async Task Tier2_marker_con_deprecated_false_da_Present()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithMarker("core-patch", deprecated: false, "OrderWorkflow"));

        var results = await Build(source).DiscoverAsync();

        Assert.Equal(MarkerPresence.Present, Only(Assert.Single(results)).Marker);
    }

    [Fact]
    public async Task Tier1_y_tier2_de_la_misma_ejecucion_se_fusionan_en_un_snapshot_deprecado()
    {
        var source = new FakeExecutionSource().Seed(
            ExecutionFixture.Open("OrderWorkflow")
                .WithPatchAttribute("core-patch")
                .WithMarker("core-patch", deprecated: true));

        var results = await Build(source).DiscoverAsync();

        Assert.Equal(MarkerPresence.PresentDeprecated, Only(Assert.Single(results)).Marker);
    }

    [Fact]
    public async Task Historia_sin_markers_entra_como_Absent_en_un_patch_ya_existente()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow").WithWorkflowId("patched"),
            HistoryFixtures.OpenPrePatch("OrderWorkflow").WithWorkflowId("pre-patch"));

        var results = await Build(source).DiscoverAsync();

        var result = Assert.Single(results);
        Assert.Equal(2, result.Executions.Snapshots.Count);
        Assert.Equal(
            MarkerPresence.Absent,
            result.Executions.Snapshots.Single(s => s.WorkflowId == "pre-patch").Marker);
    }

    [Fact]
    public async Task Historia_sin_markers_y_sin_atributo_no_crea_un_patch_nuevo()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenPrePatch("OrderWorkflow"),
            HistoryFixtures.OpenPrePatch("OrderWorkflow"));

        var results = await Build(source).DiscoverAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task Una_ejecucion_flotante_no_se_atribuye_a_patches_de_otro_workflowType()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
            HistoryFixtures.OpenPrePatch("ShippingWorkflow"));

        var results = await Build(source).DiscoverAsync();

        Assert.Single(Assert.Single(results).Executions.Snapshots);
    }

    [Fact]
    public async Task Historia_rota_sin_atributo_entra_como_Unknown_en_un_patch_ya_existente()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow").WithWorkflowId("patched"),
            HistoryFixtures.OpenPrePatch("OrderWorkflow").WithWorkflowId("broken").WithBrokenHistory());

        var results = await Build(source).DiscoverAsync();

        var result = Assert.Single(results);
        Assert.Equal(
            MarkerPresence.Unknown,
            result.Executions.Snapshots.Single(s => s.WorkflowId == "broken").Marker);
    }

    // ---- Topes e IsTruncated --------------------------------------------------

    [Fact]
    public async Task Con_LimitReached_todos_los_sets_salen_truncados()
    {
        var source = new FakeExecutionSource()
            .Seed(
                HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow"),
                HistoryFixtures.OpenWithAttribute("core-patch", "ShippingWorkflow"))
            .ForceLimitReached();

        var results = await Build(source).DiscoverAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Executions.IsTruncated));
    }

    [Fact]
    public async Task Con_MaxHistories_agotado_solo_se_trunca_el_patch_con_ejecuciones_sin_inspeccionar()
    {
        var options = Options with { MaxHistories = 1 };
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow").WithWorkflowId("patched"),
            HistoryFixtures.OpenWithAttribute("other-patch", "ShippingWorkflow").WithWorkflowId("shipped"),
            HistoryFixtures.OpenPrePatch("OrderWorkflow").WithWorkflowId("pre"));

        var results = await Build(source, options).DiscoverAsync();

        Assert.True(ForType(results, "OrderWorkflow").Executions.IsTruncated);
        Assert.False(ForType(results, "ShippingWorkflow").Executions.IsTruncated);
    }

    [Fact]
    public async Task Una_excepcion_de_historia_trunca_solo_su_patch_y_deja_los_demas_intactos()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithAttribute("core-patch", "OrderWorkflow").WithWorkflowId("ok"),
            HistoryFixtures.OpenPrePatch("OrderWorkflow").WithWorkflowId("boom").WithBrokenHistory(),
            HistoryFixtures.OpenWithMarker("solo-hist", deprecated: false, "BillingWorkflow"));

        var results = await Build(source).DiscoverAsync();

        Assert.True(ForType(results, "OrderWorkflow").Executions.IsTruncated);
        Assert.False(ForType(results, "BillingWorkflow").Executions.IsTruncated);
    }

    [Fact]
    public async Task Con_presupuesto_de_sobra_y_sin_LimitReached_nada_se_trunca()
    {
        var source = new FakeExecutionSource().Seed(
            HistoryFixtures.OpenWithMarker("core-patch", deprecated: false, "OrderWorkflow"),
            HistoryFixtures.OpenPrePatch("OrderWorkflow"));

        var results = await Build(source).DiscoverAsync();

        Assert.All(results, r => Assert.False(r.Executions.IsTruncated));
    }
}
