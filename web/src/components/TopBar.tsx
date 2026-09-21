import { Pause, Play, RotateCcw } from "lucide-react";
import { NavLink } from "react-router";
import {
  useHealth,
  usePauseSchedule,
  useSchedule,
  useTriggerSchedule,
  useUnpauseSchedule,
} from "@/api/hooks";
import { ScheduleUnavailableError } from "@/api/client";
import { RelativeTime } from "@/components/RelativeTime";
import { StatusDot } from "@/components/StatusDot";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

function formatInterval(interval: string): string {
  const [h, m, s] = interval.split(":").map(Number);
  const parts: string[] = [];
  if (h) parts.push(`${h} h`);
  if (m) parts.push(`${m} min`);
  if (s && !h && !m) parts.push(`${s} s`);
  return parts.length ? `cada ${parts.join(" ")}` : interval;
}

function navLinkClass({ isActive }: { isActive: boolean }): string {
  return cn(
    "rounded-md px-2.5 py-1 text-sm font-medium transition-colors",
    isActive
      ? "bg-muted text-foreground"
      : "text-muted-foreground hover:text-foreground",
  );
}

/**
 * Barra superior compartida por todas las vistas (spec 11): salud del cluster propio, estado
 * del Schedule con sus acciones, y la navegación entre Patches y Corridas.
 */
export function TopBar() {
  const health = useHealth();
  const schedule = useSchedule();
  const trigger = useTriggerSchedule();
  const pause = usePauseSchedule();
  const unpause = useUnpauseSchedule();

  const scheduleUnavailable =
    schedule.error instanceof ScheduleUnavailableError;
  const healthStatus = health.data
    ? health.data.temporal
    : health.isError
      ? "unreachable"
      : "unknown";

  return (
    <header className="flex flex-wrap items-center justify-between gap-3 border-b bg-background px-4 py-3">
      <div className="flex items-center gap-4">
        <span className="font-heading text-sm font-semibold">PatchMonitor</span>
        <nav className="flex items-center gap-1">
          <NavLink to="/" end className={navLinkClass}>
            Patches
          </NavLink>
          <NavLink to="/runs" className={navLinkClass}>
            Ejecuciones
          </NavLink>
        </nav>
      </div>

      <div className="flex flex-wrap items-center gap-4 text-sm">
        <div className="flex items-center gap-1.5">
          <StatusDot status={healthStatus} />
          <span className="text-muted-foreground">
            {health.data
              ? health.data.temporal === "ok"
                ? "Temporal activo"
                : "Temporal inaccesible"
              : "Salud desconocida"}
          </span>
        </div>

        {health.data && (
          <span className="text-muted-foreground">
            monitor: {health.data.monitorNamespace} · observado:{" "}
            {health.data.targetNamespace}
          </span>
        )}

        {scheduleUnavailable && (
          <span className="text-muted-foreground">Schedule no disponible</span>
        )}

        {schedule.data && (
          <>
            <span className="text-muted-foreground">
              {schedule.data.paused
                ? "Pausado"
                : formatInterval(schedule.data.interval)}{" "}
              · última <RelativeTime at={schedule.data.lastRunAt} /> · próxima{" "}
              <RelativeTime at={schedule.data.nextRunAt} />
            </span>
            <div className="flex items-center gap-1.5">
              <Button
                size="sm"
                variant="outline"
                onClick={() => trigger.mutate()}
                disabled={trigger.isPending}
              >
                <Play /> Disparar ahora
              </Button>
              {schedule.data.paused ? (
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => unpause.mutate(undefined)}
                  disabled={unpause.isPending}
                >
                  <RotateCcw /> Reanudar
                </Button>
              ) : (
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => pause.mutate(undefined)}
                  disabled={pause.isPending}
                >
                  <Pause /> Pausar
                </Button>
              )}
            </div>
          </>
        )}
      </div>
    </header>
  );
}
