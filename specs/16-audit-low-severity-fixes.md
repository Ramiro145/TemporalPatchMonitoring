# 16 - Correcciones de severidad baja de la auditoría

**Estado:** Aprobado
**Depende de:** [02-patch-lifecycle-domain-model.md](02-patch-lifecycle-domain-model.md), [03-patch-discovery-two-tier.md](03-patch-discovery-two-tier.md), [04-current-phase-resolution.md](04-current-phase-resolution.md), [08-control-api.md](08-control-api.md), [11-monitor-web-mvp.md](11-monitor-web-mvp.md), [15-audit-medium-severity-fixes.md](15-audit-medium-severity-fixes.md)
**Fecha:** 2026-10-02

**Objetivo:** saldar los hallazgos bajos de la auditoría (B-1 a B-8) y el recorte erróneo de
`patchId` en el Tier 1, sin cambiar el `workflowId` de ningún entity existente con key segura.

## Por qué existe este spec

`Audtioria_pathmonitor.md` dejó 8 hallazgos bajos: ninguno compromete una decisión, pero son deuda
barata. Se suma un bug encontrado en vivo contra `ssy-yardflow` (2026-10-02):
`PatchDiscoveryService.ParsePatchId` quita un sufijo `-<dígitos>` que `TemporalChangeVersion` no
tiene (el SDK .NET guarda el `patchId` crudo), así que `rot-1` se descubre como `rot` cuando la
historia no se lee y aparece un entity fantasma. Comparte zona con B-3.

**Correcciones a la auditoría.**

- **B-6:** el paquete `cn` instalado se declara *drop-in replacement* de `clsx` + `tailwind-merge`
  (repo `shadcn-ui/cn`). Se verifica antes de reemplazarlo (paso 9).
- **B-8:** `WorkflowValidator` ya chequea `StatusCode.NotFound`; lo frágil es solo el fallback por
  texto `"no rows in result set"` y que los callers comparen el string `NotFoundError`.
- **M-1** (token de la API) queda fuera por decisión: solo se documenta como límite conocido.

## Alcance

**Incluye:**

- **B-1 — `Unreadable` conserva el motivo.**
  - `src/Contracts/Api/PatchResponses.cs`: `PatchSummaryResponse` gana `string? Error` al final
    (`null` en un resumen normal); `Unreadable(key, reason)` lo llena.
  - `web/src/api/types.ts`: `error: string | null` en `PatchSummary`.
  - `web/src/routes/Dashboard.tsx`: `isUnreadable` pasa a `patch.error != null` (se borra la
    heurística `phase === 0 && revision === 0` y su comentario); el badge "Ilegible" muestra el
    motivo en un tooltip.

- **B-2 — `PatchKey` sin colisiones por saneado.**
  - `src/Contracts/Domain/PatchKey.cs`: si **algún** segmento cambió al sanearse, el id lleva el
    sufijo `_<hash8>` calculado sobre el compuesto **crudo** (sin sanear); con truncado a 200 el
    sufijo es ese mismo hash. Si ningún segmento cambió, el id es **idéntico al actual** (incluido
    el caso largo con hash del compuesto). Las keys reales de hoy solo usan `[A-Za-z0-9._-]`: cero
    entities huérfanos.

- **B-3 + `ParsePatchId` — Tier 1 honesto.**
  - `src/PatchMonitor/Services/PatchDiscoveryService.cs`: en la rama Tier 1 (atributo sin markers),
    `historyReadable == false` ⇒ `MarkerPresence.Unknown` (el set queda `IsTruncated` y los gates
    dan `Inconclusive`); historia legible sin markers sigue en `Present`. Reescribir el comentario
    de las líneas 81-82.
  - `ParsePatchId` desaparece: la entrada de `TemporalChangeVersion` es el `patchId` crudo, igual
    que el `id` del marker `core_patch` (Tier 2).
  - `src/Contracts/Discovery/ExecutionListItem.cs`: doc del formato corregida (`<patchId>` crudo).
  - Tests: `HistoryFixtures.WithPatchAttribute` deja de agregar `-{version}`; el Theory
    `El_patchId_se_extrae_quitando_solo_el_sufijo_de_version` se reemplaza por uno que comprueba
    que `rot-1` y `my-feature-flag-12` se descubren intactos;
    `Tier1_sin_markers_legibles_igual_descubre_el_patch_como_Present` pasa a esperar `Unknown` y
    set truncado.

- **B-4 — `PhaseEvaluator` solo para fases de transición.**
  - `src/Contracts/Domain/Gates/PhaseEvaluator.cs`: `Clean` y `Unknown` lanzan
    `InvalidOperationException`, como el caso "sin gate". Doc-comment alineado con
    `PhaseActivities.AssessPatch` (`Verdict = null` en esas fases).
  - `PhaseEvaluatorTests`: `Fase_Clean_da_veredicto_terminal_sin_fase_siguiente` y
    `Fase_Unknown_da_Inconclusive_sin_fase_siguiente` pasan a esperar la excepción.

- **B-5 — `GET /patches` en paralelo acotado.**
  - `src/Contracts/Api/ApiOptions.cs`: `int ListPatchesConcurrency` (env
    `API_LIST_PATCHES_CONCURRENCY`, default 8, misma regla de `PositiveIntOrDefault`).
  - `src/MonitorApi/Endpoints/PatchEndpoints.cs`: lecturas de estado con `SemaphoreSlim` del tope;
    el orden de salida es el del registry; un fallo por key sigue dando `Unreadable` sin romper el
    listado.
  - `TemporalPatchStateStore.LoadActiveOverridesAsync`: mismo patrón con el mismo tope (lo recibe
    por `ApiOptions` o parámetro, a elección del implementador si el store no ve `ApiOptions`).
  - `docker/docker-compose.yml`: `API_LIST_PATCHES_CONCURRENCY=8` en `monitor-api`.

- **B-6 — Dependencias del dashboard.**
  - `web/package.json`: `shadcn` a `devDependencies`.
  - Verificar que `cn("px-2", "px-4")` devuelve `"px-4"` (y un caso de colores en conflicto). Si
    resuelve: se conserva `cn` y se documenta en el spec. Si no: `web/src/lib/utils.ts` pasa a
    `twMerge(clsx(inputs))`, se agregan `clsx` y `tailwind-merge`, y se quita `cn`.

- **B-7 — Contenedores no-root.**
  - `docker/Dockerfile.PatchMonitor` y `docker/Dockerfile.MonitorApi`: `USER $APP_UID` (usuario
    `app` de la imagen `aspnet:8.0`) en la etapa final; `EXPOSE 5100` se mueve a la etapa final.
  - `docker/Dockerfile.MonitorWeb`: base `nginxinc/nginx-unprivileged:alpine`, `EXPOSE 8080`.
  - `docker/nginx.conf`: `listen 8080`.
  - `docker/docker-compose.yml`: `monitor-web` publica `5101:8080` (el puerto del host no cambia).

- **B-8 — "No encontrado" por código, no por texto.**
  - Verificación en vivo contra el Temporal de `ssy-yardflow` (Postgres): `DescribeAsync` de un id
    inexistente en el namespace `monitor`. Si devuelve `StatusCode.NotFound`, se elimina el match
    `"no rows in result set"`; si no, se aísla en un único `TemporalErrors.IsNotFound(RpcException)`
    en `src/Common/Temporal/` con su test y un comentario con el código observado.
  - **Resultado de la verificación (2026-10-02, paso 4):** contra el Temporal de `ssy-yardflow`
    (persistencia Postgres, `localhost:7233`) y SDK `Temporalio` 1.9.0, `DescribeAsync` de un id
    inexistente lanza `RpcException` con `Code = NotFound` y mensaje
    `workflow not found for ID: <id>`, tanto en `monitor` como en `default`. El fallback por texto
    `"no rows in result set"` no se dispara: se **elimina** (sin `TemporalErrors.IsNotFound`).
  - `src/Common/Temporal/WorkflowValidator.cs`: el resultado expone `bool NotFound`;
    `TemporalPatchStateStore` (líneas ~73 y ~103) y `/health` en `src/MonitorApi/Program.cs`
    dejan de comparar `error == WorkflowValidator.NotFoundError`.

- **Documentación.**
  - `README.md`: campo `error` de `/patches`; `API_LIST_PATCHES_CONCURRENCY`; contenedores
    no-root; `TemporalChangeVersion` con `patchId` crudo y SDKs Go/Java (`GetVersion`) fuera de
    alcance; en "Límites conocidos": la API no tiene autenticación, `5100` se publica directo al
    host y `/health/workflow` no tiene rate limit (M-1, fuera de este spec); limpieza de entities
    fantasma `patch-state::...::<id recortado>` si existieran.
  - `Construction.md` §7: fila 16 con el slug `audit-low-severity-fixes`, sin M-1, implementada al
    cerrar.
  - `CLAUDE.md`: "Estado actual" (spec 16 implementado; M-1 sin spec) y conteo de tests.
  - Guía visual (<https://claude.ai/code/artifact/1b237336-261e-4cca-9807-589e748b7dbb>):
    actualizar si describe el puerto interno de nginx, variables de la API o el formato de
    `TemporalChangeVersion`.
  - Memoria `tier1_parsepatchid_suffix_bug.md`: marcar como resuelto o borrarla al cerrar.

**Fuera de alcance (para otro spec):**

- M-1: token / autenticación de la API, rate limit de `/health/workflow`, Swagger condicional.
- Soporte de `GetVersion` de Go/Java (formato `changeId-version`, sin marker `core_patch`).
- Migrar entities existentes cuyo id cambie por B-2 (solo keys con caracteres inseguros; no hay hoy).
- Reducir `GetStateAsync` a un solo RPC (quitar el `Describe` previo al `Query`).
- Override desde el dashboard; tests de frontend.
- `Workflow.Patched` en `MonitorWorkflow` y auto-versionado (spec 10).

## Modelo de datos

```csharp
// src/Contracts/Api/PatchResponses.cs — campo nuevo al final
public sealed record PatchSummaryResponse(
    ..., DateTimeOffset? LastObservedAt, DateTimeOffset? LastChangedAt, string? Error = null);

// src/Contracts/Api/ApiOptions.cs — campo nuevo
public sealed record ApiOptions(
    int MaxListPatches, TimeSpan OverrideDefaultTtl, int MaxListRuns, int ListPatchesConcurrency);
// DefaultListPatchesConcurrency = 8; env API_LIST_PATCHES_CONCURRENCY

// src/Common/Temporal/WorkflowValidator.cs — el resultado gana NotFound
//   (bool Exists, bool NotFound, WorkflowExecutionDescription? Info, string? Error)
```

```ts
// web/src/api/types.ts
export interface PatchSummary { /* ... */ error: string | null }
```

`PatchKey` no cambia de forma, solo `ToWorkflowId()` para keys saneadas. Ningún record que viaje
en historias de workflow cambia: no hace falta `Workflow.Patched`.

## Plan de implementación

Cada paso deja `dotnet build` sin errores y `dotnet test` en verde (`npm run build` en los pasos
que tocan `web/`).

1. **B-4.** Excepción para `Clean`/`Unknown` en `PhaseEvaluator` y tests actualizados.
2. **B-2.** Sufijo de hash solo para keys saneadas. Tests en `PatchKeyTests`: `"a b"` y `"a_b"`
   dan ids distintos; `"a:b"` y `"a/b"` dan ids distintos; los tests de formato existentes
   (`patch-state::default::OrderWorkflow::core-patch-v2`, `core_patch.v2`, truncado) siguen pasando
   sin cambios; determinismo del id saneado.
3. **B-3 + `ParsePatchId`.** Presencia `Unknown` con historia ilegible, `patchId` crudo, fixtures y
   tests de `PatchDiscoveryServiceTests` (ver Alcance), doc de `ExecutionListItem`.
4. **B-8a.** Verificación en vivo del código de `NotFound` contra `ssy-yardflow` (documentar el
   resultado en el spec).
5. **B-8b.** `NotFound` en el resultado de `WorkflowValidator`, callers sin comparación de string,
   match de texto eliminado o aislado según el paso 4. Test del helper si queda.
6. **B-1.** `Error` en `PatchSummaryResponse`; `PatchResponsesTests` verifica el motivo;
   `PatchEndpointsTests` (key ilegible) verifica que `Error` no es nulo y que una sana lo deja
   `null`. Espejo en `types.ts` y `Dashboard.tsx` con tooltip.
7. **B-5.** `ListPatchesConcurrency` en `ApiOptions` (+ `ApiOptionsTests`: default, válido, no
   numérico/no positivo), paralelo acotado en `PatchEndpoints` y `LoadActiveOverridesAsync`, compose.
   Test: con tope 2 y 5 keys, el orden de salida es el del registry y la concurrencia observada
   (contador en un `FakePatchStateStore` con demora) nunca pasa de 2.
8. **B-7.** Dockerfiles, `nginx.conf` y compose. Verificación manual: `docker compose build` y
   `up -d`; `docker compose exec patch-monitor-worker id -u` y `... monitor-api id -u` ≠ `0`;
   dashboard en `http://localhost:5101` funcionando y `/api/health` respondiendo a través de nginx.
9. **B-6.** Mover `shadcn`; prueba de `cn` (script node descartable o consola); según resultado,
   conservar o reemplazar. `npm run build` en verde.
10. **Documentación.** README, `Construction.md`, `CLAUDE.md`, guía visual y memoria, según
    Alcance.

## Criterios de aceptación

- [ ] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [ ] `dotnet test PatchMonitor.sln` pasa en verde sin Docker.
- [ ] `npm run build` en `web/` pasa.
- [ ] `PatchKey` con `"a b"` y `"a_b"` produce workflowIds distintos (test); una key con solo
      `[A-Za-z0-9._-]` conserva su id anterior (tests de formato sin cambios).
- [ ] Un patch `rot-1` descubierto solo por Tier 1 conserva el id `rot-1` (test).
- [ ] Tier 1 con historia ilegible da `MarkerPresence.Unknown` y set truncado (test).
- [ ] `PhaseEvaluator.Evaluate` con `Clean` o `Unknown` lanza `InvalidOperationException` (test).
- [ ] `GET /patches` con una key ilegible devuelve su `error` con el motivo (test) y el dashboard
      muestra el motivo en el badge "Ilegible" (verificación manual).
- [ ] La concurrencia de lecturas de `GET /patches` no supera `API_LIST_PATCHES_CONCURRENCY` y el
      orden se conserva (test).
- [ ] `"no rows in result set"` no aparece en `src/` o aparece solo dentro de
      `TemporalErrors.IsNotFound` (grep), y ningún caller compara contra `NotFoundError` (grep).
- [ ] `shadcn` está en `devDependencies` de `web/package.json`.
- [ ] Worker, API y dashboard corren con uid distinto de 0 y el dashboard responde en `:5101`
      (verificación manual).
- [ ] README documenta la falta de autenticación de la API como límite conocido.

## Decisiones

- **Sí:** hash del compuesto crudo solo cuando el saneado cambió algo. **No:** escape reversible
  (cambia el id de toda key con `_`, como `core_patch`: entities huérfanos masivos). **No:** solo
  detectar la colisión (el estado seguiría compartido).
- **Sí:** historia ilegible ⇒ `Unknown` en Tier 1; legible sin markers ⇒ `Present`. **No:**
  `Unknown` para todo Tier 1 sin markers (un patch descubierto solo por atributo nunca tendría
  fase).
- **Sí:** `patchId` crudo de `TemporalChangeVersion` (verificado en vivo contra `ssy-yardflow`).
  **No:** parseo tolerante al formato `changeId-version` (es de `GetVersion` en Go/Java, que no
  emite `core_patch` y está fuera del dominio del monitor).
- **Sí:** `Clean` y `Unknown` lanzan en `PhaseEvaluator`. **No:** devolver `PhaseVerdict?`
  (cambia la firma sin beneficio). **No:** dejar `Unknown` con `Inconclusive` (también
  inalcanzable).
- **Sí:** motivo en `Error` hasta el dashboard. **No:** seguir infiriendo "ilegible" por
  `phase === 0 && revision === 0`.
- **Sí:** paralelo acotado configurable (default 8). **No:** constante fija (convención de
  `*Options`). **No:** quitar el `Describe` previo al `Query` en este spec (toca el store; queda
  fuera).
- **Sí:** verificar `cn` antes de reemplazarlo. **No:** reemplazarlo a ciegas por un hallazgo que
  el propio paquete contradice.
- **Sí:** no-root en los tres contenedores, nginx sin privilegios en 8080. **No:** solo .NET.
- **Sí:** verificar en vivo el código de `NotFound` y quitar el match de texto si sobra.
- **No:** M-1 en este spec (decisión del usuario); queda documentado como límite conocido.

## Riesgos identificados

| Riesgo | Mitigación |
| --- | --- |
| Un entity vivo con key insegura cambia de id por B-2 | Hoy no hay ninguna; documentado en README con la limpieza manual |
| Con historias ilegibles (tope agotado) un patch pasa a `Unknown`/`Inconclusive` en vez de `Coexistence` | Es el comportamiento correcto (no saber ≠ retroceder); el default 500/500 del spec 15 lo hace raro |
| Entities fantasma con el id recortado quedan en el registry | Limpieza manual documentada (signal `Unregister` + terminate) |
| El paralelo de `GET /patches` carga más el cluster por request | Tope configurable; default 8 |
| `USER app` sin permisos sobre algún path escrito en runtime | Verificación manual con `docker compose up`; el worker y la API no escriben a disco |
| El Postgres de `ssy-yardflow` no devuelve `NotFound` | Se conserva el fallback aislado en `TemporalErrors.IsNotFound` |
| Cambio de `ToWorkflowId` afecta el orden de `PatchRegistryWorkflow.List()` | Solo para keys saneadas (inexistentes hoy); la lista es una query, no emite comandos |

## Qué **no** está en este spec

- Autenticación de la API (M-1), rate limit y Swagger condicional.
- `GetVersion` de Go/Java.
- Migración de entities por cambio de id.
- `GetStateAsync` de un solo RPC.

Cada uno de estos, si hace falta, va en su propio spec.
