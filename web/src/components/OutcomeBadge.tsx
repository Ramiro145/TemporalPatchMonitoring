import { OUTCOME_LABEL } from "@/api/enums"
import { Badge } from "@/components/ui/badge"
import { cn } from "@/lib/utils"

const OUTCOME_CLASS: Record<number, string> = {
  0: "bg-muted text-muted-foreground",
  1: "bg-red-100 text-red-700 dark:bg-red-500/20 dark:text-red-300",
  2: "bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-300",
}

/** Badge del gate (Inconclusive/Blocked/Ready), o "Sin veredicto" cuando outcome es null. */
export function OutcomeBadge({
  outcome,
  className,
}: {
  outcome: number | null
  className?: string
}) {
  if (outcome === null) {
    return (
      <Badge
        variant="outline"
        className={cn("border-transparent bg-muted text-muted-foreground", className)}
      >
        Sin veredicto
      </Badge>
    )
  }

  return (
    <Badge
      variant="outline"
      className={cn("border-transparent", OUTCOME_CLASS[outcome] ?? OUTCOME_CLASS[0], className)}
    >
      {OUTCOME_LABEL[outcome] ?? `Outcome ${outcome}`}
    </Badge>
  )
}
