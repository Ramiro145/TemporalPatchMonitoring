using Contracts.Api;
using PatchMonitor.Tests.State;
using Xunit;

namespace PatchMonitor.Tests.Api;

/// <summary>
/// <see cref="ApiOptions.FromEnvironment"/> lee <c>API_MAX_LIST_PATCHES</c>,
/// <c>API_OVERRIDE_DEFAULT_TTL_HOURS</c> y <c>API_MAX_LIST_RUNS</c> del proceso. Cada test guarda
/// y restaura las tres variables para no filtrar estado entre tests ni al entorno de desarrollo.
/// </summary>
[Collection(EnvVarCollection.Name)]
public class ApiOptionsTests
{
    private const string MaxListVar = "API_MAX_LIST_PATCHES";
    private const string TtlVar = "API_OVERRIDE_DEFAULT_TTL_HOURS";
    private const string MaxRunsVar = "API_MAX_LIST_RUNS";

    private static ApiOptions WithEnv(string? maxList, string? ttlHours, string? maxRuns = null)
    {
        var savedMaxList = Environment.GetEnvironmentVariable(MaxListVar);
        var savedTtl = Environment.GetEnvironmentVariable(TtlVar);
        var savedMaxRuns = Environment.GetEnvironmentVariable(MaxRunsVar);
        try
        {
            Environment.SetEnvironmentVariable(MaxListVar, maxList);
            Environment.SetEnvironmentVariable(TtlVar, ttlHours);
            Environment.SetEnvironmentVariable(MaxRunsVar, maxRuns);
            return ApiOptions.FromEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaxListVar, savedMaxList);
            Environment.SetEnvironmentVariable(TtlVar, savedTtl);
            Environment.SetEnvironmentVariable(MaxRunsVar, savedMaxRuns);
        }
    }

    [Fact]
    public void Variables_ausentes_devuelven_los_defaults()
    {
        var options = WithEnv(null, null);

        Assert.Equal(100, options.MaxListPatches);
        Assert.Equal(TimeSpan.FromHours(24), options.OverrideDefaultTtl);
        Assert.Equal(20, options.MaxListRuns);
    }

    [Fact]
    public void Valores_validos_se_respetan()
    {
        var options = WithEnv("50", "8", "5");

        Assert.Equal(50, options.MaxListPatches);
        Assert.Equal(TimeSpan.FromHours(8), options.OverrideDefaultTtl);
        Assert.Equal(5, options.MaxListRuns);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-5")]
    public void MaxListPatches_no_numerico_o_no_positivo_cae_al_default(string basura)
    {
        var options = WithEnv(basura, null);

        Assert.Equal(100, options.MaxListPatches);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-5")]
    public void OverrideDefaultTtl_no_numerico_o_no_positivo_cae_al_default(string basura)
    {
        var options = WithEnv(null, basura);

        Assert.Equal(TimeSpan.FromHours(24), options.OverrideDefaultTtl);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-5")]
    public void MaxListRuns_no_numerico_o_no_positivo_cae_al_default(string basura)
    {
        var options = WithEnv(null, null, basura);

        Assert.Equal(20, options.MaxListRuns);
    }
}
