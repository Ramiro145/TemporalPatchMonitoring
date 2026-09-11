using Contracts.Monitor;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MonitorApi.Endpoints;

/// <summary>
/// Handlers `static` sobre <see cref="IScheduleController"/> (spec 08). <c>503</c> —no
/// <c>404</c>— cuando el Schedule no existe: significa que el worker nunca arrancó, una falla
/// de disponibilidad del sistema, no un recurso ausente.
/// </summary>
public static class ScheduleEndpoints
{
    public static async Task<Results<Ok<ScheduleStatus>, StatusCodeHttpResult>> DescribeAsync(
        IScheduleController controller, CancellationToken ct = default)
    {
        var status = await controller.DescribeAsync(ct).ConfigureAwait(false);
        return ToResult(status);
    }

    public static async Task<Results<Ok<ScheduleStatus>, StatusCodeHttpResult>> PauseAsync(
        string? note, IScheduleController controller, CancellationToken ct = default)
    {
        var paused = await controller.PauseAsync(note, ct).ConfigureAwait(false);
        if (!paused)
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var status = await controller.DescribeAsync(ct).ConfigureAwait(false);
        return ToResult(status);
    }

    public static async Task<Results<Ok<ScheduleStatus>, StatusCodeHttpResult>> UnpauseAsync(
        string? note, IScheduleController controller, CancellationToken ct = default)
    {
        var unpaused = await controller.UnpauseAsync(note, ct).ConfigureAwait(false);
        if (!unpaused)
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var status = await controller.DescribeAsync(ct).ConfigureAwait(false);
        return ToResult(status);
    }

    public static async Task<Results<Accepted<TriggerResponse>, StatusCodeHttpResult>> TriggerAsync(
        IScheduleController controller, CancellationToken ct = default)
    {
        var triggered = await controller.TriggerAsync(ct).ConfigureAwait(false);

        return triggered
            ? TypedResults.Accepted((string?)null, new TriggerResponse(true))
            : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static Results<Ok<ScheduleStatus>, StatusCodeHttpResult> ToResult(ScheduleStatus? status) =>
        status is null
            ? TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : TypedResults.Ok(status);
}

/// <summary>Cuerpo de la respuesta <c>202</c> de <c>POST /schedule/trigger</c>.</summary>
public sealed record TriggerResponse(bool Triggered);
