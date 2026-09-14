import { Link, useParams } from "react-router"
import { usePatch } from "@/api/hooks"
import { ApiError } from "@/api/client"
import type { PhaseVerdict } from "@/api/types"
import { OutcomeBadge } from "@/components/OutcomeBadge"
import { PhaseBadge } from "@/components/PhaseBadge"
import { PhaseTrack } from "@/components/PhaseTrack"
import { RelativeTime } from "@/components/RelativeTime"
import { TopBar } from "@/components/TopBar"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"

function VerdictCard({ title, verdict }: { title: string; verdict: PhaseVerdict | null }) {
  if (!verdict) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>{title}</CardTitle>
        </CardHeader>
        <CardContent className="text-sm text-muted-foreground">Sin veredicto todavía.</CardContent>
      </Card>
    )
  }

  const hasMore = verdict.blockingExecutionCount > verdict.blockingSample.length

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between gap-2">
          {title}
          <OutcomeBadge outcome={verdict.outcome} />
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        <p>{verdict.reason}</p>
        {verdict.blockingSample.length > 0 && (
          <div>
            <p className="text-xs font-medium text-muted-foreground">
              Ejecuciones bloqueantes
              {hasMore
                ? ` (muestra de ${verdict.blockingSample.length} de ${verdict.blockingExecutionCount})`
                : ` (${verdict.blockingExecutionCount})`}
            </p>
            <ul className="mt-1 list-inside list-disc font-mono text-xs">
              {verdict.blockingSample.map((id) => (
                <li key={id}>{id}</li>
              ))}
            </ul>
          </div>
        )}
        <p className="text-xs text-muted-foreground">
          Evaluado <RelativeTime at={verdict.evaluatedAt} />
        </p>
      </CardContent>
    </Card>
  )
}

function BackToDashboard() {
  return (
    <Link to="/" className="text-sm text-muted-foreground hover:text-foreground hover:underline">
      ← Volver al dashboard
    </Link>
  )
}

export default function PatchDetail() {
  const { ns = "", type = "", patchId = "" } = useParams()
  const patch = usePatch(ns, type, patchId)

  if (patch.isLoading) {
    return (
      <div className="flex min-h-svh flex-col">
        <TopBar />
        <main className="flex-1 p-4">
          <p className="text-sm text-muted-foreground">Cargando patch…</p>
        </main>
      </div>
    )
  }

  if (patch.error instanceof ApiError && patch.error.status === 404) {
    return (
      <div className="flex min-h-svh flex-col">
        <TopBar />
        <main className="flex-1 space-y-3 p-4">
          <p className="text-sm text-destructive">Patch no encontrado.</p>
          <BackToDashboard />
        </main>
      </div>
    )
  }

  if (patch.isError || !patch.data) {
    return (
      <div className="flex min-h-svh flex-col">
        <TopBar />
        <main className="flex-1 space-y-3 p-4">
          <p className="text-sm text-destructive">
            No se pudo cargar el patch{patch.error ? `: ${patch.error.message}` : "."}
          </p>
          <BackToDashboard />
        </main>
      </div>
    )
  }

  const detail = patch.data

  return (
    <div className="flex min-h-svh flex-col">
      <TopBar />
      <main className="flex-1 space-y-4 p-4">
        <div>
          <BackToDashboard />
          <h1 className="mt-1 text-lg font-semibold">{detail.summary.patchId}</h1>
          <p className="text-sm text-muted-foreground">
            {detail.summary.workflowType} · {detail.summary.namespace}
          </p>
        </div>

        <Card>
          <CardHeader>
            <CardTitle>Fase actual</CardTitle>
          </CardHeader>
          <CardContent className="space-y-2">
            <PhaseTrack
              currentPhase={detail.summary.phase}
              nextPhase={detail.summary.nextPhase}
              outcome={detail.summary.outcome}
            />
            <p className="text-sm text-muted-foreground">{detail.phaseReason}</p>
          </CardContent>
        </Card>

        {detail.override && (
          <Card>
            <CardHeader>
              <CardTitle>Override activo</CardTitle>
            </CardHeader>
            <CardContent className="flex flex-wrap items-center gap-1 text-sm text-muted-foreground">
              Fase forzada a <PhaseBadge phase={detail.override.phase} />
              por <span className="font-medium text-foreground">{detail.override.declaredBy}</span>,
              declarado <RelativeTime at={detail.override.declaredAt} />
              {detail.override.expiresAt && (
                <>
                  · vence <RelativeTime at={detail.override.expiresAt} />
                </>
              )}
            </CardContent>
          </Card>
        )}

        <div className="grid gap-4 md:grid-cols-2">
          <VerdictCard title="Veredicto actual" verdict={detail.lastVerdict} />
          <VerdictCard title="Veredicto anterior" verdict={detail.previousVerdict} />
        </div>

        <Card>
          <CardHeader>
            <CardTitle>Historial</CardTitle>
          </CardHeader>
          <CardContent>
            {detail.history.length === 0 ? (
              <p className="text-sm text-muted-foreground">Sin transiciones registradas todavía.</p>
            ) : (
              <ul className="space-y-2">
                {[...detail.history].reverse().map((change, i) => (
                  <li
                    key={`${change.at}-${i}`}
                    className="flex items-start justify-between gap-4 border-b pb-2 text-sm last:border-0 last:pb-0"
                  >
                    <div>
                      <div className="flex items-center gap-1.5">
                        <PhaseBadge phase={change.fromPhase} />
                        <span className="text-muted-foreground">→</span>
                        <PhaseBadge phase={change.toPhase} />
                      </div>
                      <p className="mt-1 text-muted-foreground">{change.reason}</p>
                    </div>
                    <span className="shrink-0 text-xs text-muted-foreground">
                      <RelativeTime at={change.at} />
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </main>
    </div>
  )
}
