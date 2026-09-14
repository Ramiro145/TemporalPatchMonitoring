import { ArrowRight, ChevronRight } from "lucide-react"
import { PHASE_LABEL } from "@/api/enums"
import { cn } from "@/lib/utils"

const STEPS = [1, 2, 3]

/**
 * El recorrido 1 → 2 → 3 con la fase actual resaltada. Cuando el outcome vigente es Ready (2) y
 * hay nextPhase, el separador que lleva a esa fase se dibuja como flecha en vez de chevron.
 */
export function PhaseTrack({
  currentPhase,
  nextPhase,
  outcome,
}: {
  currentPhase: number
  nextPhase: number | null
  outcome: number | null
}) {
  if (!STEPS.includes(currentPhase)) {
    return <span className="text-sm text-muted-foreground">Fase desconocida</span>
  }

  const readyToAdvance = outcome === 2 && nextPhase !== null

  return (
    <div className="flex items-center gap-1.5">
      {STEPS.map((step, i) => {
        const isCurrent = step === currentPhase
        const isNext = readyToAdvance && step === nextPhase
        const arrowsHere = i > 0 && readyToAdvance && STEPS[i - 1] === currentPhase && step === nextPhase

        return (
          <div key={step} className="flex items-center gap-1.5">
            {i > 0 &&
              (arrowsHere ? (
                <ArrowRight className="size-3.5 text-emerald-600 dark:text-emerald-400" />
              ) : (
                <ChevronRight className="size-3.5 text-muted-foreground/40" />
              ))}
            <span
              className={cn(
                "rounded-full px-2 py-0.5 text-xs font-medium",
                isCurrent
                  ? "bg-primary text-primary-foreground"
                  : isNext
                    ? "border border-dashed border-emerald-500 text-emerald-600 dark:text-emerald-400"
                    : "bg-muted text-muted-foreground",
              )}
            >
              {PHASE_LABEL[step]}
            </span>
          </div>
        )
      })}
    </div>
  )
}
