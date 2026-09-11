using Contracts.Api;
using PatchMonitor.Tests.State;
using Xunit;

namespace PatchMonitor.Tests.Api;

/// <summary>
/// <see cref="ApiOptions.FromEnvironment"/> lee <c>API_MAX_LIST_PATCHES</c> y
/// <c>API_OVERRIDE_DEFAULT_TTL_HOURS</c> del proceso. Cada test guarda y restaura ambas
/// variables para no filtrar estado entre tests ni al entorno de desarrollo.
/// </summary>
[Collection(EnvVarCollection.Name)]
public class ApiOptionsTests
{
    private const string MaxListVar = "API_MAX_LIST_PATCHES";
    private const string TtlVar = "API_OVERRIDE_DEFAULT_TTL_HOURS";

    private static ApiOptions WithEnv(string? maxList, string? ttlHours)
    {
        var savedMaxList = Environment.GetEnvironmentVariable(MaxListVar);
        var savedTtl = Environment.GetEnvironmentVariable(TtlVar);
        try
        {
            Environment.SetEnvironmentVariable(MaxListVar, maxList);
            Environment.SetEnvironmentVariable(TtlVar, ttlHours);
            return ApiOptions.FromEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaxListVar, savedMaxList);
            Environment.SetEnvironmentVariable(TtlVar, savedTtl);
        }
    }

    [Fact]
    public void Variables_ausentes_devuelven_los_defaults()
    {
        var options = WithEnv(null, null);

        Assert.Equal(100, options.MaxListPatches);
        Assert.Equal(TimeSpan.FromHours(24), options.OverrideDefaultTtl);
    }

    [Fact]
    public void Valores_validos_se_respetan()
    {
        var options = WithEnv("50", "8");

        Assert.Equal(50, options.MaxListPatches);
        Assert.Equal(TimeSpan.FromHours(8), options.OverrideDefaultTtl);
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
}
