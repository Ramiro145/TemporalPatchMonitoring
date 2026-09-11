using Contracts.Api;
using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
using Microsoft.AspNetCore.Http.HttpResults;
using MonitorApi.Endpoints;
using PatchMonitor.Tests.Monitor;
using Xunit;

namespace PatchMonitor.Tests.Api;

/// <summary>
/// <see cref="PatchEndpoints.ListAsync"/> y <see cref="PatchEndpoints.GetAsync"/> invocados
/// directo sobre un <see cref="FakePatchStateStore"/>, sin host HTTP.
/// </summary>
public class PatchEndpointsTests
{
    private static readonly PatchKey KeyA = new("default", "OrderWorkflow", "order-v2");
    private static readonly PatchKey KeyB = new("default", "ShippingWorkflow", "ship-v3");
    private static readonly PatchKey KeyC = new("default", "BillingWorkflow", "bill-v1");
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static ApiOptions Options(int maxList = 100) => new(maxList, TimeSpan.FromHours(24));

    private static PatchState Assessed(PatchKey key) =>
        PatchState.Initial(key) with
        {
            Phase = PatchPhase.Coexistence,
            LastVerdict = new PhaseVerdict(
                GateOutcome.Blocked, PatchPhase.Coexistence, PatchPhase.Deprecated,
                1, new[] { "wf-1" }, "bloqueado", T0),
            LastObservedAt = T0,
            Revision = 1,
        };

    [Fact]
    public async Task ListAsync_con_registry_vacio_devuelve_Count_cero()
    {
        var store = new FakePatchStateStore();

        var result = await PatchEndpoints.ListAsync(store, Options());

        Assert.Equal(0, result.Value!.Count);
        Assert.False(result.Value.Truncated);
        Assert.Empty(result.Value.Patches);
    }

    [Fact]
    public async Task ListAsync_con_tres_patches_devuelve_tres_resumenes()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        store.Seed(KeyB, Assessed(KeyB));
        store.Seed(KeyC, Assessed(KeyC));

        var result = await PatchEndpoints.ListAsync(store, Options());

        Assert.Equal(3, result.Value!.Count);
        Assert.False(result.Value.Truncated);
        Assert.Equal(3, result.Value.Patches.Count);
    }

    [Fact]
    public async Task ListAsync_con_tope_menor_al_registry_trunca()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        store.Seed(KeyB, Assessed(KeyB));
        store.Seed(KeyC, Assessed(KeyC));

        var result = await PatchEndpoints.ListAsync(store, Options(maxList: 2));

        Assert.True(result.Value!.Truncated);
        Assert.Equal(2, result.Value.Patches.Count);
    }

    [Fact]
    public async Task ListAsync_con_una_key_ilegible_la_marca_Unknown_y_no_rompe_el_listado()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        store.FailGetFor(KeyB);

        var result = await PatchEndpoints.ListAsync(store, Options());

        Assert.Equal(2, result.Value!.Count);
        var unreadable = result.Value.Patches.Single(p => p.PatchId == KeyB.PatchId);
        Assert.Equal(PatchPhase.Unknown, unreadable.Phase);
        var ok = result.Value.Patches.Single(p => p.PatchId == KeyA.PatchId);
        Assert.Equal(PatchPhase.Coexistence, ok.Phase);
    }

    [Fact]
    public async Task GetAsync_de_un_patch_existente_devuelve_el_detalle_completo()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));

        var result = await PatchEndpoints.GetAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, store);

        var ok = Assert.IsType<Ok<PatchDetailResponse>>(result.Result);
        Assert.Equal(PatchPhase.Coexistence, ok.Value!.Summary.Phase);
    }

    [Fact]
    public async Task GetAsync_de_un_patch_inexistente_devuelve_NotFound()
    {
        var store = new FakePatchStateStore();

        var result = await PatchEndpoints.GetAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, store);

        Assert.IsType<NotFound>(result.Result);
    }
}
