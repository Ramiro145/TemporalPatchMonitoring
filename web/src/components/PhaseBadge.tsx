import { PHASE_LABEL } from "@/api/enums"
import { Badge } from "@/components/ui/badge"
import { cn } from "@/lib/utils"

const PHASE_CLASS: Record<number, string> = {
  0: "bg-muted text-muted-foreground",
  1: "bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-300",
  2: "bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-300",
  3: "bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-300",
}

/** Badge de fase (1/2/3, o Desconocida), coloreado según src/api/enums.ts. */
export function PhaseBadge({ phase, className }: { phase: number; className?: string }) {
  return (
    <Badge
      variant="outline"
      className={cn("border-transparent", PHASE_CLASS[phase] ?? PHASE_CLASS[0], className)}
    >
      {PHASE_LABEL[phase] ?? `Fase ${phase}`}
    </Badge>
  )
}
