import { cn } from "@/lib/utils"

/** Punto verde/rojo/gris para health.temporal ("ok" / "unreachable" / sin dato todavía). */
export function StatusDot({ status }: { status: "ok" | "unreachable" | "unknown" }) {
  return (
    <span
      role="status"
      aria-label={status}
      className={cn(
        "inline-block size-2.5 shrink-0 rounded-full",
        status === "ok" && "bg-emerald-500",
        status === "unreachable" && "bg-red-500",
        status === "unknown" && "bg-muted-foreground/40",
      )}
    />
  )
}
