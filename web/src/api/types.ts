// Tipos TypeScript espejo de los DTOs reales de MonitorApi (spec 11), en camelCase igual que
// la serialización por defecto de System.Text.Json. Los campos numéricos de enums (phase,
// source, outcome) se mapean a texto legible únicamente en ./enums.ts.

export interface Health {
  temporal: "ok" | "unreachable";
  targetNamespace: string;
  taskQueue: string;
}

export interface PatchKey {
  namespace: string;
  workflowType: string;
  patchId: string;
}

export interface PatchSummary {
  namespace: string;
  workflowType: string;
  patchId: string;
  phase: number;
  source: number;
  outcome: number | null;
  nextPhase: number | null;
  blockingExecutionCount: number;
  hasOverride: boolean;
  revision: number;
  lastObservedAt: string | null;
  lastChangedAt: string | null;
}

export interface PatchListResponse {
  count: number;
  truncated: boolean;
  patches: PatchSummary[];
}

export interface PhaseVerdict {
  outcome: number;
  currentPhase: number;
  nextPhase: number | null;
  blockingExecutionCount: number;
  blockingSample: string[];
  reason: string;
  evaluatedAt: string;
}

export interface PatchStateChange {
  at: string;
  fromPhase: number;
  toPhase: number;
  fromOutcome: number | null;
  toOutcome: number | null;
  reason: string;
}

export interface PhaseOverride {
  key: PatchKey;
  phase: number;
  declaredBy: string;
  declaredAt: string;
  expiresAt: string | null;
}

export interface PatchDetail {
  summary: PatchSummary;
  phaseReason: string;
  lastVerdict: PhaseVerdict | null;
  previousVerdict: PhaseVerdict | null;
  override: PhaseOverride | null;
  assessmentCount: number;
  notifiedRevision: number;
  history: PatchStateChange[];
}

export interface ScheduleStatus {
  scheduleId: string;
  paused: boolean;
  note: string | null;
  interval: string; // "hh:mm:ss"
  lastRunAt: string | null;
  nextRunAt: string | null;
  runningCount: number;
  numActions: number;
}

export interface TriggerResponse {
  triggered: boolean;
}

export interface MonitorRunSummary {
  startedAt: string;
  finishedAt: string;
  patchesDiscovered: number;
  patchesAssessed: number;
  verdictsChanged: number;
  overridesLoaded: number;
  errors: string[];
  notificationsSent: number;
  notificationsFailed: number;
}

export interface MonitorRun {
  workflowId: string;
  runId: string;
  startedAt: string;
  closedAt: string | null;
  status: string;
  summary: MonitorRunSummary | null;
}

export interface RunListResponse {
  runs: MonitorRun[];
}
