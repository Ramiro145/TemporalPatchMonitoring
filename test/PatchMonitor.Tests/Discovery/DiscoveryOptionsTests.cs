using Contracts.Discovery;
using Xunit;

namespace PatchMonitor.Tests.Discovery;

/// <summary>
/// <see cref="DiscoveryOptions.FromEnvironment"/> lee variables de entorno del proceso. Cada
/// test guarda y restaura las cuatro variables para no filtrar estado entre tests ni al
/// entorno de desarrollo. Ningún otro test de esta suite toca estos nombres.
/// </summary>
public class DiscoveryOptionsTests
{
    private static readonly string[] Vars =
    {
        "TARGET_TEMPORAL_NAMESPACE",
        "TARGET_TEMPORAL_HOST",
        "DISCOVERY_LOOKBACK_DAYS",
        "DISCOVERY_MAX_EXECUTIONS",
        "DISCOVERY_MAX_HISTORIES",
    };

    private static DiscoveryOptions WithEnv(IDictionary<string, string?> values)
    {
        var saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Vars)
            {
                Environment.SetEnvironmentVariable(
                    name, values.TryGetValue(name, out var v) ? v : null);
            }

            return DiscoveryOptions.FromEnvironment();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void Todas_ausentes_devuelve_los_defaults()
    {
        var options = WithEnv(new Dictionary<string, string?>());

        Assert.Equal(DiscoveryOptions.DefaultNamespace, options.Namespace);
        Assert.Equal(DiscoveryOptions.DefaultTargetHost, options.TargetHost);
        Assert.Equal(DiscoveryOptions.DefaultLookbackDays, options.LookbackDays);
        Assert.Equal(DiscoveryOptions.DefaultMaxExecutions, options.MaxExecutions);
        Assert.Equal(DiscoveryOptions.DefaultMaxHistories, options.MaxHistories);
    }

    [Fact]
    public void Cada_variable_seteada_con_valor_valido_se_respeta()
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["TARGET_TEMPORAL_NAMESPACE"] = "releaseorder",
            ["TARGET_TEMPORAL_HOST"] = "host.docker.internal:7233",
            ["DISCOVERY_LOOKBACK_DAYS"] = "30",
            ["DISCOVERY_MAX_EXECUTIONS"] = "1000",
            ["DISCOVERY_MAX_HISTORIES"] = "50",
        });

        Assert.Equal("releaseorder", options.Namespace);
        Assert.Equal("host.docker.internal:7233", options.TargetHost);
        Assert.Equal(30, options.LookbackDays);
        Assert.Equal(1000, options.MaxExecutions);
        Assert.Equal(50, options.MaxHistories);
    }

    [Theory]
    [InlineData("no-soy-un-numero")]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Valor_basura_o_no_positivo_en_un_entero_cae_al_default(string basura)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["DISCOVERY_LOOKBACK_DAYS"] = basura,
            ["DISCOVERY_MAX_EXECUTIONS"] = basura,
            ["DISCOVERY_MAX_HISTORIES"] = basura,
        });

        Assert.Equal(DiscoveryOptions.DefaultLookbackDays, options.LookbackDays);
        Assert.Equal(DiscoveryOptions.DefaultMaxExecutions, options.MaxExecutions);
        Assert.Equal(DiscoveryOptions.DefaultMaxHistories, options.MaxHistories);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Namespace_vacio_o_en_blanco_cae_al_default(string blanco)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["TARGET_TEMPORAL_NAMESPACE"] = blanco,
        });

        Assert.Equal(DiscoveryOptions.DefaultNamespace, options.Namespace);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TargetHost_vacio_o_en_blanco_cae_al_default(string blanco)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["TARGET_TEMPORAL_HOST"] = blanco,
        });

        Assert.Equal(DiscoveryOptions.DefaultTargetHost, options.TargetHost);
    }

    [Fact]
    public void TargetHost_ausente_cae_al_default()
    {
        var options = WithEnv(new Dictionary<string, string?>());

        Assert.Equal(DiscoveryOptions.DefaultTargetHost, options.TargetHost);
    }
}
