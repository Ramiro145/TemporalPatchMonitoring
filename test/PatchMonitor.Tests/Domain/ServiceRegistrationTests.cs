using Contracts.Domain;
using Contracts.Domain.Gates;
using Microsoft.Extensions.DependencyInjection;
using PatchMonitor.Infrastructure;
using Xunit;

namespace PatchMonitor.Tests.Domain;

public class ServiceRegistrationTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection().AddPatchMonitorServices().BuildServiceProvider();

    [Fact]
    public void AddPatchMonitorServices_resuelve_el_PhaseEvaluator()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<PhaseEvaluator>());
    }

    [Fact]
    public void AddPatchMonitorServices_registra_exactamente_dos_gates_con_From_distintos()
    {
        using var provider = BuildProvider();

        var froms = provider.GetServices<IPhaseGate>()
            .Select(g => g.From)
            .OrderBy(p => p)
            .ToArray();

        Assert.Equal(new[] { PatchPhase.Coexistence, PatchPhase.Deprecated }, froms);
    }

    [Fact]
    public void El_PhaseEvaluator_resuelto_delega_en_los_gates_registrados()
    {
        using var provider = BuildProvider();
        var evaluator = provider.GetRequiredService<PhaseEvaluator>();

        var verdict = evaluator.Evaluate(
            new PatchKey("default", "OrderWorkflow", "core-patch"),
            PatchPhase.Deprecated,
            ExecutionSnapshotSet.Empty);

        Assert.Equal(GateOutcome.Ready, verdict.Outcome);
        Assert.Equal(PatchPhase.Clean, verdict.NextPhase);
    }
}
