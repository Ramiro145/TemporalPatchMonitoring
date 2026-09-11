using Contracts.Api;
using Contracts.Domain;
using Contracts.Phase;
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

    public static async Task<Results<Ok<PatchDetailResponse>, BadRequest<string>, NotFound, Conflict<string>>> SetOverrideAsync(
        string ns, string type, string patchId, SetOverrideRequest request, bool? force,
        IPatchStateStore store, ApiOptions options, CancellationToken ct = default)
    {
        if (request.Phase == PatchPhase.Unknown || string.IsNullOrWhiteSpace(request.DeclaredBy))
        {
            return TypedResults.BadRequest(
                "Phase no puede ser Unknown y DeclaredBy no puede estar vacío.");
        }

        var declaredAt = DateTimeOffset.UtcNow;
        if (request.ExpiresAt is { } expiresAt && expiresAt <= declaredAt)
        {
            return TypedResults.BadRequest("ExpiresAt ya pasó.");
        }

        var key = new PatchKey(ns, type, patchId);
        var state = await store.GetStateAsync(key, ct).ConfigureAwait(false);
        if (state is null)
        {
            return TypedResults.NotFound();
        }

        // Redeclarar la fase vigente no es un salto: nunca da 409, con o sin force.
        var isJump = request.Phase != state.Phase;
        if (isJump && !PhaseTransition.IsLegal(state.Phase, request.Phase) && force != true)
        {
            return TypedResults.Conflict(
                $"Transición ilegal de {state.Phase} a {request.Phase}. Repetí con ?force=true si es una corrección deliberada.");
        }

        var ov = new PhaseOverride(
            key, request.Phase, request.DeclaredBy, declaredAt,
            request.ExpiresAt ?? declaredAt + options.OverrideDefaultTtl);

        var updated = await store.SetOverrideAsync(ov, ct).ConfigureAwait(false);
        return TypedResults.Ok(PatchDetailResponse.FromState(updated));
    }

    public static async Task<Results<Ok<PatchDetailResponse>, NotFound>> ClearOverrideAsync(
        string ns, string type, string patchId, IPatchStateStore store, CancellationToken ct = default)
    {
        var key = new PatchKey(ns, type, patchId);
        var state = await store.GetStateAsync(key, ct).ConfigureAwait(false);
        if (state is null)
        {
            return TypedResults.NotFound();
        }

        var updated = await store.ClearOverrideAsync(key, ct).ConfigureAwait(false);
        return TypedResults.Ok(PatchDetailResponse.FromState(updated));
    }
}
