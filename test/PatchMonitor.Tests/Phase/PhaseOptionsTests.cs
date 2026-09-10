using Contracts.Phase;
using Xunit;

namespace PatchMonitor.Tests.Phase;

/// <summary>
/// <see cref="PhaseOptions.FromEnvironment"/> lee <c>PHASE_CLEAN_GRACE_HOURS</c> del proceso.
/// Cada test guarda y restaura la variable para no filtrar estado entre tests ni al entorno de
/// desarrollo. Ningún otro test de esta suite toca ese nombre.
/// </summary>
public class PhaseOptionsTests
{
    private const string Var = "PHASE_CLEAN_GRACE_HOURS";

    private static PhaseOptions WithEnv(string? value)
    {
        var saved = Environment.GetEnvironmentVariable(Var);
        try
        {
            Environment.SetEnvironmentVariable(Var, value);
            return PhaseOptions.FromEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, saved);
        }
    }

    [Fact]
    public void Ausente_devuelve_el_default_de_24_horas()
    {
        var options = WithEnv(null);

        Assert.Equal(PhaseOptions.DefaultCleanGrace, options.CleanGrace);
        Assert.Equal(TimeSpan.FromHours(24), options.CleanGrace);
    }

    [Fact]
    public void Valor_valido_se_respeta()
    {
        var options = WithEnv("48");

        Assert.Equal(TimeSpan.FromHours(48), options.CleanGrace);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-3")]
    public void No_numerico_o_no_positivo_cae_al_default_sin_lanzar(string basura)
    {
        var options = WithEnv(basura);

        Assert.Equal(PhaseOptions.DefaultCleanGrace, options.CleanGrace);
    }
}
