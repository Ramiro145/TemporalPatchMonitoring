using Contracts.Domain;
using Contracts.Phase;
using PatchMonitor.Services;
using Xunit;
using static PatchMonitor.Tests.Domain.ExecutionSnapshotBuilder;
using static PatchMonitor.Tests.Phase.SnapshotSetBuilder;

namespace PatchMonitor.Tests.Phase;

public class PhaseResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static PhaseResolver Resolver(
        IPhaseOverrideStore? overrides = null,
        TimeSpan? cleanGrace = null,
        DateTimeOffset? now = null) =>
        new(
            overrides ?? new InMemoryPhaseOverrideStore(),
            new PhaseOptions(cleanGrace ?? TimeSpan.FromHours(24)),
            new FakeTimeProvider(now ?? Now));

    // ── Caso 2: sin evidencia de marker ────────────────────────────────────────

    [Fact]
    public void Caso2_conjunto_vacio_da_Unknown()
    {
        var res = Resolver().Resolve(Empty());

        Assert.Equal(PatchPhase.Unknown, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Equal("sin evidencia de marker", res.Reason);
    }

    [Fact]
    public void Caso2_todas_las_ejecuciones_en_Unknown_da_Unknown()
    {
        var res = Resolver().Resolve(Of(
            Open().Uninspected().StartedAt(T0),
            Closed().Uninspected().StartedAt(T0)));

        Assert.Equal(PatchPhase.Unknown, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Equal("sin evidencia de marker", res.Reason);
    }

    // ── Caso 6: hay ejecuciones pero ninguna lleva el marker ───────────────────

    [Fact]
    public void Caso6_todas_Absent_da_Unknown_con_razon_de_marker_ausente()
    {
        var res = Resolver().Resolve(Of(
            Open().WithoutMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0)));

        Assert.Equal(PatchPhase.Unknown, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Equal("ninguna ejecución lleva el marker", res.Reason);
    }

    // ── Caso 5: el marker más reciente no está deprecado → Coexistence ─────────

    [Fact]
    public void Caso5_newestWithMarker_Present_da_Coexistence()
    {
        var res = Resolver().Resolve(Of(
            Closed().WithoutMarker().StartedAt(T0),
            Open().WithMarker().StartedAt(T0.AddDays(1))));

        Assert.Equal(PatchPhase.Coexistence, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Equal("marker más reciente sin deprecar", res.Reason);
    }

    // ── Caso 4: el marker más reciente está deprecado → Deprecated ─────────────

    [Fact]
    public void Caso4_newestWithMarker_PresentDeprecated_da_Deprecated()
    {
        var res = Resolver().Resolve(Of(
            Open().WithDeprecatedMarker().StartedAt(T0.AddDays(1))));

        Assert.Equal(PatchPhase.Deprecated, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Equal("marker más reciente deprecado", res.Reason);
    }

    [Fact]
    public void Ejecucion_vieja_Present_no_cambia_el_resultado_si_la_mas_reciente_es_PresentDeprecated()
    {
        var res = Resolver().Resolve(Of(
            Closed().WithMarker().StartedAt(T0),
            Open().WithDeprecatedMarker().StartedAt(T0.AddDays(2))));

        Assert.Equal(PatchPhase.Deprecated, res.Phase);
    }

    // ── Caso 3: código limpio ────────────────────────────────────────────────

    [Fact]
    public void Caso3_sin_abiertas_con_marker_y_ejecucion_nueva_sin_marker_pasado_el_margen_da_Clean()
    {
        var res = Resolver(cleanGrace: TimeSpan.FromHours(24)).Resolve(Of(
            Closed().WithMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(25))));

        Assert.Equal(PatchPhase.Clean, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Contains("código limpio", res.Reason);
    }

    [Fact]
    public void Caso3_ejecucion_sin_marker_dentro_del_margen_no_da_Clean_y_cae_al_ultimo_marker()
    {
        var res = Resolver(cleanGrace: TimeSpan.FromHours(24)).Resolve(Of(
            Closed().WithMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(10))));

        Assert.Equal(PatchPhase.Coexistence, res.Phase);
    }

    [Fact]
    public void Una_ejecucion_abierta_con_marker_nunca_da_Clean_aunque_haya_codigo_nuevo_sin_marker()
    {
        var res = Resolver(cleanGrace: TimeSpan.FromHours(24)).Resolve(Of(
            Open().WithDeprecatedMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(100))));

        Assert.Equal(PatchPhase.Deprecated, res.Phase);
        Assert.NotEqual(PatchPhase.Clean, res.Phase);
    }

    // ── Desempate explícito: empate de StartTime Present vs PresentDeprecated ──

    [Fact]
    public void Empate_de_StartTime_entre_Present_y_PresentDeprecated_gana_PresentDeprecated()
    {
        var res = Resolver().Resolve(Of(
            Open().WithMarker().StartedAt(T0),
            Open().WithDeprecatedMarker().StartedAt(T0)));

        Assert.Equal(PatchPhase.Deprecated, res.Phase);
        Assert.Equal("marker más reciente deprecado", res.Reason);
    }
}
