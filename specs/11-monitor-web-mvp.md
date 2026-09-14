# 11 - Front de observabilidad

**Estado:** Aprobado
**Depende de:** [09-multi-target-e2e-validation.md](09-multi-target-e2e-validation.md)
**Fecha:** 2026-09-14

**Objetivo:** Un dashboard web (`web/`, React + Vite) que consuma `MonitorApi` para mostrar de un
vistazo qué patches hay, en qué fase está cada uno y qué lo bloquea, y permita disparar el Schedule y
pausarlo/reanudarlo, sin depender de Swagger ni de `Invoke-RestMethod`.

## Por qué existe este spec

`Construction.md` §3 declaraba "sin frontend" como decisión cerrada, y `specs/08-control-api.md` lo
repetía dos veces: Swagger UI era "la única interfaz de exploración". Esa decisión era correcta
cuando se tomó — el objetivo central del proyecto todavía no estaba demostrado y cualquier esfuerzo
en UI era prematuro. El spec 09 cambió esa premisa: probó con evidencia real
(`docs/e2e/evidence/*.json`) que el monitor apuntado a `ReleaseOrderDemo` reproduce el recorrido
completo de las 3 fases sin tocar una línea del proyecto observado. El objetivo del README está
cumplido.

Lo que queda como cuello de botella no es capacidad, es superficie. `Construction.md` §3 fue
actualizado el 2026-09-14 para reflejar esto: el frontend pasa de "fuera de alcance" a entregable,
justo antes de este spec.

`GET /patches` y `GET /patches/{ns}/{type}/{patchId}` (spec 08) ya devuelven todo lo que un dashboard
necesita — fase, gate, motivo, bloqueantes, historial de cambios — pero como JSON con enums
numéricos, pensado para máquina. Un desarrollador de otro equipo, dueño de un proyecto que use
patches de Temporal, no debería tener que leer `src/Contracts/Domain/PatchPhase.cs` para entender
qué significa `"phase": 2`. Ese es el problema que resuelve este spec: la misma información que ya
existe, legible por una persona en segundos.

`specs/09-...md`, sección "Límites conocidos para reusar esto en otro proyecto", deja pendiente
probar el monitor contra un segundo proyecto real. Ese trabajo sigue siendo el próximo hito de fondo
del proyecto; este spec no lo reemplaza, lo acompaña — un dashboard hace esa segunda validación más
fácil de conducir y de mostrar.

## Alcance

**Incluye:**

- **`web/`** — proyecto Vite + React 19 + TypeScript, scaffoldeado con
  `npm create vite@latest web -- --template react-ts`, dentro de este mismo repo. Node 24 (LTS del
  host), sin versión mínima fijada en `package.json` más allá de lo que Vite exige.

- **`web/src/api/types.ts`** — tipos TypeScript espejo de los DTOs reales de `MonitorApi`
  (`PatchSummary`, `PatchDetail`, `PhaseVerdict`, `PatchStateChange`, `PhaseOverride`,
  `ScheduleStatus`, `MonitorRun`, `Health`), en camelCase igual que la serialización real de
  `System.Text.Json`.

- **`web/src/api/enums.ts`** — el único lugar del front donde viven los mapeos numéricos de
  `src/Contracts/Domain/PatchPhase.cs`, `GateOutcome.cs` y `src/Contracts/Phase/PhaseResolution.cs`:

  ```ts
  export const PHASE_LABEL: Record<number, string> = {
    0: "Desconocida", 1: "Convivencia", 2: "Deprecación", 3: "Código limpio",
  };
  export const OUTCOME_LABEL: Record<number, string> = {
    0: "Sin datos", 1: "Bloqueado", 2: "Listo",
  };
  ```

  Ningún componente de UI compara contra `0`/`1`/`2`/`3` directamente fuera de este archivo.

- **`web/src/api/client.ts`** — wrapper de `fetch` contra el prefijo `/api`, que:
  - encodea `ns`/`type`/`patchId` con `encodeURIComponent` antes de armar la ruta;
  - lee el body de `400`/`409` como `.text()` (la API los devuelve en texto plano, no JSON —
    verificado en `PatchEndpoints.cs`), y el de las respuestas `2xx` como `.json()`;
  - trata el `503` de `/schedule*` como estado "Schedule no disponible", no como excepción genérica.

- **`web/src/api/hooks.ts`** — hooks de TanStack Query: `useHealth`, `useSchedule`, `usePatches`,
  `usePatch(ns, type, patchId)`, `useRuns`, y las mutaciones `useTriggerSchedule`,
  `usePauseSchedule`, `useUnpauseSchedule` (invalidan `useSchedule` al resolver).
  `refetchInterval`: 15 s para `/health` y `/schedule`, 30 s para `/patches` y `/runs` — más lento
  que el resto porque `GET /patches` hace una query por patch contra Temporal (N+1, ver
  `PatchEndpoints.ListAsync`).

- **Componentes de estado**, reutilizados en todas las vistas: `PhaseBadge`, `OutcomeBadge`,
  `PhaseTrack` (el recorrido 1→2→3 con el punto actual resaltado y una flecha hacia `nextPhase`
  cuando el outcome es `Ready`), `RelativeTime` (formatea `DateTimeOffset` ISO), `StatusDot` (verde/
  rojo/gris para `health.temporal`).

- **`/` — Dashboard** (`web/src/routes/Dashboard.tsx`). Barra superior con:
  - salud del cluster (`GET /health`);
  - estado del Schedule (`GET /schedule`): pausado o no, intervalo, última y próxima corrida;
  - botones *Disparar ahora* (`POST /schedule/trigger`), *Pausar*/*Reanudar*
    (`POST /schedule/pause|unpause`), deshabilitados mientras la mutación está en curso.

  Debajo, la tabla de `GET /patches`, ordenada con los `Ready` primero (accionables), luego
  `Blocked`, luego `Inconclusive`. Columnas: `patchId`, `workflowType`, `namespace`, fase (badge),
  gate (badge + `blockingExecutionCount`), `lastObservedAt` relativo. Fila clickeable → detalle.
  Estados vacíos diferenciados: sin patches descubiertos / `/health` no reachable / lista truncada
  (`truncated: true`) con aviso de que hay más de `API_MAX_LIST_PATCHES`.

- **`/patches/:ns/:type/:patchId` — Detalle** (`web/src/routes/PatchDetail.tsx`), vía
  `GET /patches/{ns}/{type}/{patchId}`:
  - fase actual y `phaseReason`;
  - `lastVerdict`: outcome, `reason`, `blockingSample` (hasta 5 workflowIds, con
    `blockingExecutionCount` si hay más);
  - `previousVerdict` si existe;
  - `override` activo si existe (`declaredBy`, `declaredAt`, `expiresAt`);
  - timeline de `history[]`: cada entrada como `fromPhase → toPhase` con su `reason` y `at`.
  - `404` → mensaje de "patch no encontrado", con link de vuelta al dashboard.

- **`/runs` — Corridas** (`web/src/routes/Runs.tsx`), vía `GET /runs` (nuevo, ver más abajo): tabla
  de los últimos `MonitorRunSummary` con inicio, duración, descubiertos, evaluados, cambios de
  veredicto, notificaciones enviadas/fallidas y errores (si `errors[]` no está vacío, se muestra
  expandible).

- **`web/vite.config.ts`** — `server.proxy`: `/api` → `http://localhost:5100`, con `rewrite` que
  quita el prefijo `/api`. El front llama siempre a rutas relativas `/api/...`; nunca hardcodea
  `localhost:5100`, para que el mismo build sirva en dev y detrás de nginx.

- **`src/MonitorApi/Program.cs`** _(modificado)_ — CORS mínimo:

  ```csharp
  var corsOrigins = (Environment.GetEnvironmentVariable("API_CORS_ORIGINS") ?? "http://localhost:5173")
      .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
  builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));
  // ...
  app.UseCors();
  ```

  Sigue la convención de `CLAUDE.md`: ausente o vacía ⇒ default, nunca lanza. En el despliegue real
  detrás de nginx (mismo origen) esta política no llega a activarse; solo importa para `npm run dev`
  hablando directo con `:5100` sin el proxy de Vite, y como red de seguridad.

- **`GET /runs`** _(nuevo endpoint)_ — expone `MonitorRunSummary`, hoy solo visible en la Event
  History de `MonitorWorkflow` (Temporal UI en `:8234`) o vía SDK. Sin tocar ningún workflow:

  - **`src/Contracts/Monitor/IMonitorRunReader.cs`** _(nuevo)_ — puerto, mismo estilo que
    `IScheduleController` (`src/Contracts/Monitor/IScheduleController.cs`):

    ```csharp
    public interface IMonitorRunReader
    {
        Task<IReadOnlyList<MonitorRunView>> ListRecentAsync(int limit, CancellationToken ct = default);
    }

    public sealed record MonitorRunView(
        string WorkflowId, string RunId, DateTimeOffset StartedAt, DateTimeOffset? ClosedAt,
        string Status, MonitorRunSummary? Summary);
    ```

    `Summary` es `null` cuando la Event History ya fue purgada por retención o la corrida sigue
    `Running` — nunca se lanza una excepción por esto.

  - **`src/Common/Temporal/TemporalMonitorRunReader.cs`** _(nuevo)_ — implementación, mismo patrón
    que `TemporalScheduleController` (`src/Common/Temporal/ScheduleController.cs`): usa
    `client.ListWorkflowsAsync` filtrando `WorkflowType = 'MonitorWorkflow'`, ordenado por
    `StartTime` descendente, tope `limit`; para las cerradas exitosas intenta `GetResultAsync<MonitorRunSummary>()`
    y traga cualquier error de deserialización o historia purgada devolviendo `Summary: null`.

  - **`src/MonitorApi/Endpoints/RunEndpoints.cs`** _(nuevo)_ — handler `static` con `TypedResults`,
    mismo estilo que `PatchEndpoints`/`ScheduleEndpoints`:

    ```csharp
    public static async Task<Ok<RunListResponse>> ListAsync(
        IMonitorRunReader reader, ApiOptions options, CancellationToken ct = default)
    {
        var runs = await reader.ListRecentAsync(options.MaxListRuns, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunListResponse(runs));
    }
    ```

    Registrado en `Program.cs`: `app.MapGet("/runs", RunEndpoints.ListAsync).WithTags("Runs");`.

  - **`src/Contracts/Api/ApiOptions.cs`** _(modificado)_ — nuevo campo `int MaxListRuns`, leído de
    `API_MAX_LIST_RUNS` (default `20`), mismo patrón `PositiveIntOrDefault` que el resto del record.

  - **`test/PatchMonitor.Tests/Api/ApiOptionsTests.cs`** _(modificado)_ — casos para
    `API_MAX_LIST_RUNS` ausente / no numérica / no positiva ⇒ default 20, guardando y restaurando la
    variable como ya hace la clase con las otras dos.

- **`docker/Dockerfile.MonitorWeb`** _(nuevo)_ — multi-stage: `node:24-alpine` (`npm ci && npm run
  build`), `nginx:alpine` sirviendo `web/dist`.

- **`docker/nginx.conf`** _(nuevo)_ — `try_files $uri /index.html` para las rutas de React Router, y
  `location /api/ { proxy_pass http://monitor-api:5100/; }` — mismo origen que el front, por lo que
  CORS no entra en juego en este camino.

- **`docker/docker-compose.yml`** _(modificado)_ — servicio `monitor-web`, puerto `5101:80`
  (siguiendo el corrimiento deliberado de puertos del proyecto: 7234/8234/5433/5100), `depends_on:
  monitor-api`.

- **`.gitignore`** _(modificado)_ — agrega `node_modules/`, `web/dist/`, `.vite/`, `*.log`,
  `.env.local`.

- **`.dockerignore`** _(modificado)_ — agrega `node_modules/` y `web/dist/`. Crítico:
  `Dockerfile.MonitorApi` y `Dockerfile.PatchMonitor` usan `context: ..` (la raíz del repo entero)
  — sin esto, `node_modules` se copiaría al contexto de build de las imágenes .NET.

- **`README.md`** y **`CLAUDE.md`** _(modificados)_ — tabla de puertos (agrega `5101`), sección de
  build/run del front. La guía visual del artifact
  (`https://claude.ai/code/artifact/1b237336-261e-4cca-9807-589e748b7dbb`) se actualiza si describe
  puertos o límites que este spec cambia.

**No incluye (fuera de alcance de este spec):**

- **Gestión de overrides desde la UI** (crear/borrar con TTL vía `POST`/`DELETE
  /patches/.../override`). La API ya la expone; se opera por Swagger, igual que hoy. Es la superficie
  de escritura más delicada (puede forzar un salto de fase ilegal con `force=true`) y no aporta a la
  observabilidad, que es el objetivo de este spec.
- **Push en vivo** (SSE/WebSocket/webhook entrante). El refresco es por polling de TanStack Query;
  ningún endpoint nuevo de streaming.
- **Navegación de ejecuciones bloqueantes más allá de `blockingSample`** (las hasta 5 que ya trae la
  API). Listar las `N` completas exigiría un endpoint nuevo sobre `ExecutionSnapshot`, que hoy ni
  siquiera se persiste (ver informe de dominio: viven solo durante la pasada del monitor).
- **Autenticación o autorización.** `MonitorApi` no tiene ninguna hoy; este spec no la agrega. El
  dashboard queda tan abierto como la API que consume.
- **Suite de tests de frontend** (Vitest, Testing Library, Playwright). La verificación de este spec
  es `npm run build` (incluye `tsc --noEmit`) más el recorrido end-to-end manual del último paso,
  igual que hace el spec 09 con PowerShell.
- **Multi-cluster o multi-namespace en una sola vista.** El dashboard apunta al mismo
  `TARGET_TEMPORAL_HOST`/`TARGET_TEMPORAL_NAMESPACE` que ya configura `monitor-api`; no hay selector
  de cluster.
- **El spec 10 (`self-versioning-and-drain`).** Sigue diferido, sin relación con este spec más allá
  de compartir la numeración de la secuencia.

## Modelo de datos

Los tipos de dominio (`PatchPhase`, `GateOutcome`, `PhaseVerdict`, `PatchState`, etc.) no cambian:
este spec los consume por HTTP, no los toca en `Contracts`. Lo nuevo son tres cosas.

### 1. `MonitorRunView` / `RunListResponse` (backend, `src/Contracts/Monitor/`)

```csharp
public sealed record MonitorRunView(
    string WorkflowId,
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    string Status,              // "Running" | "Completed" | "Failed" | "Terminated" | ...
    MonitorRunSummary? Summary  // null si la historia fue purgada o la corrida sigue abierta
);

public sealed record RunListResponse(IReadOnlyList<MonitorRunView> Runs);
```

Serializa en camelCase, igual que el resto de la API. `MonitorRunSummary` ya existe
(`src/Contracts/Monitor/MonitorRunSummary.cs`) y no cambia de forma.

### 2. `ApiOptions` — campo nuevo

```csharp
public sealed record ApiOptions(int MaxListPatches, TimeSpan OverrideDefaultTtl, int MaxListRuns)
```

| Variable | Default | Significado |
|----------|---------|--------------|
| `API_MAX_LIST_RUNS` | 20 | Tope de corridas devueltas por `GET /runs` |

### 3. Tipos TypeScript (`web/src/api/types.ts`) — espejo de los DTOs de `Contracts/Api`

```ts
export interface PatchSummary {
  namespace: string; workflowType: string; patchId: string;
  phase: number; source: number; outcome: number | null; nextPhase: number | null;
  blockingExecutionCount: number; hasOverride: boolean; revision: number;
  lastObservedAt: string | null; lastChangedAt: string | null;
}

export interface PhaseVerdict {
  outcome: number; currentPhase: number; nextPhase: number | null;
  blockingExecutionCount: number; blockingSample: string[]; reason: string;
  evaluatedAt: string;
}

export interface PatchStateChange {
  at: string; fromPhase: number; toPhase: number;
  fromOutcome: number | null; toOutcome: number | null; reason: string;
}

export interface PhaseOverride {
  key: { namespace: string; workflowType: string; patchId: string };
  phase: number; declaredBy: string; declaredAt: string; expiresAt: string | null;
}

export interface PatchDetail {
  summary: PatchSummary; phaseReason: string;
  lastVerdict: PhaseVerdict | null; previousVerdict: PhaseVerdict | null;
  override: PhaseOverride | null; assessmentCount: number; notifiedRevision: number;
  history: PatchStateChange[];
}

export interface ScheduleStatus {
  scheduleId: string; paused: boolean; note: string | null; interval: string; // "hh:mm:ss"
  lastRunAt: string | null; nextRunAt: string | null; runningCount: number; numActions: number;
}

export interface MonitorRun {
  workflowId: string; runId: string; startedAt: string; closedAt: string | null;
  status: string; summary: MonitorRunSummary | null;
}

export interface MonitorRunSummary {
  startedAt: string; finishedAt: string;
  patchesDiscovered: number; patchesAssessed: number; verdictsChanged: number;
  overridesLoaded: number; errors: string[];
  notificationsSent: number; notificationsFailed: number;
}
```

Sin base de datos ni esquema nuevo: todo el estado sigue viviendo en los entity workflows del spec 05.

## Plan de implementación

1. **Reabrir la decisión "sin frontend".** `Construction.md` §3 (fila "Entregables extra"), §7 (fila
   11 + párrafo "Por qué van en ese orden"), §8 (checklist). Sin código todavía. *(Hecho como parte
   de este mismo spec, antes de aprobarlo — ver commit de este archivo.)*

2. **CORS mínimo en `MonitorApi`.** `AddCors`/`UseCors` en `Program.cs` con `API_CORS_ORIGINS`
   (default `http://localhost:5173`). Test manual: `curl -H "Origin: http://localhost:5173" -I
   http://localhost:5100/health` debe traer `Access-Control-Allow-Origin`.

3. **`GET /runs`.** `IMonitorRunReader` en `Contracts`, `TemporalMonitorRunReader` en `Common`
   (reusando el patrón de `TemporalScheduleController`), `RunEndpoints` en `MonitorApi`, campo
   `MaxListRuns` en `ApiOptions` + su caso en `ApiOptionsTests`. `dotnet build` y `dotnet test`
   deben seguir en verde (263 + los nuevos) sin Docker.

4. **Scaffolding de `web/`.** `npm create vite@latest web -- --template react-ts`; instalar
   `react-router`, `@tanstack/react-query`, `lucide-react`, `tailwindcss` + `@tailwindcss/vite`;
   configurar Tailwind v4 y el proxy de `vite.config.ts`. Página en blanco que compila y sirve en
   `:5173`.

5. **shadcn/ui mínimo.** `npx shadcn@latest init`, agregar solo `badge`, `button`, `card`, `table`,
   `tooltip`. Los componentes quedan copiados en `web/src/components/ui/`, editables, sin quedar
   atados a una versión de la librería.

6. **Capa de datos.** `web/src/api/types.ts`, `enums.ts`, `client.ts`, `hooks.ts`. Verificable de
   forma aislada: con el stack de Docker arriba, cada hook debe traer datos reales o el estado de
   error correspondiente (probar con el worker apagado para confirmar que `/patches` no rompe la UI).

7. **Componentes de estado + Dashboard.** `PhaseBadge`, `OutcomeBadge`, `PhaseTrack`, `RelativeTime`,
   `StatusDot`, y la ruta `/` completa: barra de salud/Schedule/acciones + tabla de patches.

8. **Detalle de patch.** Ruta `/patches/:ns/:type/:patchId`: veredicto actual/anterior, override,
   timeline de `history[]`. Enlazar desde las filas del Dashboard.

9. **Corridas.** `GET /runs` consumido en la ruta `/runs`, enlazada desde la barra superior.

10. **Empaquetado y verificación end-to-end manual.** `docker/Dockerfile.MonitorWeb`,
    `docker/nginx.conf`, servicio `monitor-web` en `docker-compose.yml`, `.gitignore`/`.dockerignore`
    ampliados, `README.md`/`CLAUDE.md` actualizados. Luego, contra el stack completo (ver
    `## Criterios de aceptación` para el detalle exacto): `docker compose up -d`, abrir
    `http://localhost:5101`, disparar el Schedule, pausar/reanudar, entrar al detalle de un patch y
    contrastar su timeline contra `docs/e2e/evidence/20260911160916-05-phase3-clean.json`.

## Criterios de aceptación

- [ ] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias tras agregar
      `IMonitorRunReader`/`TemporalMonitorRunReader`/`RunEndpoints`/`ApiOptions.MaxListRuns`.
- [ ] `dotnet test PatchMonitor.sln` pasa con el stack de Docker apagado, incluidos los casos nuevos
      de `ApiOptionsTests` para `API_MAX_LIST_RUNS`.
- [ ] `curl http://localhost:5100/runs` devuelve `RunListResponse` con al menos una corrida tras un
      `POST /schedule/trigger`, sin tocar ningún workflow existente.
- [ ] `cd web && npm run build` compila sin errores de TypeScript (`tsc --noEmit` incluido en el
      build de Vite).
- [ ] Con `docker compose up -d` (6 servicios sanos, incluido `monitor-web`), `http://localhost:5101`
      muestra el patch real del `ReleaseOrderDemo` (overlay `docker-compose.e2e.yml`) con su fase y
      gate correctos.
- [ ] *Disparar ahora* dispara una corrida visible en `/runs`; *Pausar*/*Reanudar* cambian
      `scheduleStatus.paused` y la UI lo refleja sin recargar la página.
- [ ] El detalle de un patch muestra una timeline consistente con
      `docs/e2e/evidence/20260911160916-05-phase3-clean.json` al reproducir el mismo recorrido
      (`seed-orders.ps1` → `drain-orders.ps1`).
- [ ] Con `patch-monitor-worker` detenido, el Dashboard sigue cargando (salud "unreachable" o
      patches en error legible), sin pantalla en blanco ni error sin manejar.
- [ ] `npm run dev` en `:5173` contra el `monitor-api` del compose (proxy de Vite) muestra el mismo
      contenido que `http://localhost:5101` (nginx) — confirma que ambos caminos son intercambiables.
- [ ] `node_modules/` y `web/dist/` no aparecen en `git status` tras un build local, ni infla el
      contexto de build de `docker compose build patch-monitor-worker`.

## Decisiones tomadas y descartadas

- **Sí:** reabrir `Construction.md` §3 explícitamente en vez de dejar que esta spec contradijera la
  decisión cerrada en silencio. Es lo que exige §6: "estas decisiones no se re-discuten en los specs;
  se citan" — citarlas significa también poder actualizarlas con fecha y motivo cuando cambian.
- **Sí:** numerar esta spec como **11**, dejando el **10** reservado para `self-versioning-and-drain`.
  Ese spec sigue diferido pero no descartado (`Construction.md` §7); reusar su número lo hubiera
  enterrado de hecho.
- **Sí:** `web/` dentro de este mismo repo, no un repo separado. El contrato API↔UI cambia en el
  mismo commit que la API, y `docker compose up` sigue siendo el único comando para levantar todo.
- **Sí:** Tailwind + shadcn/ui en vez de una librería de componentes monolítica (Mantine, MUI). Los
  componentes de shadcn se copian al repo (`web/src/components/ui/`), no quedan como dependencia
  versionada — menos superficie de upgrade forzado, más control del look para un panel denso.
- **Sí:** proxy (`/api` en Vite dev, `location /api/` en nginx) en vez de configurar CORS como
  solución principal. CORS se agrega igual, mínimo, como red de seguridad para quien corra
  `npm run dev` sin el proxy — pero el camino recomendado nunca depende de él.
- **Sí:** `GET /runs` nuevo, en vez de dejar el historial de corridas fuera del MVP. Es el hueco de
  observabilidad más notable que encontró la exploración (`MonitorRunSummary` solo vivía en la Event
  History) y agregarlo cuesta un endpoint de solo lectura, sin tocar ningún workflow.
- **No:** exponer `ExecutionSnapshot` individuales. No se persisten (viven solo durante la pasada del
  monitor); traerlos de vuelta exigiría reconsultar Temporal fuera del ciclo de evaluación, con el
  mismo costo N+1 que ya tiene `GET /patches` pero peor.
- **No:** overrides desde la UI en este MVP. Es la superficie de escritura más peligrosa (permite
  forzar transiciones ilegales con `force=true`) y no aporta a observar; se mantiene en Swagger hasta
  que haya una razón concreta de agregarla.
- **No:** autenticación. La API no la tiene; agregarla en el front sin agregarla en el backend sería
  seguridad de fachada.

## Riesgos identificados

| Riesgo | Mitigación |
| ------ | ---------- |
| `GET /patches` es N+1 contra Temporal; el dashboard puede sentirse lento con muchos patches | `refetchInterval` moderado (30 s) en vez de polling agresivo; `API_MAX_LIST_PATCHES` ya acota el tope desde el backend |
| `node_modules/` en el contexto de build de `Dockerfile.MonitorApi`/`Dockerfile.PatchMonitor` (usan `context: ..`) infla esas imágenes .NET si no se ignora | `.dockerignore` ampliado con `node_modules/` y `web/dist/` en el paso 10, verificado antes de cerrar el spec |
| Los enums numéricos cambian de significado si alguien reordena el enum en `Contracts` sin avisar al front | `web/src/api/enums.ts` como único punto de mapeo; el spec deja registrado el criterio de tabla espejo, no de inferencia |
| `PatchSummaryResponse.Unreadable` descarta el motivo del error (`phase: 0, revision: 0` sin explicación) | El front la distingue por esa firma exacta (`phase === 0 && revision === 0`) y la marca "ilegible" en vez de mostrarla como `Unknown` genérica |
| CORS mal configurado bloquea silenciosamente el dev server para quien no usa el proxy de Vite | `API_CORS_ORIGINS` documentado en `README.md`; el camino recomendado (proxy) no depende de CORS en absoluto |

## Qué NO entra en este spec

- Gestión de overrides desde la UI (crear/borrar con TTL).
- Push en vivo (SSE/WebSocket).
- Navegación completa de ejecuciones bloqueantes más allá de `blockingSample`.
- Autenticación o autorización de la API o del dashboard.
- Suite de tests automatizados de frontend (Vitest/Playwright).
- Selector de multi-cluster o multi-namespace.
- El spec 10 (`self-versioning-and-drain`), que sigue diferido sin relación con este trabajo.

Cada uno, si entra, va en su propio spec.
