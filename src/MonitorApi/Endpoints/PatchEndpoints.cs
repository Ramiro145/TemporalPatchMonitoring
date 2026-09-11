using Contracts.Api;
using Contracts.Domain;
using Contracts.State;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MonitorApi.Endpoints;

/// <summary>
/// Handlers `static` de la superficie de lectura/override de patches (spec 08), construidos
/// con <see cref="TypedResults"/> para que los tests los inviertan directo sin host HTTP.
/// </summary>
public static class PatchEndpoints
{
    public static async Task<Ok<PatchListResponse>> ListAsync(
        IPatchStateStore store, ApiOptions options, CancellationToken ct = default)
    {
        var keys = await store.ListAsync(ct).ConfigureAwait(false);
        var limited = keys.Take(options.MaxListPatches).ToArray();
        var truncated = keys.Count > options.MaxListPatches;

        var summaries = new List<PatchSummaryResponse>(limited.Length);
        foreach (var key in limited)
        {
            PatchSummaryResponse summary;
            try
            {
                var state = await store.GetStateAsync(key, ct).ConfigureAwait(false);
                summary = state is null
                    ? PatchSummaryResponse.Unreadable(key, "El entity no existe todavía.")
                    : PatchSummaryResponse.FromState(state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Una key ilegible no puede tirar abajo el listado entero.
                summary = PatchSummaryResponse.Unreadable(key, ex.Message);
            }

            summaries.Add(summary);
        }

        return TypedResults.Ok(new PatchListResponse(summaries.Count, truncated, summaries));
    }

    public static async Task<Results<Ok<PatchDetailResponse>, NotFound>> GetAsync(
        string ns, string type, string patchId, IPatchStateStore store, CancellationToken ct = default)
    {
        var key = new PatchKey(ns, type, patchId);
        var state = await store.GetStateAsync(key, ct).ConfigureAwait(false);

        return state is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(PatchDetailResponse.FromState(state));
    }
}
