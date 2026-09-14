import { TriangleAlert } from "lucide-react"
import { useNavigate, Link } from "react-router"
import { usePatches, useHealth } from "@/api/hooks"
import type { PatchSummary } from "@/api/types"
import { OutcomeBadge } from "@/components/OutcomeBadge"
import { PhaseBadge } from "@/components/PhaseBadge"
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

// Ready primero (accionables), después Blocked, después Inconclusive; sin veredicto al final.
function outcomeRank(outcome: number | null): number {
  if (outcome === 2) return 0
  if (outcome === 1) return 1
  if (outcome === 0) return 2
  return 3
}

function patchDetailPath(ns: string, type: string, patchId: string): string {
  return `/patches/${encodeURIComponent(ns)}/${encodeURIComponent(type)}/${encodeURIComponent(patchId)}`
}

// PatchSummaryResponse.Unreadable (PatchEndpoints.cs) usa esta firma exacta: la key está en el
// registry pero su entity no responde. Se distingue de la fase Unknown genuina.
function isUnreadable(patch: PatchSummary): boolean {
  return patch.phase === 0 && patch.revision === 0
}

export default function Dashboard() {
  const health = useHealth()
  const patches = usePatches()
  const navigate = useNavigate()

  const sorted = patches.data
    ? [...patches.data.patches].sort((a, b) => outcomeRank(a.outcome) - outcomeRank(b.outcome))
    : []

  return (
    <div className="flex min-h-svh flex-col">
      <TopBar />
      <main className="flex-1 p-4">
        {health.data?.temporal === "unreachable" && (
          <div className="mb-4 flex items-center gap-2 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
            <TriangleAlert className="size-4 shrink-0" />
            El cluster observado ({health.data.targetNamespace}) no responde. Los datos mostrados
            pueden estar desactualizados.
          </div>
        )}

        {patches.data?.truncated && (
          <div className="mb-4 flex items-center gap-2 rounded-md border border-border bg-muted px-3 py-2 text-sm text-muted-foreground">
            <TriangleAlert className="size-4 shrink-0" />
            Se alcanzó el tope de patches listados ({patches.data.count}); hay más en el namespace
            observado.
          </div>
        )}

        {patches.isLoading && <p className="text-sm text-muted-foreground">Cargando patches…</p>}

        {patches.isError && (
          <p className="text-sm text-destructive">
            No se pudieron cargar los patches: {patches.error.message || "error desconocido"}
          </p>
        )}

        {patches.data && patches.data.count === 0 && (
          <p className="text-sm text-muted-foreground">
            No se descubrió ningún patch en el namespace observado todavía.
          </p>
        )}

        {patches.data && patches.data.count > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Patch</TableHead>
                <TableHead>Workflow</TableHead>
                <TableHead>Namespace</TableHead>
                <TableHead>Fase</TableHead>
                <TableHead>Gate</TableHead>
                <TableHead>Observado</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {sorted.map((patch) => {
                const path = patchDetailPath(patch.namespace, patch.workflowType, patch.patchId)
                const unreadable = isUnreadable(patch)

                return (
                  <TableRow
                    key={path}
                    className="cursor-pointer"
                    onClick={() => navigate(path)}
                  >
                    <TableCell className="font-medium">
                      <Link to={path} className="hover:underline" onClick={(e) => e.stopPropagation()}>
                        {patch.patchId}
                      </Link>
                    </TableCell>
                    <TableCell>{patch.workflowType}</TableCell>
                    <TableCell>{patch.namespace}</TableCell>
                    <TableCell>
                      {unreadable ? (
                        <Badge variant="destructive">Ilegible</Badge>
                      ) : (
                        <PhaseBadge phase={patch.phase} />
                      )}
                    </TableCell>
                    <TableCell>
                      <div className="flex items-center gap-1.5">
                        <OutcomeBadge outcome={patch.outcome} />
                        {patch.blockingExecutionCount > 0 && (
                          <span className="text-xs text-muted-foreground">
                            {patch.blockingExecutionCount}
                          </span>
                        )}
                      </div>
                    </TableCell>
                    <TableCell>
                      <RelativeTime at={patch.lastObservedAt} />
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        )}
      </main>
    </div>
  )
}
