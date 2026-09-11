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
    private const string MinutesVar = "PHASE_CLEAN_GRACE_MINUTES";

    private static PhaseOptions WithEnv(string? value) => WithEnv(value, null);

    private static PhaseOptions WithEnv(string? hoursValue, string? minutesValue)
    {
        var savedHours = Environment.GetEnvironmentVariable(Var);
        var savedMinutes = Environment.GetEnvironmentVariable(MinutesVar);
        try
        {
            Environment.SetEnvironmentVariable(Var, hoursValue);
            Environment.SetEnvironmentVariable(MinutesVar, minutesValue);
            return PhaseOptions.FromEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, savedHours);
            Environment.SetEnvironmentVariable(MinutesVar, savedMinutes);
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

    [Fact]
    public void Minutos_ausente_cae_a_la_logica_de_horas_existente()
    {
        var options = WithEnv(hoursValue: "48", minutesValue: null);

        Assert.Equal(TimeSpan.FromHours(48), options.CleanGrace);
    }

    [Fact]
    public void Minutos_presente_y_positiva_gana_sobre_horas_aunque_ambas_esten_seteadas()
    {
        var options = WithEnv(hoursValue: "48", minutesValue: "3");

        Assert.Equal(TimeSpan.FromMinutes(3), options.CleanGrace);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Minutos_no_numerico_o_no_positivo_cae_a_horas_nunca_lanza(string basura)
    {
        var options = WithEnv(hoursValue: "48", minutesValue: basura);

        Assert.Equal(TimeSpan.FromHours(48), options.CleanGrace);
    }
}
