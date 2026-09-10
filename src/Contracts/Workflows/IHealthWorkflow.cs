using Temporalio.Workflows;

namespace Contracts.Workflows;

[Workflow]
public interface IHealthWorkflow
{
    [WorkflowRun]
    Task<string> RunAsync();
}
