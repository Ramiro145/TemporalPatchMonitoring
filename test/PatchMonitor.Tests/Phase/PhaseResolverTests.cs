using Contracts.Domain;
using Contracts.Phase;
using Contracts.State;
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

    [Fact]
    public void IsTruncated_por_si_solo_no_fuerza_Unknown_si_hay_un_marker_legible()
    {
        var res = Resolver().Resolve(
            New().With(Open().WithMarker().StartedAt(T0)).Truncated().Build());

        Assert.Equal(PatchPhase.Coexistence, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
    }

    // ── Caso 3: código limpio ────────────────────────────────────────────────

    [Fact]
    public void Caso3_sin_abiertas_con_marker_deprecado_y_ejecucion_nueva_sin_marker_pasado_el_margen_da_Clean()
    {
        var res = Resolver(cleanGrace: TimeSpan.FromHours(24)).Resolve(Of(
            Closed().WithDeprecatedMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(25))));

        Assert.Equal(PatchPhase.Clean, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Contains("código limpio", res.Reason);
    }

    [Fact]
    public void Caso3_ejecucion_sin_marker_dentro_del_margen_no_da_Clean_y_cae_al_ultimo_marker()
    {
        var res = Resolver(cleanGrace: TimeSpan.FromHours(24)).Resolve(Of(
            Closed().WithDeprecatedMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(10))));

        Assert.Equal(PatchPhase.Deprecated, res.Phase);
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

    // ── Capa 1 (spec 13): no saltar fases — sin marker Deprecated no hay Clean ─

    [Fact]
    public void Capa1_marker_nunca_deprecado_no_da_Clean_aunque_haya_evidencia_de_codigo_limpio()
    {
        var res = Resolver(cleanGrace: TimeSpan.FromHours(24)).Resolve(Of(
            Closed().WithMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(25))));

        Assert.NotEqual(PatchPhase.Clean, res.Phase);
        Assert.Equal(PatchPhase.Coexistence, res.Phase);
    }

    // ── Capa 2 (spec 13): evidencia mínima adaptativa ──────────────────────────
    //
    // p = tasa de ejecuciones con marker sobre el total de evidencia (marker + Absent) antes
    // del cutoff. N = ejecuciones Absent posteriores al cutoff necesarias para Clean, con
    // confianza 0.95 (default). El caso "p=1 ⇒ N=1" ya lo cubre
    // Caso3_sin_abiertas_con_marker_deprecado_y_ejecucion_nueva_sin_marker_pasado_el_margen_da_Clean.

    [Fact]
    public void Capa2_p_0_5_exige_5_ejecuciones_limpias_y_con_4_no_alcanza()
    {
        var grace = TimeSpan.FromHours(24);
        var res = Resolver(cleanGrace: grace).Resolve(Of(
            Closed().WithDeprecatedMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(1)), // evidencia previa: p = 1/2 = 0.5
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(1)),
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(2)),
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(3)),
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(4))));

        Assert.NotEqual(PatchPhase.Clean, res.Phase);
        Assert.Equal(PatchPhase.Deprecated, res.Phase);
        Assert.Contains("N=5", res.Reason);
        Assert.Contains("faltan 1", res.Reason);
    }

    [Fact]
    public void Capa2_p_0_5_con_5_ejecuciones_limpias_alcanza_para_Clean()
    {
        var grace = TimeSpan.FromHours(24);
        var res = Resolver(cleanGrace: grace).Resolve(Of(
            Closed().WithDeprecatedMarker().StartedAt(T0),
            Closed().WithoutMarker().StartedAt(T0.AddHours(1)),
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(1)),
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(2)),
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(3)),
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(4)),
            Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(5))));

        Assert.Equal(PatchPhase.Clean, res.Phase);
    }

    [Fact]
    public void Capa2_p_0_1_exige_29_ejecuciones_limpias_y_con_28_no_alcanza()
    {
        var grace = TimeSpan.FromHours(24);
        var before = Enumerable.Range(1, 9)
            .Select(i => Closed().WithoutMarker().StartedAt(T0.AddHours(i)));
        var after = Enumerable.Range(1, 28)
            .Select(i => Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(i)));
        var executions = new[] { Closed().WithDeprecatedMarker().StartedAt(T0) }
            .Concat(before).Concat(after).ToArray();

        var res = Resolver(cleanGrace: grace).Resolve(Of(executions));

        Assert.NotEqual(PatchPhase.Clean, res.Phase);
        Assert.Contains("N=29", res.Reason);
        Assert.Contains("faltan 1", res.Reason);
    }

    [Fact]
    public void Capa2_p_0_1_con_29_ejecuciones_limpias_alcanza_para_Clean()
    {
        var grace = TimeSpan.FromHours(24);
        var before = Enumerable.Range(1, 9)
            .Select(i => Closed().WithoutMarker().StartedAt(T0.AddHours(i)));
        var after = Enumerable.Range(1, 29)
            .Select(i => Closed().WithoutMarker().StartedAt(T0 + grace + TimeSpan.FromHours(i)));
        var executions = new[] { Closed().WithDeprecatedMarker().StartedAt(T0) }
            .Concat(before).Concat(after).ToArray();

        var res = Resolver(cleanGrace: grace).Resolve(Of(executions));

        Assert.Equal(PatchPhase.Clean, res.Phase);
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

    // ── Caso 1: precedencia del override ──────────────────────────────────────

    private static PhaseOverride ActiveOverride(PatchPhase phase) =>
        new(Key, phase, "una-operadora", Now.AddDays(-1), ExpiresAt: null);

    [Fact]
    public void Caso1_override_vigente_gana_con_Source_Override_y_la_inferida_en_el_Reason()
    {
        var store = new InMemoryPhaseOverrideStore();
        store.Set(ActiveOverride(PatchPhase.Deprecated));

        // Snapshots que inferirían Coexistence.
        var res = Resolver(store).Resolve(Of(Open().WithMarker().StartedAt(T0)));

        Assert.Equal(PatchPhase.Deprecated, res.Phase);
        Assert.Equal(PhaseSource.Override, res.Source);
        Assert.Contains("la inferida era Coexistence", res.Reason);
        Assert.Contains("una-operadora", res.Reason);
    }

    [Fact]
    public void Caso1_override_con_ExpiresAt_vencido_se_ignora_y_gana_la_inferida()
    {
        var store = new InMemoryPhaseOverrideStore();
        store.Set(ActiveOverride(PatchPhase.Deprecated) with { ExpiresAt = Now.AddHours(-1) });

        var res = Resolver(store).Resolve(Of(Open().WithMarker().StartedAt(T0)));

        Assert.Equal(PatchPhase.Coexistence, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
    }

    [Fact]
    public void Caso1_override_que_el_store_da_por_vigente_pero_vencio_para_el_reloj_del_resolver_se_ignora()
    {
        // El store devuelve el override (su chequeo best-effort contra UtcNow lo deja pasar);
        // el resolver, con su reloj inyectado en Now, lo descarta con IsActiveAt(now).
        var stale = ActiveOverride(PatchPhase.Deprecated) with { ExpiresAt = Now.AddMinutes(-1) };
        var store = new StubOverrideStore(stale);

        var res = Resolver(store).Resolve(Of(Open().WithMarker().StartedAt(T0)));

        Assert.Equal(PatchPhase.Coexistence, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
    }

    [Fact]
    public void Sin_override_el_Source_es_siempre_Inferred()
    {
        var res = Resolver().Resolve(Of(Open().WithDeprecatedMarker().StartedAt(T0)));

        Assert.Equal(PhaseSource.Inferred, res.Source);
    }

    // ── Spec 15 (M-6): Clean no retrocede sin evidencia nueva ──────────────────

    private static readonly TimeSpan Grace24h = TimeSpan.FromHours(24);

    // Un marker deprecado en T0, 9 ejecuciones sin marker antes del corte (p = 0.1 ⇒ N = 29) y
    // solo 28 después: sin estado previo la Capa 2 NO alcanza para Clean (ventana ya corrida).
    private static PatchMonitor.Tests.Domain.ExecutionSnapshotBuilder[] WindowSlidWithP01() =>
        new[] { Closed().WithDeprecatedMarker().StartedAt(T0) }
            .Concat(Enumerable.Range(1, 9).Select(i => Closed().WithoutMarker().StartedAt(T0.AddHours(i))))
            .Concat(Enumerable.Range(1, 28)
                .Select(i => Closed().WithoutMarker().StartedAt(T0 + Grace24h + TimeSpan.FromHours(i))))
            .ToArray();

    private static PatchState CleanState(
        DateTimeOffset? changedAt, PhaseSource source = PhaseSource.Inferred, PatchPhase phase = PatchPhase.Clean) =>
        PatchState.Initial(Key) with { Phase = phase, Source = source, LastChangedAt = changedAt };

    [Fact]
    public void M6_control_sin_estado_previo_la_ventana_corrida_no_alcanza_para_Clean()
    {
        var res = Resolver(cleanGrace: Grace24h).Resolve(Of(WindowSlidWithP01()), previous: null);

        Assert.NotEqual(PatchPhase.Clean, res.Phase);
    }

    [Fact]
    public void M6_previous_Clean_inferido_sin_markers_nuevos_conserva_Clean()
    {
        var changedAt = T0.AddDays(10);

        var res = Resolver(cleanGrace: Grace24h).Resolve(Of(WindowSlidWithP01()), CleanState(changedAt));

        Assert.Equal(PatchPhase.Clean, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Contains("se conserva Clean", res.Reason);
        Assert.Contains($"{changedAt:o}", res.Reason);
    }

    [Fact]
    public void M6_un_marker_posterior_a_LastChangedAt_saca_de_Clean()
    {
        var changedAt = T0.AddDays(10);
        var executions = WindowSlidWithP01()
            .Append(Closed().WithDeprecatedMarker().StartedAt(changedAt.AddDays(1)))
            .ToArray();

        var res = Resolver(cleanGrace: Grace24h).Resolve(Of(executions), CleanState(changedAt));

        Assert.NotEqual(PatchPhase.Clean, res.Phase);
    }

    [Fact]
    public void M6_un_marker_anterior_a_LastChangedAt_no_cuenta_como_evidencia_nueva()
    {
        var changedAt = T0.AddDays(10);
        var executions = WindowSlidWithP01()
            .Append(Closed().WithMarker().StartedAt(changedAt.AddMinutes(-1)))
            .ToArray();

        var res = Resolver(cleanGrace: Grace24h).Resolve(Of(executions), CleanState(changedAt));

        Assert.Equal(PatchPhase.Clean, res.Phase);
    }

    // ── Spec 18: Clean no se conserva con ejecuciones posteriores sin leer ─────

    [Fact]
    public void M6_una_ejecucion_Unknown_posterior_a_LastChangedAt_da_Unknown_con_el_motivo()
    {
        var changedAt = T0.AddDays(10);
        var executions = WindowSlidWithP01()
            .Append(Closed().Uninspected().StartedAt(changedAt.AddHours(1)))
            .Append(Closed().Uninspected().StartedAt(changedAt.AddHours(2)))
            .ToArray();

        var res = Resolver(cleanGrace: Grace24h).Resolve(Of(executions), CleanState(changedAt));

        Assert.Equal(PatchPhase.Unknown, res.Phase);
        Assert.Equal(PhaseSource.Inferred, res.Source);
        Assert.Contains("no se puede confirmar Clean", res.Reason);
        Assert.Contains("2 ejecuciones", res.Reason);
        Assert.Contains($"{changedAt:o}", res.Reason);
    }

    [Fact]
    public void M6_una_ejecucion_Unknown_anterior_a_LastChangedAt_no_impide_conservar_Clean()
    {
        var changedAt = T0.AddDays(10);
        var executions = WindowSlidWithP01()
            .Append(Closed().Uninspected().StartedAt(changedAt.AddMinutes(-1)))
            .ToArray();

        var res = Resolver(cleanGrace: Grace24h).Resolve(Of(executions), CleanState(changedAt));

        Assert.Equal(PatchPhase.Clean, res.Phase);
        Assert.Contains("se conserva Clean", res.Reason);
    }

    [Fact]
    public void M6_un_listado_truncado_sin_Unknown_posteriores_conserva_Clean()
    {
        var changedAt = T0.AddDays(10);
        var discovery = SnapshotSetBuilder.New().With(WindowSlidWithP01()).Truncated().Build();

        var res = Resolver(cleanGrace: Grace24h).Resolve(discovery, CleanState(changedAt));

        Assert.Equal(PatchPhase.Clean, res.Phase);
        Assert.Contains("se conserva Clean", res.Reason);
    }

    [Fact]
    public void M6_un_marker_posterior_gana_sobre_una_ejecucion_Unknown_posterior()
    {
        var changedAt = T0.AddDays(10);
        var executions = WindowSlidWithP01()
            .Append(Closed().WithDeprecatedMarker().StartedAt(changedAt.AddDays(1)))
            .Append(Closed().Uninspected().StartedAt(changedAt.AddDays(1)))
            .ToArray();

        var res = Resolver(cleanGrace: Grace24h).Resolve(Of(executions), CleanState(changedAt));

        Assert.DoesNotContain("no se puede confirmar Clean", res.Reason);
    }

    [Fact]
    public void M6_previous_con_Source_Override_no_activa_la_regla()
    {
        var res = Resolver(cleanGrace: Grace24h).Resolve(
            Of(WindowSlidWithP01()), CleanState(T0.AddDays(10), PhaseSource.Override));

        Assert.NotEqual(PatchPhase.Clean, res.Phase);
    }

    [Fact]
    public void M6_previous_Clean_sin_LastChangedAt_no_activa_la_regla()
    {
        var res = Resolver(cleanGrace: Grace24h).Resolve(Of(WindowSlidWithP01()), CleanState(changedAt: null));

        Assert.NotEqual(PatchPhase.Clean, res.Phase);
    }

    [Fact]
    public void M6_previous_en_otra_fase_no_activa_la_regla()
    {
        var res = Resolver(cleanGrace: Grace24h).Resolve(
            Of(WindowSlidWithP01()), CleanState(T0.AddDays(10), phase: PatchPhase.Deprecated));

        Assert.NotEqual(PatchPhase.Clean, res.Phase);
    }

    [Fact]
    public void M6_un_override_vigente_gana_aunque_el_previous_sea_Clean()
    {
        var store = new InMemoryPhaseOverrideStore();
        store.Set(ActiveOverride(PatchPhase.Deprecated));

        var res = Resolver(store, Grace24h).Resolve(Of(WindowSlidWithP01()), CleanState(T0.AddDays(10)));

        Assert.Equal(PatchPhase.Deprecated, res.Phase);
        Assert.Equal(PhaseSource.Override, res.Source);
    }

    private sealed class StubOverrideStore : IPhaseOverrideStore
    {
        private readonly PhaseOverride? _ov;

        public StubOverrideStore(PhaseOverride? ov) => _ov = ov;

        public PhaseOverride? Get(PatchKey key) => _ov;

        public void Set(PhaseOverride ov) => throw new NotSupportedException();

        public void Clear(PatchKey key) => throw new NotSupportedException();

        public IReadOnlyList<PhaseOverride> GetAll() =>
            _ov is null ? Array.Empty<PhaseOverride>() : new[] { _ov };
    }
}
