import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"

// Algoritmo estándar de "hace X" (web.dev): divide la duración progresivamente hasta encontrar
// la unidad donde el resto cabe.
const DIVISIONS: { amount: number; unit: Intl.RelativeTimeFormatUnit }[] = [
  { amount: 60, unit: "seconds" },
  { amount: 60, unit: "minutes" },
  { amount: 24, unit: "hours" },
  { amount: 7, unit: "days" },
  { amount: 4.34524, unit: "weeks" },
  { amount: 12, unit: "months" },
  { amount: Number.POSITIVE_INFINITY, unit: "years" },
]

const formatter = new Intl.RelativeTimeFormat("es", { numeric: "auto" })

function formatRelative(iso: string): string {
  let duration = (new Date(iso).getTime() - Date.now()) / 1000

  for (const division of DIVISIONS) {
    if (Math.abs(duration) < division.amount) {
      return formatter.format(Math.round(duration), division.unit)
    }
    duration /= division.amount
  }

  return formatter.format(Math.round(duration), "years")
}

/** Formatea un DateTimeOffset ISO como "hace X"; el hover muestra la fecha/hora absoluta. */
export function RelativeTime({ at, fallback = "nunca" }: { at: string | null; fallback?: string }) {
  if (!at) {
    return <span className="text-muted-foreground">{fallback}</span>
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="cursor-default">{formatRelative(at)}</span>
      </TooltipTrigger>
      <TooltipContent>{new Date(at).toLocaleString("es-AR")}</TooltipContent>
    </Tooltip>
  )
}
