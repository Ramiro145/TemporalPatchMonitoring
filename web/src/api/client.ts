import type {
  Health,
  PatchDetail,
  PatchListResponse,
  RunListResponse,
  ScheduleStatus,
  TriggerResponse,
} from "./types";

const BASE = "/api";

/** Error HTTP genérico de MonitorApi: status + el cuerpo de la respuesta como texto. */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

/**
 * El 503 que devuelven los endpoints de /schedule* cuando el Schedule todavía no existe (el
 * worker nunca arrancó). Se distingue de un ApiError genérico para que la UI lo muestre como
 * "Schedule no disponible", no como una excepción cualquiera.
 */
export class ScheduleUnavailableError extends ApiError {
  constructor() {
    super(503, "Schedule no disponible");
    this.name = "ScheduleUnavailableError";
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE}${path}`, init);

  if (response.status === 503 && path.startsWith("/schedule")) {
    throw new ScheduleUnavailableError();
  }

  // La API devuelve 400/409 en texto plano, no JSON (verificado en PatchEndpoints.cs).
  if (response.status === 400 || response.status === 409) {
    throw new ApiError(response.status, await response.text());
  }

  if (!response.ok) {
    throw new ApiError(response.status, await response.text().catch(() => response.statusText));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

function patchPath(ns: string, type: string, patchId: string): string {
  return `/patches/${encodeURIComponent(ns)}/${encodeURIComponent(type)}/${encodeURIComponent(patchId)}`;
}

export const api = {
  health: () => request<Health>("/health"),

  patches: () => request<PatchListResponse>("/patches"),
  patch: (ns: string, type: string, patchId: string) =>
    request<PatchDetail>(patchPath(ns, type, patchId)),

  runs: () => request<RunListResponse>("/runs"),

  schedule: () => request<ScheduleStatus>("/schedule"),
  triggerSchedule: () =>
    request<TriggerResponse>("/schedule/trigger", { method: "POST" }),
  pauseSchedule: (note?: string) =>
    request<ScheduleStatus>(
      `/schedule/pause${note ? `?note=${encodeURIComponent(note)}` : ""}`,
      { method: "POST" },
    ),
  unpauseSchedule: (note?: string) =>
    request<ScheduleStatus>(
      `/schedule/unpause${note ? `?note=${encodeURIComponent(note)}` : ""}`,
      { method: "POST" },
    ),
};
