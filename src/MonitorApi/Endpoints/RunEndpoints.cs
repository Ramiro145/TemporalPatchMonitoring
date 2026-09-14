using Contracts.Api;
using Contracts.Monitor;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MonitorApi.Endpoints;

/// <summary>
/// Handler `static` sobre <see cref="IMonitorRunReader"/> (spec 11), mismo estilo que
/// <see cref="PatchEndpoints"/>/<see cref="ScheduleEndpoints"/>.
/// </summary>
public static class RunEndpoints
{
    public static async Task<Ok<RunListResponse>> ListAsync(
        IMonitorRunReader reader, ApiOptions options, CancellationToken ct = default)
    {
        var runs = await reader.ListRecentAsync(options.MaxListRuns, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunListResponse(runs));
    }
}

/// <summary>Cuerpo de la respuesta de <c>GET /runs</c>.</summary>
public sealed record RunListResponse(IReadOnlyList<MonitorRunView> Runs);
