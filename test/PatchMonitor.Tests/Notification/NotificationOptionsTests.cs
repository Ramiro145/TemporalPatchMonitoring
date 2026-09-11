using Contracts.Notification;
using Xunit;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="NotificationOptions.FromEnvironment"/> lee variables de entorno del proceso. Cada
/// test guarda y restaura las cinco variables para no filtrar estado entre tests ni al entorno
/// de desarrollo. Ningún otro test de esta suite toca estos nombres.
/// </summary>
public class NotificationOptionsTests
{
    private static readonly string[] Vars =
    {
        "NOTIFIER_ENABLED",
        "NOTIFIER_WEBHOOK_URL",
        "NOTIFIER_WEBHOOK_AUTH_HEADER",
        "NOTIFIER_WEBHOOK_TIMEOUT_SECONDS",
        "NOTIFIER_MAX_ATTEMPTS",
    };

    private static NotificationOptions WithEnv(IDictionary<string, string?> values)
    {
        var saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Vars)
            {
                Environment.SetEnvironmentVariable(
                    name, values.TryGetValue(name, out var v) ? v : null);
            }

            return NotificationOptions.FromEnvironment();
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

        Assert.True(options.Enabled);
        Assert.Null(options.WebhookUrl);
        Assert.Null(options.WebhookAuthHeader);
        Assert.Equal(TimeSpan.FromSeconds(NotificationOptions.DefaultWebhookTimeoutSeconds), options.WebhookTimeout);
        Assert.Equal(NotificationOptions.DefaultMaxAttempts, options.MaxAttempts);
    }

    [Fact]
    public void Cada_variable_seteada_con_valor_valido_se_respeta()
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["NOTIFIER_ENABLED"] = "true",
            ["NOTIFIER_WEBHOOK_URL"] = "https://example.org/hook",
            ["NOTIFIER_WEBHOOK_AUTH_HEADER"] = "Authorization: Bearer token",
            ["NOTIFIER_WEBHOOK_TIMEOUT_SECONDS"] = "30",
            ["NOTIFIER_MAX_ATTEMPTS"] = "5",
        });

        Assert.True(options.Enabled);
        Assert.Equal("https://example.org/hook", options.WebhookUrl);
        Assert.Equal("Authorization: Bearer token", options.WebhookAuthHeader);
        Assert.Equal(TimeSpan.FromSeconds(30), options.WebhookTimeout);
        Assert.Equal(5, options.MaxAttempts);
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
            ["NOTIFIER_WEBHOOK_TIMEOUT_SECONDS"] = basura,
            ["NOTIFIER_MAX_ATTEMPTS"] = basura,
        });

        Assert.Equal(TimeSpan.FromSeconds(NotificationOptions.DefaultWebhookTimeoutSeconds), options.WebhookTimeout);
        Assert.Equal(NotificationOptions.DefaultMaxAttempts, options.MaxAttempts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WebhookUrl_y_WebhookAuthHeader_vacios_o_en_blanco_quedan_null(string blanco)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["NOTIFIER_WEBHOOK_URL"] = blanco,
            ["NOTIFIER_WEBHOOK_AUTH_HEADER"] = blanco,
        });

        Assert.Null(options.WebhookUrl);
        Assert.Null(options.WebhookAuthHeader);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("False")]
    public void NotifierEnabled_false_en_cualquier_capitalizacion_deshabilita(string valor)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["NOTIFIER_ENABLED"] = valor,
        });

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("no-es-false")]
    [InlineData("0")]
    [InlineData("")]
    public void NotifierEnabled_cualquier_valor_distinto_de_false_habilita(string valor)
    {
        var options = WithEnv(new Dictionary<string, string?>
        {
            ["NOTIFIER_ENABLED"] = valor,
        });

        Assert.True(options.Enabled);
    }
}
