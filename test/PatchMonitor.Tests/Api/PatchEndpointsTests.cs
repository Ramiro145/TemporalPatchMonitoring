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

    [Fact]
    public async Task SetOverride_con_Phase_Unknown_devuelve_BadRequest()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        var request = new SetOverrideRequest(PatchPhase.Unknown, "operador", null);

        var result = await PatchEndpoints.SetOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, request, force: null, store, Options());

        Assert.IsType<BadRequest<string>>(result.Result);
    }

    [Fact]
    public async Task SetOverride_con_DeclaredBy_vacio_devuelve_BadRequest()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        var request = new SetOverrideRequest(PatchPhase.Deprecated, "", null);

        var result = await PatchEndpoints.SetOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, request, force: null, store, Options());

        Assert.IsType<BadRequest<string>>(result.Result);
    }

    [Fact]
    public async Task SetOverride_con_ExpiresAt_en_el_pasado_devuelve_BadRequest()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        var request = new SetOverrideRequest(
            PatchPhase.Deprecated, "operador", DateTimeOffset.UtcNow.AddHours(-1));

        var result = await PatchEndpoints.SetOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, request, force: null, store, Options());

        Assert.IsType<BadRequest<string>>(result.Result);
    }

    [Fact]
    public async Task SetOverride_de_un_patch_inexistente_devuelve_NotFound()
    {
        var store = new FakePatchStateStore();
        var request = new SetOverrideRequest(PatchPhase.Deprecated, "operador", null);

        var result = await PatchEndpoints.SetOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, request, force: null, store, Options());

        Assert.IsType<NotFound>(result.Result);
    }

    [Fact]
    public async Task SetOverride_con_salto_legal_devuelve_200_y_aplica_el_ttl_por_default()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        var request = new SetOverrideRequest(PatchPhase.Deprecated, "operador", null);
        var before = DateTimeOffset.UtcNow;

        var result = await PatchEndpoints.SetOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, request, force: null, store, Options());

        var ok = Assert.IsType<Ok<PatchDetailResponse>>(result.Result);
        Assert.Equal(PatchPhase.Deprecated, ok.Value!.Summary.Phase);
        var expectedExpiry = before + TimeSpan.FromHours(24);
        Assert.True(Math.Abs((ok.Value.Override!.ExpiresAt!.Value - expectedExpiry).TotalSeconds) < 5);
    }

    [Fact]
    public async Task SetOverride_con_salto_ilegal_devuelve_Conflict_sin_tocar_el_store()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        var request = new SetOverrideRequest(PatchPhase.Clean, "operador", null);

        var result = await PatchEndpoints.SetOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, request, force: null, store, Options());

        Assert.IsType<Conflict<string>>(result.Result);
        var state = await store.GetStateAsync(KeyA);
        Assert.Null(state!.Override);
    }

    [Fact]
    public async Task SetOverride_con_salto_ilegal_y_force_devuelve_200()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        var request = new SetOverrideRequest(PatchPhase.Clean, "operador", null);

        var result = await PatchEndpoints.SetOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, request, force: true, store, Options());

        var ok = Assert.IsType<Ok<PatchDetailResponse>>(result.Result);
        Assert.Equal(PatchPhase.Clean, ok.Value!.Summary.Phase);
    }

    [Fact]
    public async Task SetOverride_redeclarando_la_misma_fase_vigente_devuelve_200_no_Conflict()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));
        var request = new SetOverrideRequest(PatchPhase.Coexistence, "operador", null);

        var result = await PatchEndpoints.SetOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, request, force: null, store, Options());

        Assert.IsType<Ok<PatchDetailResponse>>(result.Result);
    }

    [Fact]
    public async Task ClearOverride_sobre_un_patch_sin_override_es_idempotente()
    {
        var store = new FakePatchStateStore();
        store.Seed(KeyA, Assessed(KeyA));

        var result = await PatchEndpoints.ClearOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, store);

        var ok = Assert.IsType<Ok<PatchDetailResponse>>(result.Result);
        Assert.Null(ok.Value!.Override);
    }

    [Fact]
    public async Task ClearOverride_sobre_un_patch_inexistente_devuelve_NotFound()
    {
        var store = new FakePatchStateStore();

        var result = await PatchEndpoints.ClearOverrideAsync(
            KeyA.Namespace, KeyA.WorkflowType, KeyA.PatchId, store);

        Assert.IsType<NotFound>(result.Result);
    }
}
