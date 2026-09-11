using System.Net;
using Contracts.Domain;
using Contracts.Notification;
using PatchMonitor.Services;
using Temporalio.Exceptions;
using Xunit;

namespace PatchMonitor.Tests.Notification;

/// <summary>
/// <see cref="WebhookNotifier"/> sobre un <see cref="HttpMessageHandler"/> fake: distingue
/// éxito, <c>4xx</c> no reintentable y <c>5xx</c>/<c>408</c>/<c>429</c> reintentable, y agrega
/// el header de auth cuando está configurado.
/// </summary>
public class WebhookNotifierTests
{
    private static readonly VerdictChangeNotification Notification = new(
        "wf#1",
        new PatchKey("default", "OrderWorkflow", "order-v2"),
        1,
        PatchPhase.Unknown,
        PatchPhase.Coexistence,
        null,
        GateOutcome.Blocked,
        PatchPhase.Deprecated,
        0,
        Array.Empty<string>(),
        "alta",
        DateTimeOffset.UtcNow);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public FakeHandler(HttpStatusCode status)
        {
            _status = status;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    private static NotificationOptions Options(string? authHeader = null) => new(
        Enabled: true,
        WebhookUrl: "https://example.org/hook",
        WebhookAuthHeader: authHeader,
        WebhookTimeout: TimeSpan.FromSeconds(10),
        MaxAttempts: 3);

    [Fact]
    public async Task Status_200_no_lanza_y_lleva_el_header_de_auth()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var http = new HttpClient(handler);
        var notifier = new WebhookNotifier(http, Options("Authorization: Bearer token"));

        await notifier.NotifyAsync(Notification);

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.TryGetValues("Authorization", out var values));
        Assert.Equal("Bearer token", values!.Single());
    }

    [Fact]
    public async Task Status_500_lanza_excepcion_reintentable()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        var http = new HttpClient(handler);
        var notifier = new WebhookNotifier(http, Options());

        var ex = await Record.ExceptionAsync(() => notifier.NotifyAsync(Notification));

        Assert.NotNull(ex);
        Assert.IsNotType<ApplicationFailureException>(ex);
    }

    [Fact]
    public async Task Status_400_lanza_ApplicationFailureException_no_reintentable()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest);
        var http = new HttpClient(handler);
        var notifier = new WebhookNotifier(http, Options());

        var ex = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => notifier.NotifyAsync(Notification));

        Assert.True(ex.NonRetryable);
    }

    [Fact]
    public async Task Status_429_lanza_excepcion_reintentable_no_ApplicationFailureException()
    {
        var handler = new FakeHandler(HttpStatusCode.TooManyRequests);
        var http = new HttpClient(handler);
        var notifier = new WebhookNotifier(http, Options());

        var ex = await Record.ExceptionAsync(() => notifier.NotifyAsync(Notification));

        Assert.NotNull(ex);
        Assert.IsNotType<ApplicationFailureException>(ex);
    }
}
