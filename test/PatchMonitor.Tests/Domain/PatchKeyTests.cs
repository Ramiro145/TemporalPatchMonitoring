using Contracts.Domain;
using Xunit;

namespace PatchMonitor.Tests.Domain;

public class PatchKeyTests
{
    [Fact]
    public void ToWorkflowId_es_deterministico_entre_llamadas()
    {
        var key = new PatchKey("default", "OrderWorkflow", "core-patch-v2");

        Assert.Equal(key.ToWorkflowId(), key.ToWorkflowId());
    }

    [Fact]
    public void ToWorkflowId_usa_el_formato_prefijado_por_segmentos()
    {
        var key = new PatchKey("default", "OrderWorkflow", "core-patch-v2");

        Assert.Equal("patch-state::default::OrderWorkflow::core-patch-v2", key.ToWorkflowId());
    }

    [Fact]
    public void ToWorkflowId_distingue_keys_distintas()
    {
        var a = new PatchKey("ns-a", "OrderWorkflow", "core-patch");
        var b = new PatchKey("ns-b", "OrderWorkflow", "core-patch");

        Assert.NotEqual(a.ToWorkflowId(), b.ToWorkflowId());
    }

    [Fact]
    public void ToWorkflowId_conserva_el_casing_del_patchId()
    {
        var lower = new PatchKey("default", "OrderWorkflow", "corepatch");
        var upper = new PatchKey("default", "OrderWorkflow", "CorePatch");

        Assert.NotEqual(lower.ToWorkflowId(), upper.ToWorkflowId());
    }

    [Theory]
    [InlineData("ns with spaces", "Order/Workflow", "patch:id@1")]
    [InlineData("ns\ttab", "Order.Workflow", "patch#id")]
    public void ToWorkflowId_sanea_caracteres_fuera_de_la_lista_blanca(
        string ns, string type, string patchId)
    {
        var id = new PatchKey(ns, type, patchId).ToWorkflowId();

        // Solo se permiten los separadores "::" del formato, letras, dígitos, '.', '_' y '-'.
        var withoutSeparators = id.Replace("::", string.Empty);
        Assert.All(withoutSeparators, c =>
            Assert.True(char.IsLetterOrDigit(c) || c is '.' or '_' or '-', $"carácter inesperado: '{c}'"));
    }

    [Fact]
    public void ToWorkflowId_no_reemplaza_puntos_guiones_ni_guion_bajo()
    {
        var key = new PatchKey("ns.1", "Order-Workflow", "core_patch.v2");

        Assert.Equal("patch-state::ns.1::Order-Workflow::core_patch.v2", key.ToWorkflowId());
    }

    [Fact]
    public void ToWorkflowId_trunca_a_200_con_sufijo_de_hash_cuando_el_id_es_largo()
    {
        var key = new PatchKey("default", "OrderWorkflow", new string('x', 400));

        var id = key.ToWorkflowId();

        Assert.True(id.Length <= 200, $"largo {id.Length}");
        Assert.Matches("_[0-9a-f]{8}$", id);
    }

    [Fact]
    public void ToWorkflowId_del_id_largo_sigue_siendo_deterministico()
    {
        var key = new PatchKey("default", "OrderWorkflow", new string('x', 400));

        Assert.Equal(key.ToWorkflowId(), key.ToWorkflowId());
    }

    [Fact]
    public void ToWorkflowId_del_id_largo_distingue_patchIds_que_solo_difieren_en_el_final()
    {
        var a = new PatchKey("default", "OrderWorkflow", new string('x', 400) + "-a");
        var b = new PatchKey("default", "OrderWorkflow", new string('x', 400) + "-b");

        Assert.NotEqual(a.ToWorkflowId(), b.ToWorkflowId());
    }
}
