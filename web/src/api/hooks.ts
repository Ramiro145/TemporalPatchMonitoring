import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";

// GET /patches hace una query por patch contra Temporal (N+1, ver PatchEndpoints.ListAsync):
// refetchInterval más lento que /health y /schedule a propósito.
const FAST_INTERVAL_MS = 15_000;
const SLOW_INTERVAL_MS = 30_000;

export function useHealth() {
  return useQuery({
    queryKey: ["health"],
    queryFn: api.health,
    refetchInterval: FAST_INTERVAL_MS,
  });
}

export function useSchedule() {
  return useQuery({
    queryKey: ["schedule"],
    queryFn: api.schedule,
    refetchInterval: FAST_INTERVAL_MS,
  });
}

export function usePatches() {
  return useQuery({
    queryKey: ["patches"],
    queryFn: api.patches,
    refetchInterval: SLOW_INTERVAL_MS,
  });
}

export function usePatch(ns: string, type: string, patchId: string) {
  return useQuery({
    queryKey: ["patch", ns, type, patchId],
    queryFn: () => api.patch(ns, type, patchId),
  });
}

export function useRuns() {
  return useQuery({
    queryKey: ["runs"],
    queryFn: api.runs,
    refetchInterval: SLOW_INTERVAL_MS,
  });
}

export function useTriggerSchedule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: api.triggerSchedule,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["schedule"] }),
  });
}

export function usePauseSchedule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (note?: string) => api.pauseSchedule(note),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["schedule"] }),
  });
}

export function useUnpauseSchedule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (note?: string) => api.unpauseSchedule(note),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["schedule"] }),
  });
}
