import { useRuns } from "@/api/hooks"
import type { MonitorRun } from "@/api/types"
import { RelativeTime } from "@/components/RelativeTime"
import { TopBar } from "@/components/TopBar"
import { Badge } from "@/components/ui/badge"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"

const STATUS_VARIANT: Record<string, "secondary" | "outline" | "destructive"> = {
  Completed: "secondary",
  Running: "outline",
  Failed: "destructive",
  Terminated: "destructive",
  Canceled: "destructive",
  TimedOut: "destructive",
}

function formatDuration(startedAt: string, closedAt: string | null): string {
  if (!closedAt) {
    return "en curso"
  }

  const ms = new Date(closedAt).getTime() - new Date(startedAt).getTime()
  if (ms < 1000) return `${ms} ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`

  const minutes = Math.floor(ms / 60_000)
  const seconds = Math.round((ms % 60_000) / 1000)
  return `${minutes} min ${seconds} s`
}

function RunRow({ run }: { run: MonitorRun }) {
  const summary = run.summary

  return (
    <TableRow>
      <TableCell>
        <RelativeTime at={run.startedAt} />
      </TableCell>
      <TableCell>{formatDuration(run.startedAt, run.closedAt)}</TableCell>
      <TableCell>
        <Badge variant={STATUS_VARIANT[run.status] ?? "outline"}>{run.status}</Badge>
      </TableCell>
      <TableCell>{summary ? summary.patchesDiscovered : "—"}</TableCell>
      <TableCell>{summary ? summary.patchesAssessed : "—"}</TableCell>
      <TableCell>{summary ? summary.verdictsChanged : "—"}</TableCell>
      <TableCell>
        {summary ? (
          <span>
            {summary.notificationsSent}
            {summary.notificationsFailed > 0 && (
              <span className="ml-1 text-destructive">({summary.notificationsFailed} fallidas)</span>
            )}
          </span>
        ) : (
          "—"
        )}
      </TableCell>
      <TableCell>
        {summary && summary.errors.length > 0 ? (
          <details>
            <summary className="cursor-pointer text-destructive">
              {summary.errors.length} error{summary.errors.length > 1 ? "es" : ""}
            </summary>
            <ul className="mt-1 list-inside list-disc font-mono text-xs text-muted-foreground">
              {summary.errors.map((err, i) => (
                <li key={i}>{err}</li>
              ))}
            </ul>
          </details>
        ) : (
          <span className="text-muted-foreground">—</span>
        )}
      </TableCell>
    </TableRow>
  )
}

export default function Runs() {
  const runs = useRuns()

  return (
    <div className="flex min-h-svh flex-col">
      <TopBar />
      <main className="flex-1 p-4">
        {runs.isLoading && <p className="text-sm text-muted-foreground">Cargando corridas…</p>}

        {runs.isError && (
          <p className="text-sm text-destructive">
            No se pudieron cargar las corridas: {runs.error.message || "error desconocido"}
          </p>
        )}

        {runs.data && runs.data.runs.length === 0 && (
          <p className="text-sm text-muted-foreground">Todavía no hay corridas registradas.</p>
        )}

        {runs.data && runs.data.runs.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Inicio</TableHead>
                <TableHead>Duración</TableHead>
                <TableHead>Estado</TableHead>
                <TableHead>Descubiertos</TableHead>
                <TableHead>Evaluados</TableHead>
                <TableHead>Cambios de veredicto</TableHead>
                <TableHead>Notificaciones</TableHead>
                <TableHead>Errores</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {runs.data.runs.map((run) => (
                <RunRow key={run.runId} run={run} />
              ))}
            </TableBody>
          </Table>
        )}
      </main>
    </div>
  )
}
