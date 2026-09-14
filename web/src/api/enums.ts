// Único lugar del front donde viven los mapeos numéricos de src/Contracts/Domain/PatchPhase.cs,
// GateOutcome.cs y src/Contracts/Phase/PhaseResolution.cs (PhaseSource). Ningún componente de UI
// compara contra 0/1/2/3 directamente fuera de este archivo.

export const PHASE_LABEL: Record<number, string> = {
  0: "Desconocida",
  1: "Convivencia",
  2: "Deprecación",
  3: "Código limpio",
};

export const OUTCOME_LABEL: Record<number, string> = {
  0: "Sin datos",
  1: "Bloqueado",
  2: "Listo",
};

export const SOURCE_LABEL: Record<number, string> = {
  0: "Inferida",
  1: "Override",
};
