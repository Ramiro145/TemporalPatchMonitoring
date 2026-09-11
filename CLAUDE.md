# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Idioma

Trabajar **siempre en español**: respuestas del chat, specs, mensajes de commit y documentación.
La única excepción es el encabezado obligatorio en inglés al inicio de este archivo.

**En el código la convención es inglés**: nombres de clases, métodos, variables, interfaces,
namespaces, nombres de proyectos, ramas de git, identificadores de workflows/activities/task queues
y comentarios dentro del código van en inglés. El español queda para la prosa: specs, README,
mensajes de commit y explicaciones.

## Qué es este proyecto

**PatchMonitor**: un monitor genérico del ciclo de vida de los **patches de Temporal**
(`Workflow.Patched` → `Workflow.DeprecatePatch` → código limpio). Se conecta a cualquier namespace,
descubre los `patchId` en juego, resuelve la fase de cada uno y cada 5 minutos (Temporal Schedule,
sin polling) evalúa si ya se puede saltar a la siguiente. Registra el veredicto en entity workflows
y notifica una vez por cada cambio real.

No edita código ni redespliega: el salto de fase es una acción humana; el monitor da el veredicto.

El manual de uso para personas está en `README.md`, y la guía introductoria visual en
<https://claude.ai/code/artifact/1b237336-261e-4cca-9807-589e748b7dbb> (si cambia algo que ella
describe — puertos, rutas, variables, límites — actualizarla también). La hoja de ruta, las
decisiones cerradas y el criterio de "listo" están en `Construction.md` — leer ahí antes de proponer
cambios de arquitectura.

## Estado actual

- Specs **01 a 09 implementados** (`specs/`). El spec 09 validó el monitor end-to-end contra el
  `ReleaseOrderDemo` real (evidencia en `docs/e2e/evidence/`).
- **Spec 10 (auto-versionado del `MonitorWorkflow`) diferido**, no descartado: la prioridad es
  probar el monitor contra un segundo proyecto real. Ver `Construction.md` §7 ítem 10 y §8.
- Límites conocidos para reusarlo en otros proyectos: `specs/09-multi-target-e2e-validation.md`,
  sección "Límites conocidos para reusar esto en otro proyecto".

## Arquitectura

Solución `PatchMonitor.sln`, todos los proyectos `net8.0`, `Temporalio` 1.9.0:

| Proyecto | Rol |
| -------- | --- |
| `src/Contracts` | Dominio puro (`PatchPhase`, `PatchKey`, `ExecutionSnapshot`, `PhaseVerdict`, gates), puertos (`IExecutionSource`, `IPatchDiscovery`, `IPhaseResolver`, `IPatchStateStore`, `INotifier`, `IDecisionSink`), `*Options` e interfaces de workflow. Sin I/O. |
| `src/Common` | Plumbing de Temporal portado del repo de referencia: `WorkerHost` (drenaje SIGTERM, 30 s), `WorkflowStarter`, `WorkflowValidator`, `ScheduleBootstrapper`, `ScheduleController`, `TemporalPatchStateStore`. |
| `src/PatchMonitor` | Worker. Workflows: `MonitorWorkflow` (pasada efímera por tick), `PatchStateWorkflow` (entity por patch, nunca cierra, `Continue-As-New`), `PatchRegistryWorkflow` (índice singleton), `HealthWorkflow`. Activities respaldadas por `Services/*`. Crea el Schedule al arrancar. |
| `src/MonitorApi` | API mínima de ASP.NET Core en `:5100`, Swagger siempre habilitado. |
| `test/PatchMonitor.Tests` | xUnit + entorno time-skipping de Temporalio. Sin Docker. |

Flujo de un tick: Schedule → `MonitorWorkflow` → `DiscoveryActivities` (dos niveles:
`TemporalChangeVersion` + Event History) → `PhaseActivities` (resolver fase + gate) →
`PatchStateActivities` (signal-with-start al entity) → `NotificationActivities` solo si la
`Revision` avanzó.

Dos clusters, nunca mezclar:
- `TEMPORAL_HOST` — cluster **propio**; ahí vive todo el estado del monitor.
- `TARGET_TEMPORAL_HOST` / `TARGET_TEMPORAL_NAMESPACE` — cluster **observado**; solo lectura.

## Convenciones de código

- Toda configuración por variable de entorno, a través de un `*Options.FromEnvironment()` en
  `Contracts`. Ausente, no numérica o no positiva ⇒ default; **nunca lanza**. Cada `*Options`
  tiene su `*OptionsTests` que guarda y restaura las variables del proceso.
- Activities registradas en DI por tipo concreto y pasadas a `WorkerHost` como lista de tipos.
- Los gates son funciones puras y **asimétricas**: 1→2 bloquean abiertas *sin* marker; 2→3
  bloquean abiertas *con* marker. `IsTruncated` fuerza `Inconclusive`, nunca un `Ready` falso.
- La fase se distingue por el flag `deprecated` del marker `core_patch`, no por su presencia.
- Estado en entity workflows, no en BD. Un workflow cuyos handlers leen estado inicializado desde
  los argumentos de arranque lo inicializa en un constructor `[WorkflowInit]`: un
  signal-with-start puede entregar el signal antes de que corra `[WorkflowRun]` (bug real
  encontrado en el spec 09).
- Cualquier cambio al código de `PatchStateWorkflow` o `PatchRegistryWorkflow` con ejecuciones
  vivas exige `Workflow.Patched` (nunca cierran). Es la razón de ser del spec 10.

## Build / test / run

```powershell
dotnet build PatchMonitor.sln
dotnet test  PatchMonitor.sln        # 263 tests, sin Docker

# stack del monitor, desde docker/
docker compose build
docker compose up -d
docker compose logs --tail=100 patch-monitor-worker

# apuntado a otro cluster (overlay de ejemplo contra ReleaseOrderDemo)
docker compose -f docker-compose.yml -f docker-compose.e2e.yml up -d
```

Puertos del host: Temporal `7234`, UI `8234`, Postgres `5433`, API `5100` (corridos para convivir
con un proyecto en 7233/8233/5432). `EnsureScheduleAsync` es create-if-absent: si cambiás la
configuración del Schedule, hacé `docker compose down -v` para que se recree.

Los tests de time-skipping descargan el test-server de Temporal la primera vez (queda cacheado).
Hay un flaky conocido en `TemporalPatchStateStoreTests` bajo carga paralela; si falla, repetir
aislado antes de investigar.

## Proyecto de referencia

`ReleaseOrderDemo` (<https://github.com/Ramiro145/ReleaseOrderDemo>; copia local en
`..\releaseorder-signal\releaseorder_combined`). Es de donde salen las versiones del stack, el
`Common/` portado y el target real del spec 09. No es código nuestro: no commitear cambios ahí.
Ese repo ya completó su propio ciclo de vida del patch `audit-before-decision`; reintroducirlo
exige la adaptación documentada en `docs/e2e/releaseorder-patch-phases.md`.

## Metodología: Spec-Driven Design

- `/spec <feature>` — aclara requisitos y escribe `specs/NN-slug.md`. Sin código en esa fase.
  Leer las dos specs más recientes antes de escribir una nueva.
- `/spec-impl <NN>` — solo con estado **Aprobado**; crea la rama `spec-NN-slug` e implementa paso a
  paso con pausas para revisar diffs. Nunca commitear sin que el usuario lo pida.
- `specs/.spec-config.yml` (`AutoCreateBranch`) controla la creación automática de la rama.
- Seguir las prácticas de <https://github.com/Klerith/fernando-skills>.
