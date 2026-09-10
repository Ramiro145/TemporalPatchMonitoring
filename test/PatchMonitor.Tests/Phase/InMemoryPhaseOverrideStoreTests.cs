using Contracts.Domain;
using Contracts.Phase;
using PatchMonitor.Services;
using Xunit;

namespace PatchMonitor.Tests.Phase;

public class InMemoryPhaseOverrideStoreTests
{
    private static readonly PatchKey Key = new("default", "OrderWorkflow", "core-patch");
    private static readonly PatchKey OtherKey = new("default", "OrderWorkflow", "other-patch");

    private static PhaseOverride Override(
        PatchKey key,
        PatchPhase phase = PatchPhase.Deprecated,
        DateTimeOffset? expiresAt = null) =>
        new(key, phase, "operador", DateTimeOffset.UnixEpoch, expiresAt);

    [Fact]
    public void Set_y_luego_Get_devuelve_el_mismo_override()
    {
        var store = new InMemoryPhaseOverrideStore();
        var ov = Override(Key);

        store.Set(ov);

        Assert.Equal(ov, store.Get(Key));
    }

    [Fact]
    public void Set_reemplaza_el_override_previo_del_mismo_patch()
    {
        var store = new InMemoryPhaseOverrideStore();
        store.Set(Override(Key, PatchPhase.Deprecated));

        var nuevo = Override(Key, PatchPhase.Clean);
        store.Set(nuevo);

        Assert.Equal(nuevo, store.Get(Key));
    }

    [Fact]
    public void Clear_borra_el_override()
    {
        var store = new InMemoryPhaseOverrideStore();
        store.Set(Override(Key));

        store.Clear(Key);

        Assert.Null(store.Get(Key));
    }

    [Fact]
    public void Get_de_una_key_inexistente_devuelve_null()
    {
        var store = new InMemoryPhaseOverrideStore();

        Assert.Null(store.Get(Key));
    }

    [Fact]
    public void Un_override_con_ExpiresAt_en_el_pasado_no_lo_devuelve_Get_ni_GetAll()
    {
        var store = new InMemoryPhaseOverrideStore();
        store.Set(Override(Key, expiresAt: DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Null(store.Get(Key));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void GetAll_devuelve_todos_los_overrides_vigentes()
    {
        var store = new InMemoryPhaseOverrideStore();
        var vigente1 = Override(Key);
        var vigente2 = Override(OtherKey, expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        store.Set(vigente1);
        store.Set(vigente2);

        var all = store.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(vigente1, all);
        Assert.Contains(vigente2, all);
    }
}
