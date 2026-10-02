# 15 - Correcciones de severidad media de la auditoría

**Estado:** Implementado
**Depende de:** [03-patch-discovery-two-tier.md](03-patch-discovery-two-tier.md), [04-current-phase-resolution.md](04-current-phase-resolution.md), [06-monitor-workflow-temporal-schedule.md](06-monitor-workflow-temporal-schedule.md), [08-control-api.md](08-control-api.md), [12-single-cluster-namespace-isolation.md](12-single-cluster-namespace-isolation.md), [13-per-patch-absent-attribution.md](13-per-patch-absent-attribution.md), [14-audit-high-severity-fixes.md](14-audit-high-severity-fixes.md)
**Fecha:** 2026-10-02

**Objetivo:** que el monitor reconecte y se reinicie solo, que el Schedule refleje la
configuración vigente, que un patch en `Clean` no retroceda sin evidencia nueva y que todo patch
descubierto y toda corrida reciente terminen evaluados y visibles.

## Por qué existe este spec

`Audtioria_pathmonitor.md` reportó 9 hallazgos medios. M-1 (token de la API) es opcional y va al
spec 16 con la seguridad. Este spec cubre M-2 a M-9: ninguno rompe una decisión por sí solo, pero
juntos hacen que el monitor no se recupere solo, ignore su configuración o deje de responder en
silencio.

**Corrección a la auditoría sobre M-6.** El camino descrito (caso 6 del resolver ⇒ `Unknown`) no
se alcanza: un `PatchKey` solo nace de un marker propio (`AttributeAbsence` nunca crea claves), así
que cuando todos los markers salen de la ventana el patch deja de descubrirse y su entity conserva
`Clean`. La regresión real es otra: al correrse la ventana, si caen primero ejecuciones viejas
*con* marker, la tasa `p` de la Capa 2 (spec 13) baja, `N` sube y un patch `Clean` vuelve a
`Deprecated` sin ningún hecho nuevo. Este spec corrige esa y deja tests que documentan que el caso 6
no se alcanza desde el descubrimiento.

## Alcance

**Incluye:**

- **M-2 — `/health/workflow` reusa el cliente.**
  - `src/Common/Temporal/WorkflowStarter.cs`: sobrecargas que reciben `ITemporalClient`; las que
    conectan por su cuenta se eliminan si no quedan usos (grep).
  - `src/MonitorApi/Program.cs`: el endpoint inyecta el `TemporalClient` singleton y se lo pasa.
  - Sin rate limit: proteger la API es M-1 (spec 16).

- **M-3 — El Schedule se actualiza si difiere.**
  - `src/Common/Temporal/ScheduleBootstrapper.cs`: `EnsureScheduleAsync` devuelve
    `ScheduleEnsureResult { Created, Updated, Unchanged }`. Si el Schedule ya existe, lo describe y,
    si difiere del deseado, hace `UpdateAsync` conservando el estado (pausado y nota).
  - Comparación pura `ScheduleBootstrapper.Differs(Schedule current, MonitorOptions desired)`,
    testeable sin cluster: intervalo, `CatchupWindow`, `Overlap`, task queue y workflow type de la
    acción.
  - `src/PatchMonitor/Program.cs`: loguea los tres resultados.
  - Nota de corrección en `specs/06-monitor-workflow-temporal-schedule.md` (create-if-absent pasa a
    create-or-update) con referencia a este spec.

- **M-4 — El worker y la API se reinician solos.**
  - `docker/docker-compose.yml`: `restart: unless-stopped` en `patch-monitor-worker` y
    `monitor-api`.
  - Corregir el comentario de las líneas 110-112: la conexión **no** reintenta sola; si el cluster
    no está arriba, el proceso muere y Docker lo reinicia.

- **M-5 — Reconexión sin reinicio.**
  - Nuevo `src/Common/Temporal/ResettableAsyncLazy.cs`: lazy asíncrono que, si la fábrica falla,
    descarta la tarea fallida y vuelve a intentar en la llamada siguiente; si tiene éxito, cachea
    el resultado. Thread-safe: llamadas concurrentes comparten el intento en curso.
  - Reemplaza `Lazy<Task<ITemporalClient>>` en `TemporalExecutionSource`,
    `TemporalPatchStateStore`, `TemporalScheduleController`, `TemporalMonitorRunReader`, en los dos
    `ServiceCollectionExtensions`, en `src/PatchMonitor/Program.cs` y en los tests que lo construyen.

- **M-6 — `Clean` no retrocede sin evidencia nueva.**
  - `IPhaseResolver.Resolve(PatchDiscoveryResult result, PatchState? previous)` (`src/Contracts`).
  - `PhaseActivities.AssessPatch(PatchDiscoveryResult result, PatchState? previous)`;
    `MonitorWorkflow` le pasa el `before` que ya lee.
  - Regla en `PhaseResolver`, después del override y solo sobre la fase inferida: si
    `previous.Phase == Clean`, `previous.Source == Inferred`, `previous.LastChangedAt` no es nulo,
    la inferencia nueva no es `Clean` y **ninguna** ejecución con marker (`Present` o
    `PresentDeprecated`) arrancó después de `previous.LastChangedAt` ⇒ se devuelve `Clean`
    inferido con motivo `se conserva Clean: sin markers nuevos desde <LastChangedAt:o>`.
  - Una ejecución con marker arrancada después de `LastChangedAt` (reintroducción real del patch)
    saca de `Clean` como hasta ahora.
  - El implementador verifica en `PatchStateWorkflow` que `LastChangedAt` marca el último cambio
    de fase o veredicto; con `Clean` el veredicto es `null`, así que coincide con la entrada a
    `Clean`.

- **M-7 — Topes de descubrimiento coherentes.**
  - `src/Contracts/Discovery/DiscoveryOptions.cs`: `DefaultMaxHistories = 500` (igual a
    `DefaultMaxExecutions`).
  - `src/PatchMonitor/Program.cs`: advertencia al arrancar (nunca excepción) si
    `MaxHistories < MaxExecutions`, explicando que las ejecuciones sin leer dejan los patches en
    `Inconclusive`.
  - `docker/docker-compose.yml` (y overlays que lo fijen): `DISCOVERY_MAX_HISTORIES=500`.

- **M-8 — `GET /runs` muestra las corridas recientes.**
  - `src/Common/Temporal/MonitorRunReader.cs`: primera pasada con query **simple**
    `StartTime BETWEEN '<ahora-24h>' AND '<ahora+1d>'` (sin `AND WorkflowType`: compuestas
    prohibidas por `Construction.md` §4 restricción #1), filtrando `WorkflowType` en memoria y con
    el mismo tope `MaxScanned = 500`. Si junta menos de `limit` corridas (Schedule pausado, por
    ejemplo), cae al escaneo actual por `WorkflowType` y combina sin duplicar `RunId`.
  - Orden en memoria por `StartTime` descendente, como hoy.

- **M-9 — Todos los patches se evalúan.**
  - Nuevo helper puro `src/Contracts/Monitor/PatchRotation.cs`: ordena por `PatchKey`
    (`Namespace`, `WorkflowType`, `PatchId`, ordinal) y toma `MaxPatchesPerRun` desde el offset
    `(tickIndex * MaxPatchesPerRun) % count`, dando la vuelta al final.
  - `tickIndex = floor(Workflow.UtcNow / intervalo)`: determinístico, sin estado nuevo.
  - `MonitorRunConfig` gana `int IntervalMinutes` (lo llena `ConfigActivities` desde
    `MonitorOptions`).
  - `MonitorRunSummary` gana `int PatchesSkipped` al final (descubiertos − evaluados por tope).
    Espejo en `web/src/api/types.ts`.

- **Documentación.**
  - `README.md`: quitar el remedio `docker compose down -v` (el Schedule ahora se actualiza al
    arrancar el worker); nuevo default de `DISCOVERY_MAX_HISTORIES` y la advertencia; reinicio
    automático; rotación de patches y `PatchesSkipped`; `Clean` conservado sin evidencia nueva;
    ventana de `/runs`.
  - `Construction.md` §7: fila 15 como implementada al cerrar.
  - `CLAUDE.md`: "Estado actual" y la frase "`EnsureScheduleAsync` es create-if-absent" de
    Build / test / run.
  - Guía visual (<https://claude.ai/code/artifact/1b237336-261e-4cca-9807-589e748b7dbb>): actualizar
    si describe el remedio `down -v`, los topes de descubrimiento o el comportamiento de reinicio.

**Fuera de alcance (para otro spec):**

- M-1 (token de la API) y los bajos B-1 a B-8, incluido B-7 (no-root): spec 16.
- Rate limit de `/health/workflow`: va con la protección de la API (spec 16).
- Reintentos con backoff en el arranque (`WorkerHost.ConnectAsync`, `NamespaceBootstrapper`): el
  arranque sigue fallando rápido y Docker reinicia.
- Mostrar `PatchesSkipped` en el dashboard: solo se agrega al tipo espejo.
- Congelar `p` en el estado del entity (tocaría `PatchStateWorkflow`).
- `Workflow.Patched` en `MonitorWorkflow` (misma decisión que el spec 14).
- Auto-versionado del `MonitorWorkflow` (spec 10, diferido).

## Modelo de datos

```csharp
// src/Common/Temporal/ScheduleBootstrapper.cs
public enum ScheduleEnsureResult { Created, Updated, Unchanged }

// src/Common/Temporal/ResettableAsyncLazy.cs
public sealed class ResettableAsyncLazy<T>
{
    public ResettableAsyncLazy(Func<Task<T>> factory);
    public Task<T> GetValueAsync();   // cachea éxitos; tras un fallo reintenta en la próxima llamada
}

// src/Contracts/Monitor/MonitorRunConfig.cs — campo nuevo al final
public sealed record MonitorRunConfig(
    int MaxPatchesPerRun, bool NotificationsEnabled, int NotifierMaxAttempts, int IntervalMinutes);

// src/Contracts/Monitor/MonitorRunSummary.cs — campo nuevo al final
//   ..., int NotificationsSent, int NotificationsFailed, int PatchesSkipped)

// src/Contracts/Monitor/PatchRotation.cs
public static class PatchRotation
{
    public static IReadOnlyList<PatchDiscoveryResult> Select(
        IReadOnlyList<PatchDiscoveryResult> discovered, int maxPerRun, long tickIndex);
}
```

Cambios de firma: `IPhaseResolver.Resolve` y `PhaseActivities.AssessPatch` ganan
`PatchState? previous`. `PatchState` no cambia. Los campos nuevos van al final de records que
viajan en historias: una historia vieja sin el campo deserializa con `0`, así que no hace falta
`Workflow.Patched`.

## Plan de implementación

Cada paso deja `dotnet build` sin errores y `dotnet test` en verde.

1. **M-4.** `restart: unless-stopped` y comentario corregido en `docker-compose.yml`. Verificación
   manual: `docker compose up -d` con el cluster apagado ⇒ el worker reinicia en bucle; al levantar
   el cluster queda arriba.
2. **M-5a.** `ResettableAsyncLazy<T>` con tests: fallo y luego éxito en la segunda llamada; éxito
   cacheado (la fábrica corre una vez); llamadas concurrentes comparten el intento.
3. **M-5b.** Reemplazar `Lazy<Task<ITemporalClient>>` en los consumidores, en DI, en los dos
   `Program.cs` y en los tests.
4. **M-2.** Sobrecargas de `WorkflowStarter` con `ITemporalClient` y uso en `/health/workflow`.
5. **M-3.** `ScheduleEnsureResult`, `Differs` puro con tests en `ScheduleBootstrapperTests`
   (igual ⇒ `false`; cambia intervalo o catchup ⇒ `true`), update que conserva pausa y log en
   `Program.cs`. Nota de corrección en el spec 06.
6. **M-7.** Default 500, advertencia de arranque y compose; actualizar `DiscoveryOptionsTests`.
7. **M-6.** Firma con `previous`, regla de `Clean` conservado y tests en `PhaseResolverTests`:
   regresión por `p` con `previous = Clean` ⇒ `Clean`; marker nuevo posterior a `LastChangedAt` ⇒
   sale de `Clean`; `previous` con `Source = Override` ⇒ sin regla. Test en
   `PatchDiscoveryService` de que un patch sin markers en la ventana no se descubre (el caso 6 no
   se alcanza).
8. **M-9.** `PatchRotation` con tests (orden estable; dos ticks consecutivos cubren 2×tope sin
   repetir; vuelta al final; `count <= max` ⇒ todos), `IntervalMinutes` en `MonitorRunConfig`,
   `PatchesSkipped` en el summary y en `web/src/api/types.ts`; test en `MonitorWorkflowTests` con
   tope 1 y dos patches ⇒ `PatchesSkipped = 1`.
9. **M-8.** Ventana de 24 h con fallback en `TemporalMonitorRunReader`. Verificación manual contra
   el stack real: `GET /runs` devuelve primero la corrida más reciente.
10. **Documentación.** README, `Construction.md`, `CLAUDE.md`, guía visual y spec 06, según Alcance.

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa en verde sin Docker.
- [x] `npm run build` en `web/` pasa.
- [x] `TemporalClient.ConnectAsync` ya no aparece en `WorkflowStarter.cs` (grep).
- [x] `Lazy<Task<ITemporalClient>>` no aparece en `src/` (grep).
- [x] `ResettableAsyncLazy` reintenta tras un fallo y cachea un éxito (tests).
- [x] `ScheduleBootstrapper.Differs` detecta cambios de intervalo y de catchup (tests).
- [x] Al reiniciar el worker con otro `MONITOR_INTERVAL_MINUTES`, `GET /schedule` muestra el
      intervalo nuevo sin `down -v` (verificación manual).
- [x] Con el cluster apagado, `docker compose up -d` deja worker y API reiniciándose, y quedan
      arriba al levantar el cluster (verificación manual).
- [x] Un patch `Clean` cuya `p` baja por la ventana sigue `Clean` (test); un marker nuevo lo saca
      (test).
- [x] `DiscoveryOptions.FromEnvironment()` sin variables da `MaxHistories == MaxExecutions == 500`
      (test).
- [x] `PatchRotation` cubre todos los patches en `ceil(count / max)` ticks consecutivos (test).
- [x] `MonitorRunSummary.PatchesSkipped` refleja los no evaluados por tope (test).
- [x] `README.md` ya no recomienda `docker compose down -v` para recrear el Schedule.

## Decisiones

- **Sí:** reusar el `TemporalClient` singleton en `/health/workflow`.
- **No:** rate limit ni eliminar el endpoint. Proteger la API es M-1 (spec 16).
- **Sí:** create-or-update del Schedule solo si difiere, conservando la pausa.
- **No:** update incondicional (escribe en el cluster en cada restart). **No:** solo documentar el
  borrado (el remedio actual ya demostró ser inútil con un cluster externo).
- **Sí:** `restart: unless-stopped` más un lazy reseteable propio.
- **No:** backoff de arranque (Docker reinicia; más código sin beneficio claro). **No:**
  `TemporalClient.CreateLazy` (su manejo de un fallo de conexión en el SDK 1.9.0 no está
  verificado).
- **Sí:** `Clean` conservado ante pérdida de evidencia, reintroducción real detectada por un marker
  posterior a `LastChangedAt`. La regla vive en el resolver, sin tocar entity workflows.
- **No:** `Clean` terminal absoluto: ocultaría una reintroducción del patch (pasó en
  `ReleaseOrderDemo`). **No:** congelar `p` en el entity (exige `Workflow.Patched`).
- **Sí:** defaults iguales (500/500) más advertencia. **No:** bajar a 200/200 (con más de 200
  ejecuciones en 7 días todo queda `Inconclusive` por `LimitReached`). **No:** solo advertir (el
  default seguiría roto).
- **Sí:** ventana de `StartTime` con query simple y fallback. **No:** `RecentActions` del Schedule
  (guarda ~10, menos que `API_MAX_LIST_RUNS = 20`). **No:** query compuesta (restricción #1 de la
  standard visibility).
- **Sí:** orden por `PatchKey` y rotación derivada del tiempo del tick, sin estado nuevo, con
  `PatchesSkipped` visible.
- **No:** priorizar por antigüedad de evaluación (una lectura de estado por patch descubierto).

## Riesgos identificados

| Riesgo | Mitigación |
| --- | --- |
| 500 lecturas de historia por tick cargan el cluster observado | Configurable por `DISCOVERY_MAX_HISTORIES`; documentado en README |
| `Clean` conservado oculta una regresión real sin marker nuevo | Solo pasa sin ejecuciones con marker nuevas; una reintroducción siempre trae marker |
| `UpdateAsync` pisa un cambio manual del Schedule hecho en la UI | Solo se comparan los campos que gobierna `MonitorOptions`; documentado |
| Reinicio en bucle con el cluster caído llena los logs | Esperado y visible; se corta al volver el cluster |
| Corrida de `MonitorWorkflow` en vuelo durante el deploy con firmas nuevas | Falla ese tick; el siguiente la reemplaza (igual que en el spec 14) |
| Ventana de 24 h vacía con el Schedule pausado | Fallback al escaneo por `WorkflowType` |

## Qué **no** está en este spec

- Autenticación de la API (M-1), rate limit y los bajos B-1 a B-8.
- Backoff de arranque.
- `PatchesSkipped` en la UI del dashboard.
- `Workflow.Patched` en `MonitorWorkflow` ni cambios en los entity workflows.

Cada uno de estos, si hace falta, va en su propio spec.
