using PatchMonitor.Workflows;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// Spec 18: solo <see cref="MonitorWorkflow"/> falla ante un error de no-determinismo. Las entities
/// (<see cref="PatchStateWorkflow"/>, <see cref="PatchRegistryWorkflow"/>) nunca cierran y perderían
/// su estado si fallaran: para ellas quedar suspendidas hasta un deploy corregido es lo correcto.
/// </summary>
public class WorkflowFailureTypesTests
{
    private static IEnumerable<Type> FailureTypes(Type workflow) =>
        WorkflowDefinition.Create(workflow).FailureExceptionTypes ?? Array.Empty<Type>();

    [Fact]
    public void MonitorWorkflow_declara_el_no_determinismo_como_fallo_de_workflow()
    {
        Assert.Contains(typeof(WorkflowNondeterminismException), FailureTypes(typeof(MonitorWorkflow)));
    }

    [Theory]
    [InlineData(typeof(PatchStateWorkflow))]
    [InlineData(typeof(PatchRegistryWorkflow))]
    [InlineData(typeof(HealthWorkflow))]
    public void Los_demas_workflows_no_declaran_el_no_determinismo_como_fallo(Type workflow)
    {
        Assert.DoesNotContain(typeof(WorkflowNondeterminismException), FailureTypes(workflow));
    }
}
