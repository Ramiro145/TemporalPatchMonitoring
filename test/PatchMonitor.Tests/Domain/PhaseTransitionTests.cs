using Contracts.Domain;
using Xunit;

namespace PatchMonitor.Tests.Domain;

public class PhaseTransitionTests
{
    [Theory]
    [InlineData(PatchPhase.Coexistence, PatchPhase.Deprecated)]
    [InlineData(PatchPhase.Deprecated, PatchPhase.Clean)]
    public void IsLegal_acepta_los_dos_saltos_en_orden(PatchPhase from, PatchPhase to)
    {
        Assert.True(PhaseTransition.IsLegal(from, to));
    }

    [Theory]
    [InlineData(PatchPhase.Coexistence, PatchPhase.Clean)]   // saltear fase 2
    [InlineData(PatchPhase.Deprecated, PatchPhase.Coexistence)] // retroceso
    [InlineData(PatchPhase.Clean, PatchPhase.Deprecated)]     // retroceso
    [InlineData(PatchPhase.Clean, PatchPhase.Coexistence)]    // retroceso
    [InlineData(PatchPhase.Coexistence, PatchPhase.Coexistence)] // misma fase
    [InlineData(PatchPhase.Deprecated, PatchPhase.Deprecated)]
    [InlineData(PatchPhase.Clean, PatchPhase.Clean)]
    [InlineData(PatchPhase.Unknown, PatchPhase.Coexistence)]  // desde Unknown
    [InlineData(PatchPhase.Coexistence, PatchPhase.Unknown)]  // hacia Unknown
    [InlineData(PatchPhase.Unknown, PatchPhase.Unknown)]
    public void IsLegal_rechaza_todo_lo_demas(PatchPhase from, PatchPhase to)
    {
        Assert.False(PhaseTransition.IsLegal(from, to));
    }

    [Theory]
    [InlineData(PatchPhase.Coexistence, PatchPhase.Deprecated)]
    [InlineData(PatchPhase.Deprecated, PatchPhase.Clean)]
    public void NextOf_devuelve_la_fase_siguiente_en_orden(PatchPhase phase, PatchPhase expected)
    {
        Assert.Equal(expected, PhaseTransition.NextOf(phase));
    }

    [Theory]
    [InlineData(PatchPhase.Clean)]
    [InlineData(PatchPhase.Unknown)]
    public void NextOf_devuelve_null_para_fase_final_y_no_resuelta(PatchPhase phase)
    {
        Assert.Null(PhaseTransition.NextOf(phase));
    }
}
