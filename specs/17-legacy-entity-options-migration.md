# 17 - Entities antiguas determinísticas y fallo rápido

**Estado:** Aprobado
**Depende de:** [05-durable-state-entity-workflows.md](05-durable-state-entity-workflows.md), [14-audit-high-severity-fixes.md](14-audit-high-severity-fixes.md), [15-audit-medium-severity-fixes.md](15-audit-medium-severity-fixes.md), [16-audit-low-severity-fixes.md](16-audit-low-severity-fixes.md)
**Fecha:** 2026-10-02

**Objetivo:** que ningún workflow del monitor lea el entorno, ni siquiera el de las entities arrancadas
antes del spec 14, y que un entity con replay roto no cuelgue el tick ni devuelva un 500 sin cuerpo.

## Por qué existe este spec

La prueba e2e de cierre de la auditoría (2026-10-02, contra el Temporal de `ssy-yardflow`) mostró que
el hallazgo A-3 quedó corregido solo para las entities **nuevas**. El spec 14 conservó
`options ?? StateOptions.FromEnvironment()` como "camino legado" para las ejecuciones vivas arrancadas
sin el argumento, con la idea de que quedaran determinísticas tras su próximo Continue-As-New. Pero esas
entities nunca llegan al umbral (500 assessments), así que siguen en el camino legado indefinidamente.

Evidencia (corrida del 2026-10-02): con 6 entities antiguas vivas, bajar `PATCH_STATE_CAN_THRESHOLD`
de 500 a 20 y reiniciar el worker hizo que el replay de las 3 con más de 20 assessments fallara con
`[TMPRL1100] Nondeterminism error: Continue as new workflow machine does not handle this event:
HistoryEvent(id: N, WorkflowExecutionUpdateAccepted)`. Es exactamente el escenario que describía la
auditoría. Dos defectos lo amplificaron:

- Un tick de `MonitorWorkflow` quedó colgado más de 4 minutos: la activity que consulta el estado
  reintenta sin tope una query que nunca va a funcionar.
- `GET /patches/{ns}/{type}/{patchId}` devolvió 500 sin cuerpo (el listado, en cambio, ya marca el
  patch como `Unreadable` con el motivo).

Las entities nacidas con el argumento de opciones **sí** quedaron bien (verificado en vivo con un
monitor aislado en el namespace `monitor2`: 0 errores y el CAN siguió usando el umbral arrastrado).

## Alcance

**Incluye:**

- **Sin `FromEnvironment()` en workflows.**
  - `src/PatchMonitor/Workflows/PatchStateWorkflow.cs` (`[WorkflowInit]`) y
    `PatchRegistryWorkflow.cs`: `_options` pasa a `StateOptions?`, sin fallback al entorno.
  - La condición de Continue-As-New solo puede cumplirse con `_options is not null`. Una run antigua
    (sin opciones) no hace CAN, igual que su historia grabada (ninguna entity viva había llegado al
    umbral), así que su replay es idéntico con cualquier valor del entorno.
  - Mientras `_options` sea nulo, `AppendTrimmed` usa `StateOptions.DefaultHistoryLimit` (constante).
- **Migración automática de entities antiguas.**
  - `IPatchStateWorkflow` e `IPatchRegistryWorkflow` (`src/Contracts/Workflows/`) ganan el signal
    `MigrateOptionsAsync(StateOptions options)` y el query `HasRecordedOptions()`.
  - El handler llama `Workflow.Patched("state-options-recorded-v1")` (la primera evaluación es en vivo,
    así que devuelve `true` y graba el marker), asigna `_options` si todavía es nulo y queda en la
    historia. El siguiente Continue-As-New arrastra las opciones como argumento: desde ahí la entity
    es determinística y respeta el umbral.
  - `src/Common/State/TemporalPatchStateStore.cs`: tras el `SignalWithStartAsync` de entity y de
    registry, si el `workflowId` no está en un set por proceso `_optionsChecked`, consulta
    `HasRecordedOptions`; si es `false` envía `MigrateOptionsAsync(_options)`; luego lo agrega al set.
    Costo: una query por entity por vida del proceso.
  - En `PatchRegistryWorkflow.RunAsync`: `_options = options ?? _options` (un signal puede llegar antes
    de que corra `RunAsync`).
- **Fallo rápido.**
  - `src/PatchMonitor/Activities/PatchStateActivities.cs` (`GetPatchStateAsync`,
    `RecordAssessmentAsync`) y `NotificationActivities.cs`: un `WorkflowQueryFailedException` se
    relanza como `ApplicationFailureException` con `nonRetryable: true`. El patch afectado cae al
    `catch (ActivityFailureException)` de `MonitorWorkflow`, queda en `errors` del run y el tick sigue
    con los demás patches.
- **`GET /patches/{ns}/{type}/{patchId}` ilegible ⇒ 503 con el motivo.**
  - `src/MonitorApi/Endpoints/PatchEndpoints.cs`: `GetAsync` y `SetOverrideAsync` capturan
    `Exception ex when ex is not OperationCanceledException` y devuelven
    `TypedResults.Problem(statusCode: 503, detail: ex.Message)`; el tipo de retorno suma
    `ProblemHttpResult`.
- **Documentación.**
  - `README.md`: cambiar `PATCH_STATE_*` es seguro; migración automática de entities antiguas; el 503.
  - `CLAUDE.md`: "Estado actual" y la convención "El único `FromEnvironment()` que queda es el fallback
    de ejecuciones vivas…" (deja de ser cierta).
  - `Construction.md` §7: fila 17.
  - `specs/14-audit-high-severity-fixes.md`: nota de corrección (el fallback legado se elimina) con
    referencia a este spec.
  - `docs/e2e/`: evidencia de la verificación en vivo.

**Fuera de alcance (para otro spec):**

- M-1: token / autenticación de la API.
- El parpadeo de fase por historia truncada (con `DISCOVERY_MAX_HISTORIES` bajo, un patch pasó de fase
  1 a Unknown y volvió, con dos notificaciones): es ruido, no un error de corrección, y el default
  500/500 del spec 15 lo hace raro.
- Reducir `GetStateAsync` a un solo RPC (quitar el `Describe` previo al `Query`).
- Auto-versionado del `MonitorWorkflow` (spec 10, diferido).
- Migrar entities por cambio de id.

## Modelo de datos

Ningún record que viaje en historias cambia. Cambios de contrato:

```csharp
// src/Contracts/Workflows/IPatchStateWorkflow.cs e IPatchRegistryWorkflow.cs
[WorkflowSignal] Task MigrateOptionsAsync(StateOptions options);
[WorkflowQuery]  bool HasRecordedOptions();

// src/Contracts/State/StateOptions.cs
public const int DefaultHistoryLimit = /* el default vigente de HistoryLimit */;
```

`RunAsync(..., StateOptions? options = null)` conserva su firma. El signal y el query son handlers
nuevos: no aparecen en historias viejas, así que su replay no cambia. El marker
`state-options-recorded-v1` solo se evalúa en vivo, dentro del handler de migración.

## Plan de implementación

Cada paso deja `dotnet build` sin errores y `dotnet test` en verde.

1. **Fallo rápido.** `WorkflowQueryFailedException` ⇒ `ApplicationFailureException` no reintentable en
   `PatchStateActivities` y `NotificationActivities`. Test con un store falso que lanza la excepción: la
   activity falla sin reintentos.
2. **503.** `PatchEndpoints.GetAsync` y `SetOverrideAsync`. Tests en `PatchEndpointsTests`: store que
   lanza ⇒ 503 con `detail`; store sano ⇒ sin cambios.
3. **Contrato.** Signal y query en las dos interfaces y `StateOptions.DefaultHistoryLimit`.
4. **Workflows.** `_options` nullable, condición con `_options is not null`, handler de migración con
   `Workflow.Patched`, `_options = options ?? _options` en el registry. Tests en
   `PatchStateWorkflowTests` y `PatchRegistryWorkflowTests` (en la colección `EnvVarCollection`): una run
   arrancada sin opciones, con `PATCH_STATE_CAN_THRESHOLD=1` en el entorno, no hace CAN; tras
   `MigrateOptionsAsync` con umbral 2 sí lo hace y el CAN arrastra las opciones; migrar dos veces es
   idempotente.
5. **Store.** Migración perezosa con `_optionsChecked`. Tests en `TemporalPatchStateStoreTests`: una
   entity antigua (arrancada sin opciones) recibe un solo `MigrateOptionsAsync`; una nueva no lo recibe.
6. **Verificación en vivo** contra el Temporal de `ssy-yardflow` (las 6 entities antiguas siguen vivas
   en el namespace `monitor`): reconstruir con el overlay local, esperar un tick y comprobar que
   `HasRecordedOptions` es `true` en las 6. Bajar `PATCH_STATE_CAN_THRESHOLD` a 20, reiniciar el worker
   y comprobar 0 errores de no determinismo, `/patches` sin `error` y ticks que no se cuelgan. Restaurar
   el valor. Dejar la evidencia en `docs/e2e/`.
7. **Documentación** según el alcance.

## Criterios de aceptación

- [ ] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [ ] `dotnet test PatchMonitor.sln` pasa en verde sin Docker.
- [ ] `FromEnvironment` no aparece en `src/PatchMonitor/Workflows/` (grep).
- [ ] Una run sin opciones, con un umbral bajo en el entorno, no hace Continue-As-New (test, ambos
      workflows).
- [ ] `MigrateOptionsAsync` deja `HasRecordedOptions == true` y el Continue-As-New siguiente arrastra
      las opciones (test).
- [ ] El store migra una entity antigua una sola vez por proceso y no envía el signal a una nueva (test).
- [ ] Un `WorkflowQueryFailedException` en una activity no se reintenta y deja el motivo en `errors` del
      run (test).
- [ ] `GET` de un patch con entity ilegible devuelve 503 con `detail` (test).
- [ ] En vivo: las entities antiguas quedan con `HasRecordedOptions == true`, y cambiar
      `PATCH_STATE_CAN_THRESHOLD` no produce `Nondeterminism error` ni ticks colgados (verificación
      manual).
- [ ] `README.md`, `CLAUDE.md`, `Construction.md` y la nota del spec 14 reflejan el cambio.

## Decisiones

- **Sí:** migración automática con `Workflow.Patched`. **No:** bootstrap al arrancar el worker (más
  código y depende de una query sobre el registry). **No:** solo documentar (el riesgo seguiría latente).
- **Sí:** eliminar el fallback al entorno. **No:** conservarlo: es la causa del defecto.
- **Sí:** que la run antigua no haga CAN hasta migrar, lo que coincide con su historia grabada. **No:**
  una constante de umbral para el camino legado (podría diferir del valor con que se grabó la historia).
- **Sí:** migración perezosa con cache por proceso. **No:** un signal en cada assessment (duplicaría
  eventos en la historia).
- **Sí:** fallo rápido solo para `WorkflowQueryFailedException`. **No:** un `MaximumAttempts` global
  (cambia el comportamiento ante fallos transitorios, que sí conviene reintentar).
- **Sí:** 503 con el motivo en `ProblemDetails`. **No:** 200 con `summary.error` (un detalle sin datos
  no es una respuesta correcta).
- **No:** abordar el parpadeo de fase del B-3 en este spec.

## Riesgos identificados

| Riesgo | Mitigación |
| --- | --- |
| Una entity con el replay ya roto antes de migrar | Esta versión no lee el entorno: el replay vuelve a coincidir con la historia al desplegar |
| Worker y API con `PATCH_STATE_*` distintos migran con el valor de quien llegue primero | Documentado; el valor queda fijo en la entity tras su primer Continue-As-New |
| Una run antigua sin migrar crece sin Continue-As-New | Se migra en el primer assessment tras el deploy |
| Una query extra por entity al arrancar cada proceso | Una por entity por vida del proceso |
| Corrida de `MonitorWorkflow` en vuelo durante el deploy | Falla ese tick; el siguiente la reemplaza (igual que en los specs 14 y 15) |

## Qué **no** está en este spec

- Autenticación de la API (M-1).
- El parpadeo de fase por historia truncada.
- `GetStateAsync` de un solo RPC.
- Auto-versionado del `MonitorWorkflow` (spec 10).

Cada uno de estos, si hace falta, va en su propio spec.
