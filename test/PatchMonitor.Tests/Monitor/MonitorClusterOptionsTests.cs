using Contracts.Monitor;
using Xunit;

namespace PatchMonitor.Tests.Monitor;

/// <summary>
/// <see cref="MonitorClusterOptions.FromEnvironment"/> lee variables de entorno del proceso.
/// Cada test guarda y restaura las dos variables para no filtrar estado entre tests ni al
/// entorno de desarrollo. Ningún otro test de esta suite toca estos nombres.
/// </summary>
public class MonitorClusterOptionsTests
{
    private static readonly string[] Vars =
    {
        "TEMPORAL_HOST",
        "TEMPORAL_NAMESPACE",
    };

    private static MonitorClusterOptions WithEnv(IDictionary<string, string?> values)
    {
        var saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Vars)
            {
                Environment.SetEnvironmentVariable(
                    name, values.TryGetValue(name, out var v) ? v : null);
            }

            return MonitorClusterOptions.FromEnvironment();
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

        Assert.Equal(MonitorClusterOptions.DefaultHost, options.Host);
        Assert.Equal(MonitorClusterOptions.DefaultNamespace, options.Namespace);
    }

    [Fact]
    public void Cada_variable_seteada_con_valor_valido_se_respeta()
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["TEMPORAL_HOST"] = "host.docker.internal:7233",
            ["TEMPORAL_NAMESPACE"] = "monitor-custom",
        });

        Assert.Equal("host.docker.internal:7233", options.Host);
        Assert.Equal("monitor-custom", options.Namespace);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Host_vacio_o_en_blanco_cae_al_default(string blanco)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["TEMPORAL_HOST"] = blanco,
        });

        Assert.Equal(MonitorClusterOptions.DefaultHost, options.Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Namespace_vacio_o_en_blanco_cae_al_default(string blanco)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["TEMPORAL_NAMESPACE"] = blanco,
        });

        Assert.Equal(MonitorClusterOptions.DefaultNamespace, options.Namespace);
    }

    [Fact]
    public void Host_ausente_cae_al_default()
    {
        var options = WithEnv(new Dictionary<string, string?>());

        Assert.Equal(MonitorClusterOptions.DefaultHost, options.Host);
    }

    [Fact]
    public void Namespace_ausente_cae_al_default()
    {
        var options = WithEnv(new Dictionary<string, string?>());

        Assert.Equal(MonitorClusterOptions.DefaultNamespace, options.Namespace);
    }
}
