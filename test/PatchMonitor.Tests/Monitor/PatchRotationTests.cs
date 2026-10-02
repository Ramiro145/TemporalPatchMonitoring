using Contracts.Discovery;
using Contracts.Domain;
using Contracts.Monitor;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// <see cref="PatchRotation"/> (spec 15, M-9): orden estable por <c>PatchKey</c> y ventana
/// rotativa por tick, función pura sin cluster.
/// </summary>
public class PatchRotationTests
{
    private static PatchDiscoveryResult Patch(string workflowType, string patchId, string ns = "default") =>
        new(new PatchKey(ns, workflowType, patchId), ExecutionSnapshotSet.Empty);

    private static IReadOnlyList<PatchDiscoveryResult> Patches(int count) =>
        Enumerable.Range(0, count).Select(i => Patch("Wf", $"p{i:D3}")).ToArray();

    private static string[] Ids(IEnumerable<PatchDiscoveryResult> results) =>
        results.Select(r => r.Key.PatchId).ToArray();

    [Fact]
    public void Con_count_menor_o_igual_al_tope_devuelve_todos_ordenados_por_PatchKey()
    {
        var discovered = new[]
        {
            Patch("B", "z"), Patch("A", "y", ns: "other"), Patch("A", "b"), Patch("A", "a"),
        };

        var selected = PatchRotation.Select(discovered, maxPerRun: 4, tickIndex: 7);

        Assert.Equal(
            new[] { ("default", "A", "a"), ("default", "A", "b"), ("default", "B", "z"), ("other", "A", "y") },
            selected.Select(s => (s.Key.Namespace, s.Key.WorkflowType, s.Key.PatchId)).ToArray());
    }

    [Fact]
    public void El_orden_no_depende_del_orden_de_entrada()
    {
        var discovered = Patches(10);
        var shuffled = discovered.Reverse().ToArray();

        Assert.Equal(
            Ids(PatchRotation.Select(discovered, 3, tickIndex: 5)),
            Ids(PatchRotation.Select(shuffled, 3, tickIndex: 5)));
    }

    [Fact]
    public void Es_determinista_para_el_mismo_tick()
    {
        var discovered = Patches(10);

        Assert.Equal(
            Ids(PatchRotation.Select(discovered, 3, tickIndex: 42)),
            Ids(PatchRotation.Select(discovered, 3, tickIndex: 42)));
    }

    [Fact]
    public void Dos_ticks_consecutivos_cubren_el_doble_del_tope_sin_repetir()
    {
        var discovered = Patches(10);

        var first = Ids(PatchRotation.Select(discovered, 3, tickIndex: 0));
        var second = Ids(PatchRotation.Select(discovered, 3, tickIndex: 1));

        Assert.Equal(new[] { "p000", "p001", "p002" }, first);
        Assert.Equal(new[] { "p003", "p004", "p005" }, second);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void La_ventana_da_la_vuelta_al_final_de_la_lista()
    {
        var discovered = Patches(10);

        // tick 3 ⇒ offset 9: p009, luego vuelve a p000 y p001.
        var selected = Ids(PatchRotation.Select(discovered, 3, tickIndex: 3));

        Assert.Equal(new[] { "p009", "p000", "p001" }, selected);
    }

    [Theory]
    [InlineData(10, 3)]
    [InlineData(7, 2)]
    [InlineData(50, 7)]
    [InlineData(9, 3)]
    [InlineData(11, 10)]
    public void Ceil_count_sobre_max_ticks_consecutivos_cubren_todos_los_patches(int count, int max)
    {
        var discovered = Patches(count);
        var ticks = (int)Math.Ceiling(count / (double)max);

        foreach (var startTick in new long[] { 0, 1, 13, 1_000_003 })
        {
            var seen = new HashSet<string>();
            for (var t = 0; t < ticks; t++)
            {
                foreach (var id in Ids(PatchRotation.Select(discovered, max, startTick + t)))
                {
                    seen.Add(id);
                }
            }

            Assert.Equal(count, seen.Count);
        }
    }

    [Fact]
    public void Un_tick_enorme_no_desborda_ni_sale_del_rango()
    {
        var discovered = Patches(7);

        var selected = PatchRotation.Select(discovered, 3, tickIndex: long.MaxValue);

        Assert.Equal(3, selected.Count);
        Assert.Equal(3, selected.Select(s => s.Key.PatchId).Distinct().Count());
    }

    [Fact]
    public void Sin_patches_o_con_tope_no_positivo_devuelve_vacio()
    {
        Assert.Empty(PatchRotation.Select(Array.Empty<PatchDiscoveryResult>(), 5, 0));
        Assert.Empty(PatchRotation.Select(Patches(3), 0, 0));
    }
}
