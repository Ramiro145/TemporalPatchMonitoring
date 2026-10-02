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

        // Lecturas en paralelo acotado (spec 16, B-5): el orden de salida es el del registry.
        var summaries = await BoundedParallel.SelectAsync(
            limited,
            options.ListPatchesConcurrency,
            async (key, token) =>
            {
                try
                {
                    var state = await store.GetStateAsync(key, token).ConfigureAwait(false);
                    return state is null
                        ? PatchSummaryResponse.Unreadable(key, "El entity no existe todavía.")
                        : PatchSummaryResponse.FromState(state);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Una key ilegible no puede tirar abajo el listado entero.
                    return PatchSummaryResponse.Unreadable(key, ex.Message);
                }
            },
            ct).ConfigureAwait(false);

        return TypedResults.Ok(new PatchListResponse(summaries.Length, truncated, summaries));
    }

    public static async Task<Results<Ok<PatchDetailResponse>, NotFound, ProblemHttpResult>> GetAsync(
        string ns, string type, string patchId, IPatchStateStore store, CancellationToken ct = default)
    {
        var key = new PatchKey(ns, type, patchId);

        PatchState? state;
        try
        {
            state = await store.GetStateAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreadable(key, ex);
        }

        return state is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(PatchDetailResponse.FromState(state));
    }

    /// <summary>
    /// Spec 17: un entity que no se puede leer (p. ej. replay roto) es un fallo del backend, no
    /// un 500 sin cuerpo: 503 con el motivo en <c>detail</c>, como el <c>Error</c> que el listado
    /// ya expone en <see cref="PatchSummaryResponse.Unreadable"/>.
    /// </summary>
    private static ProblemHttpResult Unreadable(PatchKey key, Exception ex) =>
        TypedResults.Problem(
            title: "El entity del patch no se pudo leer.",
            detail: $"{key}: {ex.Message}",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    public static async Task<Results<Ok<PatchDetailResponse>, BadRequest<string>, NotFound, Conflict<string>, ProblemHttpResult>> SetOverrideAsync(
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

        PatchState? state;
        try
        {
            state = await store.GetStateAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreadable(key, ex);
        }

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

        try
        {
            var updated = await store.SetOverrideAsync(ov, ct).ConfigureAwait(false);
            return TypedResults.Ok(PatchDetailResponse.FromState(updated));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreadable(key, ex);
        }
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
