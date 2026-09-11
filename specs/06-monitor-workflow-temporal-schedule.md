# 06 - MonitorWorkflow y Temporal Schedule

**Estado:** Implementado
**Depende de:** [05-durable-state-entity-workflows.md](05-durable-state-entity-workflows.md)
**Fecha:** 2026-09-10

**Objetivo:** Unir descubrimiento, resolución de fase, evaluación de gate y persistencia en una sola
pasada (`MonitorWorkflow`) que un **Temporal Schedule** dispara cada 5 minutos, sin ningún bucle de
polling en el código.

## Por qué existe este spec

Los specs 03, 04 y 05 dejaron todas las piezas construidas y probadas, pero **sueltas**: `IPatchDiscovery`
saca los patches en juego, `IPhaseResolver` los ubica en fase 1/2/3, `PhaseEvaluator` dice si el gate
está abierto, y `IPatchStateStore` persiste el veredicto en entity workflows. Nada de eso se ejecuta
solo: hoy solo se ejercita desde tests y desde harnesses descartables. Este spec entrega el pegamento
—`MonitorWorkflow`, una pasada que recorre esas cuatro etapas por cada patch— y el **reloj** que lo
dispara.

`Construction.md` §7 fija el reloj como un **Temporal Schedule cada 5 minutos**, y el README lo exige
en negativo: _"no estar pulleando constantemente"_. El Schedule cumple esa exigencia de forma literal:
es un timer del lado del servidor de Temporal. Entre tick y tick el worker del monitor **no emite
ninguna request**; el servidor le encola una ejecución de `MonitorWorkflow` cuando toca y el worker la
toma como cualquier otra tarea. No hay un hilo consultando "¿ya cerró la ejecución X?" en ningún lado.

El pedido explícito del usuario fue evaluar si el Schedule es lo más óptimo o si hay una alternativa
mejor. La respuesta —desarrollada en `## Decisiones tomadas y descartadas`— es que **no hay
alternativa event-driven disponible**: Temporal no publica un evento hacia afuera cuando una ejecución
cierra, así que reaccionar "en el momento" obligaría a instrumentar el proyecto observado (rompe la
genericidad, que es el objetivo del proyecto) o a tailear Visibility en un loop (eso _es_ polling, y
encima es la query compuesta que la restricción #1 documenta como colgada sobre Postgres). Lo que sí
tiene margen de optimización no es la cadencia sino el **trabajo por tick**; ese trabajo adaptativo se
deja fuera de alcance hasta tener evidencia de que hace falta.

El `MonitorWorkflow` de este spec es deliberadamente **efímero**: arranca, hace una pasada, devuelve
un `MonitorRunSummary` y cierra. No es un workflow long-running con `Workflow.DelayAsync` en un
`while(true)` —ese patrón sería un polling disfrazado y, además, chocaría con la restricción #4 (un
workflow que nunca cierra nunca pasa su propio gate 1→2)—. El estado que sobrevive entre pasadas ya
vive en los entity workflows del spec 05.

## Alcance

**Incluye:**

- **`src/Contracts/Monitor/MonitorOptions.cs`** — parámetros del Schedule y de la pasada, con el mismo
  parseo tolerante "ausente / no numérico / ≤ 0 ⇒ default, nunca lanza" de `DiscoveryOptions` y
  `PhaseOptions`:

  ```csharp
  public sealed record MonitorOptions(
      string ScheduleId,
      TimeSpan Interval,
      TimeSpan CatchupWindow,
      int MaxPatchesPerRun,
      string TaskQueue)
  {
      public const string DefaultScheduleId = "patch-monitor-schedule";
      public const int DefaultIntervalMinutes = 5;
      public const int DefaultCatchupWindowMinutes = 10;
      public const int DefaultMaxPatchesPerRun = 50;

      public static MonitorOptions FromEnvironment();
  }
  ```

  Lee `MONITOR_SCHEDULE_ID`, `MONITOR_INTERVAL_MINUTES`, `MONITOR_CATCHUP_WINDOW_MINUTES`,
  `MONITOR_MAX_PATCHES_PER_RUN` y `MONITOR_TASK_QUEUE` (este último ya existía, default
  `TaskQueues.PatchMonitor`).

- **`src/Contracts/Monitor/MonitorRunSummary.cs`** — el resultado de una corrida, lo que Temporal deja
  visible en la UI y lo que los tests asertan:

  ```csharp
  public sealed record MonitorRunSummary(
      DateTimeOffset StartedAt,
      DateTimeOffset FinishedAt,
      int PatchesDiscovered,
      int PatchesAssessed,
      int VerdictsChanged,
      int OverridesLoaded,
      IReadOnlyList<string> Errors);
  ```

  `VerdictsChanged` se cuenta comparando el `Revision` que devuelve `RecordAssessmentAsync` (spec 05)
  antes y después: si subió, el veredicto de ese patch cambió en esta pasada.

- **`src/Contracts/Monitor/PatchAssessment.cs`** —
  `record PatchAssessment(PhaseResolution Resolution, PhaseVerdict? Verdict)`. `Verdict` es nullable:
  las fases `Clean` (final) y `Unknown` (sin resolver) no tienen gate que evaluar.

- **`src/Contracts/Workflows/IMonitorWorkflow.cs`** — la interfaz compartida cliente/worker, mismo
  patrón que `IPatchStateWorkflow`:

  ```csharp
  [Workflow]
  public interface IMonitorWorkflow
  {
      [WorkflowRun] Task<MonitorRunSummary> RunAsync();
  }
  ```

- **`src/Common/Temporal/ScheduleBootstrapper.cs`** — plumbing genérico del Schedule, en `Common`
  junto a `WorkerHost` y `WorkflowStarter`. Tres builders **puros** (testeables sin cluster) y un
  método de efecto:
  - `static ScheduleSpec BuildSpec(MonitorOptions)` — `Intervals = [new ScheduleIntervalSpec(Interval)]`.
  - `static SchedulePolicy BuildPolicy(MonitorOptions)` —
    `Overlap = ScheduleOverlapPolicy.Skip`, `CatchupWindow = MonitorOptions.CatchupWindow`.
  - `static ScheduleActionStartWorkflow BuildAction(MonitorOptions)` —
    `ScheduleActionStartWorkflow.Create((IMonitorWorkflow wf) => wf.RunAsync(), new(id, taskQueue))`,
    con `WorkflowId` prefijo fijo `"patch-monitor-run"`.
  - `static async Task<bool> EnsureScheduleAsync(ITemporalClient client, MonitorOptions options)` —
    `client.CreateScheduleAsync(options.ScheduleId, new Schedule { Action = ..., Spec = ..., Policy = ... })`
    dentro de un `try/catch (ScheduleAlreadyRunningException)`. Devuelve `true` si lo creó, `false` si
    ya existía. Cualquier otra excepción propaga.

- **`src/PatchMonitor/Workflows/MonitorWorkflow.cs`** — la pasada. En pseudocódigo:

  ```
  RunAsync():
      startedAt = Workflow.UtcNow
      overridesLoaded = await LoadPhaseOverridesAsync()          // Activity, spec 05
      discovered = await DiscoverPatchesAsync()                   // Activity, spec 03
      foreach patch in discovered.Take(MaxPatchesPerRun):
          try:
              assessment = await AssessPatch(patch)              // Activity nueva (abajo)
              state = await RecordAssessmentAsync(input(patch, assessment))  // Activity, spec 05
              if state.Revision > revisionAntesDeEsteAssessment: verdictsChanged++
              patchesAssessed++
          catch (ApplicationFailureException ex):
              errors.Add($"{patch.Key}: {ex.Message}")           // aislar y seguir
      return new MonitorRunSummary(startedAt, Workflow.UtcNow, discovered.Count,
                                   patchesAssessed, verdictsChanged, overridesLoaded, errors)
  ```

  Sin `while`, sin `Workflow.DelayAsync`, sin `Continue-As-New`: una corrida por tick que cierra. El
  `PatchAssessmentInput` (spec 05) se arma en el workflow con `assessment.Resolution`,
  `assessment.Verdict` y `Workflow.UtcNow` como `ObservedAt`.

- **`src/PatchMonitor/Activities/PhaseActivities.cs`** _(modificado)_ — nueva
  `[Activity] PatchAssessment AssessPatch(PatchDiscoveryResult result)`: llama `_resolver.Resolve(result)`
  y —solo si la fase resuelta es `Coexistence` o `Deprecated`— `_evaluator.Evaluate(result.Key,
resolution.Phase, result.Executions)`. Para `Clean` / `Unknown` devuelve `Verdict = null`. Vive en
  una Activity y **no** en el workflow porque `PhaseEvaluator.Evaluate` usa `DateTimeOffset.UtcNow`
  internamente, que dentro de código de workflow es no-determinismo; además junta resolución +
  evaluación en un solo round-trip. `PhaseActivities` gana `PhaseEvaluator` por constructor.

- **`src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs`** _(modificado)_ — registra
  `MonitorOptions` singleton desde `FromEnvironment()`. `PhaseEvaluator` ya está registrado (spec 02).

- **`src/PatchMonitor/Program.cs`** _(modificado)_ — antes de `WorkerHost.RunAsync`: resolver el
  `Lazy<Task<ITemporalClient>>` ya registrado (el cliente del cluster propio del monitor) y
  `await ScheduleBootstrapper.EnsureScheduleAsync(client, monitorOptions)`, logueando si creó o
  reusó. Sumar `typeof(MonitorWorkflow)` a `additionalWorkflowTypes`.

- **`docker/docker-compose.yml`** _(modificado)_ — agrega `MONITOR_SCHEDULE_ID`,
  `MONITOR_INTERVAL_MINUTES`, `MONITOR_CATCHUP_WINDOW_MINUTES` y `MONITOR_MAX_PATCHES_PER_RUN` al
  `environment` del `patch-monitor-worker`.

- **`test/PatchMonitor.Tests/Monitor/`** — `MonitorOptionsTests.cs` (puro),
  `ScheduleBootstrapperTests.cs` (sobre los builders puros: `BuildSpec` da un intervalo de 5 min,
  `BuildPolicy` da `Overlap == Skip` y el `CatchupWindow` configurado, `BuildAction` apunta a
  `IMonitorWorkflow`), `MonitorWorkflowTests.cs` (sobre `WorkflowEnvironment.StartTimeSkippingAsync`
  con las Activities reales sobre `FakeExecutionSource` y un fake de `IPatchStateStore`),
  `MonitorRegistrationTests.cs` (resuelve `MonitorOptions` desde `AddPatchMonitorServices()`).

**No incluye (fuera de alcance de este spec):**

- **Pausar / reanudar / disparar el Schedule.** `ScheduleHandle.PauseAsync`, `UnpauseAsync` y
  `TriggerAsync` son superficie del spec 08, junto con la API HTTP. Acá `ScheduleBootstrapper` solo
  crea de forma idempotente.
- **La superficie HTTP** (`GET /patches`, `POST /patches/{key}/override`, `POST /schedule/trigger`):
  spec 08.
- **`INotifier` y la notificación exactamente-una-vez.** Este spec cuenta `VerdictsChanged` en el
  resumen, pero nadie notifica: spec 07.
- **Trabajo adaptativo por tick:** saltear los patches en fase `Clean`, espaciar los que llevan K
  corridas sin cambiar veredicto, jitter. Optimizaciones sobre el trabajo, no sobre la cadencia; se
  evalúan cuando haya un caso real que las pida.
- **Un camino de disparo por evento** desde el proyecto observado. Exigiría instrumentarlo y rompería
  la genericidad.
- **Child workflow por patch.** La pasada es un solo workflow secuencial; el fan-out en ejecuciones
  hijas infla la retención y complica el spec 10.
- **Aplicar `Workflow.Patched` a `MonitorWorkflow`.** El ciclo de versionado del propio monitor es el
  spec 10.
- **Apuntar a más de un namespace por corrida.** `DiscoveryOptions.Namespace` es uno solo.

## Modelo de datos

Todo lo nuevo de `Contracts` vive en `namespace Contracts.Monitor`, salvo la interfaz de workflow, que
va a `Contracts.Workflows` junto a `IHealthWorkflow` e `IPatchStateWorkflow`. Árbol tras este spec
(solo lo que cambia):

```text
proyecto_monitoreo/
├── src/
│   ├── Contracts/
│   │   ├── Monitor/
│   │   │   ├── MonitorOptions.cs
│   │   │   ├── MonitorRunSummary.cs
│   │   │   └── PatchAssessment.cs
│   │   └── Workflows/
│   │       └── IMonitorWorkflow.cs
│   ├── Common/
│   │   └── Temporal/ScheduleBootstrapper.cs
│   └── PatchMonitor/
│       ├── Workflows/MonitorWorkflow.cs
│       ├── Activities/PhaseActivities.cs                      (modificado)
│       ├── Infrastructure/ServiceCollectionExtensions.cs      (modificado)
│       └── Program.cs                                         (modificado)
├── docker/docker-compose.yml                                  (modificado)
└── test/
    └── PatchMonitor.Tests/
        └── Monitor/
            ├── MonitorOptionsTests.cs
            ├── ScheduleBootstrapperTests.cs
            ├── MonitorWorkflowTests.cs
            └── MonitorRegistrationTests.cs
```

Env vars que lee `MonitorOptions.FromEnvironment()`:

| Variable                         | Default                    | Significado                                                      |
| -------------------------------- | -------------------------- | ---------------------------------------------------------------- |
| `MONITOR_SCHEDULE_ID`            | `patch-monitor-schedule`   | Id del Temporal Schedule que dispara `MonitorWorkflow`.          |
| `MONITOR_INTERVAL_MINUTES`       | `5`                        | Cada cuánto el Schedule encola una corrida.                      |
| `MONITOR_CATCHUP_WINDOW_MINUTES` | `10`                       | Ventana para recuperar ticks perdidos si el worker estuvo caído. |
| `MONITOR_MAX_PATCHES_PER_RUN`    | `50`                       | Tope de patches procesados por corrida (acota la duración).      |
| `MONITOR_TASK_QUEUE`             | `patch-monitor-task-queue` | Task queue donde corre `MonitorWorkflow` (ya existía).           |

## Plan de implementación

1. **`MonitorOptions` + `FromEnvironment` + tests.** Crear `Contracts/Monitor/MonitorOptions.cs` con
   el parseo tolerante y las constantes de default. `MonitorOptionsTests.cs`: ausentes → los cinco
   defaults; valores válidos respetados; `"basura"`, `"0"` y `"-3"` → default, sin excepción.
   `dotnet test` en verde.

2. **DTOs de resultado y contrato de workflow.** Crear `MonitorRunSummary.cs`, `PatchAssessment.cs` e
   `IMonitorWorkflow.cs`. Solo contratos: nada los implementa aún. `dotnet build` compila.

3. **`PhaseActivities.AssessPatch` + test.** Agregar `PhaseEvaluator` al constructor y la Activity
   `AssessPatch`. Test sobre snapshots sintéticos (reusando `SnapshotSetBuilder`): fase `Clean` y
   `Unknown` → `Verdict == null`; `Coexistence` con una ejecución pre-patch abierta → `Verdict.Outcome
== Blocked`; las mismas drenadas → `Ready`.

4. **`MonitorWorkflow` — pasada feliz.** Implementar `RunAsync` sin el manejo de errores todavía:
   `LoadPhaseOverridesAsync` → `DiscoverPatchesAsync` → por patch `AssessPatch` + `RecordAssessmentAsync`
   → `MonitorRunSummary`. `MonitorWorkflowTests.cs` sobre `WorkflowEnvironment.StartTimeSkippingAsync`
   con un `FakeExecutionSource` de dos patches y un fake de `IPatchStateStore`: `PatchesDiscovered ==
2`, `PatchesAssessed == 2`, `Errors` vacío; un segundo run con el mismo estado deja `VerdictsChanged
== 0`.

5. **Aislamiento de fallos + tope `MaxPatchesPerRun`.** Envolver el cuerpo del `foreach` en
   `try/catch (ApplicationFailureException)` que acumula en `Errors` y sigue; aplicar
   `.Take(MaxPatchesPerRun)`. Tests: un patch cuyo `AssessPatch` lanza no impide que el otro se
   procese y su mensaje aparece en `Errors`; con `MaxPatchesPerRun = 1` y dos patches descubiertos,
   `PatchesAssessed == 1` y `PatchesDiscovered == 2`; un fallo de `DiscoverPatchesAsync` entero
   (Activity que lanza `DiscoveryConfigurationError` no reintentable) sí hace fallar la corrida.

6. **`ScheduleBootstrapper` + tests de los builders.** Implementar `BuildSpec` / `BuildPolicy` /
   `BuildAction` / `EnsureScheduleAsync` en `Common`. `ScheduleBootstrapperTests.cs`: `BuildSpec` da
   un único `ScheduleIntervalSpec` de 5 min con los defaults; `BuildPolicy` da `Overlap == Skip` y
   `CatchupWindow == 10 min`; `BuildAction` produce un `ScheduleActionStartWorkflow` con la task queue
   configurada. (`EnsureScheduleAsync` no se testea en unit: el test-server de time-skipping no
   soporta Schedules; se cubre en el paso 8.)

7. **DI + `Program.cs` + compose + `MonitorRegistrationTests`.** Registrar `MonitorOptions`; en
   `Program.cs` resolver el `ITemporalClient` propio y llamar `EnsureScheduleAsync` antes de
   `WorkerHost.RunAsync`, con `typeof(MonitorWorkflow)` agregado a `additionalWorkflowTypes`; sumar
   las cuatro env vars al `patch-monitor-worker`. `MonitorRegistrationTests.cs` resuelve
   `MonitorOptions` desde `AddPatchMonitorServices()`. `dotnet build` y `dotnet test` en verde.

8. **Verificación end-to-end manual.** Con el stack de `docker/` arriba y el worker recompilado:
   - `dotnet build PatchMonitor.sln` → 0 errores, 0 advertencias.
   - `dotnet test PatchMonitor.sln` → todos verdes.
   - `temporal schedule describe --schedule-id patch-monitor-schedule` (contra `localhost:7234`)
     muestra intervalo de 5 min, `Overlap: Skip` y la acción apuntando a `IMonitorWorkflow` en
     `patch-monitor-task-queue`.
   - Esperar dos ticks (o `temporal schedule trigger --schedule-id patch-monitor-schedule` dos
     veces): `temporal workflow list` muestra dos ejecuciones de `MonitorWorkflow` cerradas con un
     `MonitorRunSummary` en el resultado.
   - `temporal workflow list` sobre los entity: hay **una sola** ejecución por `PatchKey` (los ticks
     señalan, no crean de nuevo).
   - `docker compose stop patch-monitor-worker` + `up -d`: `temporal schedule describe` sigue
     mostrando **un** Schedule (el segundo arranque capturó `ScheduleAlreadyRunningException`).
   - Chequeo del "sin polling": `docker compose logs --tail=200 patch-monitor-worker` entre dos ticks
     no muestra actividad; solo aparece la línea de la pasada cuando el Schedule dispara.
   - Registrar la salida de estos comandos en este spec antes de marcar los criterios.

   **Salida registrada (2026-09-11, stack local vía `docker compose`):**

   ```
   $ dotnet build PatchMonitor.sln
   Compilación correcta.
       0 Advertencia(s)
       0 Errores

   $ dotnet test PatchMonitor.sln
   Correctas! - Con error: 0, Superado: 174, Omitido: 0, Total: 174

   $ temporal schedule describe --address localhost:7234 --schedule-id patch-monitor-schedule
     Action            {"Workflow":"MonitorWorkflow","TaskQueue":"patch-monitor-task-queue",...}
     Spec              [{"every":"5m 0s"}]
     OverlapPolicy     Skip
     CatchupWindow     10m 0s
     Paused            false

   $ temporal schedule trigger ... (x2) && esperar un tick automático
   $ temporal workflow list --query "WorkflowType='MonitorWorkflow'"
     Completed  patch-monitor-run-2026-09-11T16:30:00Z  MonitorWorkflow  (tick automático)
     Completed  patch-monitor-run-2026-09-11T16:28:46Z  MonitorWorkflow  (trigger manual)
     Completed  patch-monitor-run-2026-09-11T16:28:41Z  MonitorWorkflow  (trigger manual)
   # Resultado de una corrida:
   {"Errors":null,"FinishedAt":"...","OverridesLoaded":1,"PatchesAssessed":1,
    "PatchesDiscovered":1,"StartedAt":"...","VerdictsChanged":0}

   $ temporal workflow list --query "WorkflowType='PatchStateWorkflow'"
   # Una sola ejecución Running por cada PatchKey, antes y después de los ticks.

   $ docker compose stop patch-monitor-worker && docker compose up -d patch-monitor-worker
   patch-monitor-worker-1 | Schedule 'patch-monitor-schedule' ya existía.
   patch-monitor-worker-1 | Worker listening on 'patch-monitor-task-queue'...
   $ temporal schedule list --address localhost:7234
     patch-monitor-schedule   {"Workflow":"MonitorWorkflow"}   false   ...
   # Un solo Schedule tras el reinicio.

   # Chequeo "sin polling": logs del worker entre el arranque (16:29:15) y el tick automático
   # (16:30:00, ~45s después) no muestran ninguna línea nueva; recién aparece actividad cuando
   # el server de Temporal encola la tarea.
   ```

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa con el stack de Docker **apagado**; `MonitorWorkflowTests`
      corre sobre `WorkflowEnvironment.StartTimeSkippingAsync`.
- [x] `MonitorWorkflow` no contiene ningún `while`, `for` infinito, `Workflow.DelayAsync` ni
      `Continue-As-New`: `RunAsync` hace una pasada y devuelve.
- [x] El Schedule se crea una sola vez: un segundo arranque del worker no lanza y no deja dos
      Schedules (`EnsureScheduleAsync` devuelve `false` y loguea "ya existía").
- [x] El Schedule tiene `Overlap == ScheduleOverlapPolicy.Skip` y un `ScheduleIntervalSpec` igual a
      `MonitorOptions.Interval` (5 min con los defaults).
- [x] Un patch cuyo `AssessPatch` o `RecordAssessmentAsync` lanza `ApplicationFailureException` no
      aborta la pasada: los demás patches se procesan y el mensaje queda en `MonitorRunSummary.Errors`.
- [x] Un fallo del descubrimiento entero (`DiscoveryConfigurationError` no reintentable) sí hace
      fallar la corrida de `MonitorWorkflow`.
- [x] Con `MONITOR_MAX_PATCHES_PER_RUN = N` y más de N patches descubiertos, `PatchesAssessed == N` y
      `PatchesDiscovered` refleja el total real.
- [x] `AssessPatch` devuelve `Verdict == null` para fase `Clean` y `Unknown`, y un `PhaseVerdict` no
      nulo para `Coexistence` y `Deprecated`.
- [x] `MonitorRunSummary.VerdictsChanged` cuenta solo los patches cuyo `Revision` avanzó en esta
      pasada; dos pasadas seguidas sin cambios reales lo dejan en 0 en la segunda.
- [x] `MonitorOptions.FromEnvironment()` devuelve los cinco defaults con las env vars ausentes,
      respeta valores válidos y cae al default ante basura o valores ≤ 0.
- [x] Un `ServiceProvider` de `AddPatchMonitorServices()` resuelve `MonitorOptions`.
- [x] Los contratos de los specs 03, 04 y 05 no cambian salvo el agregado de `PhaseEvaluator` al
      constructor de `PhaseActivities` y la Activity `AssessPatch`.
- [x] El `patch-monitor-worker` declara las cuatro env vars nuevas y el worker arranca con
      `MonitorWorkflow` registrado y el Schedule creado.

## Decisiones tomadas y descartadas

- **Sí:** Temporal Schedule como reloj, cada 5 minutos. Es un timer del servidor: el worker no emite
  ninguna request entre ticks, que es exactamente la definición de "no pullear" del README. Trae
  gratis `Overlap`, `CatchupWindow`, pausa/reanudación y disparo on-demand —justo lo que el spec 08
  necesita— sin escribir una línea de scheduling propio.
- **No:** un `MonitorWorkflow` long-running con `Workflow.DelayAsync(5 min)` en un `while(true)`. Es
  polling con otro nombre (el workflow se despierta a mirar aunque no haya nada que hacer), infla la
  Event History sin techo y choca con la restricción #4: un workflow que nunca cierra nunca pasa su
  propio gate 1→2, lo que volvería imposible el spec 10.
- **No:** un monitor event-driven que reacciona al cierre de cada ejecución observada. **Temporal no
  emite eventos hacia afuera cuando una ejecución cierra**: no hay changefeed, ni webhook, ni cola de
  "workflow closed". Conseguir esa reacción exigiría (a) instrumentar el proyecto observado para que
  señale al monitor —rompe la genericidad, que es el objetivo declarado del proyecto— o (b) un loop
  que tailea Visibility, que además de ser polling es la query compuesta de `KeywordList` que la
  restricción #1 documenta como colgada (`context deadline exceeded`) sobre el Postgres del stack de
  referencia. No hay una tercera opción más óptima disponible; el Schedule es el techo.
- **Sí:** el `MonitorWorkflow` es efímero —una pasada y cierra— y devuelve un `MonitorRunSummary`. El
  estado que sobrevive entre pasadas ya vive en los entity workflows del spec 05; el workflow de la
  pasada solo necesita reportar qué hizo, y ese reporte queda visible en la UI de Temporal y es lo que
  los tests asertan sin tener que consultar N entities.
- **Sí:** una sola pasada secuencial sobre los patches, con tope `MaxPatchesPerRun`. Historia acotada
  y determinística, cero ejecuciones extra por tick, y el spec 10 (versionar el propio monitor) se
  mantiene simple. El paralelismo real no hace falta hasta que haya un namespace con cientos de
  patches, y entonces la optimización natural es sobre el trabajo por tick, no sobre el fan-out.
- **No:** un child workflow por patch. Multiplica ejecuciones por tick (N cada 5 min), infla la
  retención del namespace propio del monitor y complica el drenaje ordenado del spec 10 sin un
  beneficio real a esta escala.
- **Sí:** aislar el fallo de un patch (`try/catch` por iteración, acumular en `Errors`, seguir). Un
  patch con historia ilegible o un RPC transitorio no puede cegar la evaluación de los otros 20. Solo
  un fallo del descubrimiento entero —o un error explícitamente no reintentable— aborta la corrida,
  porque ahí no hay nada parcial que salvar.
- **Sí:** `AssessPatch` (resolución + evaluación de gate) como Activity, no como código de workflow.
  `PhaseEvaluator.Evaluate` usa `DateTimeOffset.UtcNow` internamente, que dentro de un workflow es
  no-determinismo puro. Meterlo en una Activity lo resuelve y de paso junta dos etapas en un
  round-trip.
- **Sí:** `ScheduleBootstrapper` en `Common`, con builders puros separados del método de efecto. Los
  builders se testean sin cluster; `EnsureScheduleAsync` se verifica en el paso e2e porque el
  test-server de time-skipping no soporta Schedules.
- **Sí:** crear el Schedule al arrancar el **worker**, no la API ni un servicio one-shot. El que
  ejecuta el trabajo es el que garantiza que el reloj exista; un Schedule sin worker que atienda la
  task queue solo acumula ticks fallidos. La creación es idempotente (`ScheduleAlreadyRunningException`
  capturada), así que reiniciar el worker es inocuo.
- **No:** exponer pausa/reanudación/disparo del Schedule en este spec. Es código que nadie invoca
  hasta que exista la API del spec 08; se agrega ahí, con su consumidor.
- **Sí:** cadencia configurable (`MONITOR_INTERVAL_MINUTES`) con default 5. El README pide "5 min de
  preferencia", no "5 min fijos", y un intervalo configurable deja que un test o un operador lo baje
  sin recompilar. El default cumple el requisito literal.
- **Sí:** `Overlap = Skip`. Si una pasada tarda más que el intervalo, el tick solapado se descarta en
  vez de encolarse; el estado se recupera solo en el tick siguiente. Acumular pasadas atrasadas solo
  empeoraría el atraso.

## Riesgos identificados

| Riesgo                                                                                                                            | Mitigación                                                                                                                                                                                                                             |
| --------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| El test-server de time-skipping no soporta Temporal Schedules, así que `EnsureScheduleAsync` no tiene cobertura unitaria.         | Los builders puros (`BuildSpec`/`BuildPolicy`/`BuildAction`) sí se testean; la creación real y su idempotencia se verifican en el paso 8 contra el cluster de Docker.                                                                  |
| Una pasada que tarde más de 5 minutos (namespace grande, Tempo­ral lento) haría que los ticks se pisen.                           | `Overlap = Skip` descarta el tick solapado y `MaxPatchesPerRun` acota la duración de cada pasada; si el caso aparece, la respuesta es subir el intervalo o bajar el tope, no cambiar el mecanismo.                                     |
| `EnsureScheduleAsync` falla al arrancar el worker (cluster no listo, permiso denegado).                                           | Se deja propagar a propósito: un worker vivo que nunca monitorea es peor que un contenedor que reinicia y hace el fallo visible en los logs. El `depends_on: temporal condition: service_healthy` ya cubre el caso "cluster no listo". |
| Cada tick abre y cierra una ejecución de `MonitorWorkflow` (288/día): crecimiento de ejecuciones cerradas en el namespace propio. | Son ejecuciones cortas y nada las mantiene abiertas; la retención del namespace las purga. No hay entity ni continue-as-new involucrado en la pasada.                                                                                  |
| Cambiar el código de `MonitorWorkflow` con una ejecución en vuelo rompería por no-determinismo.                                   | Las ejecuciones de la pasada duran segundos y cierran; la ventana de "ejecución viva" es mínima. El ciclo formal `Patched → DeprecatePatch → limpio` para este workflow es el spec 10.                                                 |

## Qué NO entra en este spec

- Pausar, reanudar o disparar el Schedule on-demand, y la superficie HTTP que lo expone.
- `INotifier` y la entrega exactamente-una-vez del cambio de veredicto.
- Trabajo adaptativo por tick: saltear fase `Clean`, backoff de patches estables, jitter.
- Un camino de disparo por evento desde el proyecto observado.
- Child workflow por patch o cualquier otra forma de fan-out en ejecuciones.
- Aplicar `Workflow.Patched` / `DeprecatePatch` a `MonitorWorkflow`.
- Apuntar el monitor a más de un namespace en la misma corrida.

Cada uno, si entra, va en su propio spec.
