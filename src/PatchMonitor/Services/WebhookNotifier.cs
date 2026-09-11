using System.Net;
using System.Net.Http.Json;
using Contracts.Notification;
using Temporalio.Exceptions;

namespace PatchMonitor.Services;

/// <summary>
/// Notificador que hace <c>POST</c> del <see cref="VerdictChangeNotification"/> serializado
/// contra <see cref="NotificationOptions.WebhookUrl"/>, registrado en DI solo cuando esa URL
/// está configurada. Sobre un <see cref="HttpClient"/> singleton inyectado: un único uso no
/// justifica sumar <c>Microsoft.Extensions.Http</c>.
/// </summary>
public sealed class WebhookNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly NotificationOptions _options;

    public WebhookNotifier(HttpClient http, NotificationOptions options)
    {
        _http = http;
        _options = options;
    }

    public string Name => "webhook";

    /// <summary>
    /// <c>2xx</c> ⇒ éxito. <c>4xx</c> salvo <c>408</c>/<c>429</c> ⇒ el destino rechazó el
    /// payload de forma permanente, <see cref="ApplicationFailureException"/> no reintentable.
    /// Cualquier otro caso (<c>5xx</c>, <c>408</c>, <c>429</c>, timeout, error de red) ⇒
    /// excepción normal, que la <c>RetryPolicy</c> de la Activity reintenta.
    /// </summary>
    public async Task NotifyAsync(VerdictChangeNotification notification, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.WebhookUrl)
        {
            Content = JsonContent.Create(notification),
        };

        if (!string.IsNullOrWhiteSpace(_options.WebhookAuthHeader))
        {
            var separatorIndex = _options.WebhookAuthHeader.IndexOf(':');
            if (separatorIndex > 0)
            {
                var headerName = _options.WebhookAuthHeader[..separatorIndex].Trim();
                var headerValue = _options.WebhookAuthHeader[(separatorIndex + 1)..].Trim();
                request.Headers.TryAddWithoutValidation(headerName, headerValue);
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.WebhookTimeout);

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var isRetryable =
            status < 400
            || status >= 500
            || response.StatusCode == HttpStatusCode.RequestTimeout
            || response.StatusCode == HttpStatusCode.TooManyRequests;

        if (!isRetryable)
        {
            throw new ApplicationFailureException(
                $"El webhook rechazó la notificación con {status}.",
                errorType: "WebhookRejected",
                nonRetryable: true);
        }

        throw new HttpRequestException($"El webhook respondió {status}.");
    }
}
