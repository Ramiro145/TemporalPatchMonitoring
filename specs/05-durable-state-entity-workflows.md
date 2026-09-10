# 05 - Estado durable en entity workflows

**Estado:** Aprobado
**Depende de:** [04-current-phase-resolution.md](04-current-phase-resolution.md)
**Fecha:** 2026-09-10

**Objetivo:** Persistir el veredicto de cada patch sin ninguna base de datos propia, con un entity
workflow por `PatchKey` que sobrevive por `Continue-As-New`, un registry singleton como índice y un
puerto `IDecisionSink` para un sink externo futuro.

## Por qué existe este spec

Los specs 02, 03 y 04 arman el pipeline completo —descubrir, snapshotear, resolver fase, evaluar el
gate— pero **nada de eso sobrevive a la Activity que lo produjo**. `PhaseResolution` es un valor en
memoria y `InMemoryPhaseOverrideStore` es un `ConcurrentDictionary` que se pierde al reiniciar el
worker; la propia spec 04 lo anota como riesgo aceptado y difiere la durabilidad acá. Recién ahora
hay algo que valga la pena persistir: el veredicto de un patch en un instante dado.

`Construction.md` §3 ya cerró **dónde**: entity workflows en Temporal, **sin BD propia**. La razón
no es minimalismo estético sino barrera de adopción: Temporal ya es un store durable con exactamente
la semántica que hace falta (un workflow abierto nunca lo purga la retención, el estado es
determinístico y consultable en vivo por `[WorkflowQuery]`), y agregar Postgres o SQL Server para el
monitor sería infraestructura que el usuario tiene que operar sin ganar nada. La auditoría histórica
de largo plazo sí necesita otra cosa, y por eso entra el puerto `IDecisionSink`: aditivo, no-op por
default, sin reescribir el núcleo cuando alguien quiera volcar a SQL.

El precio de esa decisión es la **restricción #4**: un entity workflow nunca cierra. Si el monitor
guarda su estado en entity workflows, esas ejecuciones jamás pasarían el gate 1→2 de sus propios
patches, y la Event History crecería sin techo. `Continue-As-New` por umbral de historia es lo que
lo resuelve, y es un **requisito de este spec**, no un ajuste de performance: sin él el spec 10 —el
monitor versionándose a sí mismo— es directamente imposible.

El spec 07 necesita saber si un veredicto **cambió**, no cuál es: notificar por corrida spamearía
cada 5 minutos. Por eso el estado guarda el veredicto anterior y un `Revision` monotónico que avanza
solo cuando hay cambio real. El spec 08 necesita listar todos los patches sin depender de Visibility
(que sobre Postgres falla en queries compuestas, restricción #1): de ahí el registry singleton como
índice explícito.

## Alcance

**Incluye:**

- **`src/Contracts/State/PatchState.cs`** — el estado durable de un patch, y el elemento del ring
  buffer de cambios:

  ```csharp
  public sealed record PatchState(
      PatchKey Key,
      PatchPhase Phase,
      PhaseSource Source,
      string PhaseReason,
      PhaseVerdict? LastVerdict,
      PhaseVerdict? PreviousVerdict,
      DateTimeOffset? LastObservedAt,
      DateTimeOffset? LastChangedAt,
      PhaseOverride? Override,
      int AssessmentCount,
      int Revision,
      IReadOnlyList<PatchStateChange> History);

  public sealed record PatchStateChange(
      DateTimeOffset At,
      PatchPhase FromPhase,
      PatchPhase ToPhase,
      GateOutcome? FromOutcome,
      GateOutcome? ToOutcome,
      string Reason);
  ```

  Más `static PatchState Initial(PatchKey key)` y `PatchState ForCarryover(int historyLimit)`, que
  recorta `History` y resetea `AssessmentCount` a 0 para el `Continue-As-New`.

- **`src/Contracts/State/PatchAssessmentInput.cs`** — lo que el spec 06 manda por signal:
  `record PatchAssessmentInput(PatchKey Key, PhaseResolution Resolution, PhaseVerdict? Verdict,
DateTimeOffset ObservedAt)`. `Verdict` es nullable porque el gate puede no aplicar (fase `Clean`
  o `Unknown`).
- **`src/Contracts/State/PatchRegistryState.cs`** —
  `record PatchRegistryState(IReadOnlyList<PatchKey> Keys, DateTimeOffset UpdatedAt)`.
- **`src/Contracts/State/StateOptions.cs`** —
  `record StateOptions(int ContinueAsNewThreshold, int HistoryLimit, string TaskQueue)` con
  `FromEnvironment()` que lee `PATCH_STATE_CAN_THRESHOLD` (default `500`),
  `PATCH_STATE_HISTORY_LIMIT` (default `20`) y `MONITOR_TASK_QUEUE`
  (default `TaskQueues.PatchMonitor`), con el mismo patrón tolerante "ausente / no numérico / no
  positivo ⇒ default, nunca lanza" de `DiscoveryOptions` y `PhaseOptions`. Más
  `const string RegistryWorkflowId = "patch-registry"`.
- **`src/Contracts/Workflows/IPatchStateWorkflow.cs`** — la interfaz compartida entre cliente y
  worker (mismo patrón que `IHealthWorkflow`):

  ```csharp
  [Workflow]
  public interface IPatchStateWorkflow
  {
      [WorkflowRun] Task RunAsync(PatchKey key, PatchState? carryover);
      [WorkflowSignal] Task RecordAssessmentAsync(PatchAssessmentInput input);
      [WorkflowQuery] PatchState GetState();
      [WorkflowUpdate] Task<PatchState> SetOverrideAsync(PhaseOverride ov);
      [WorkflowUpdate] Task<PatchState> ClearOverrideAsync();
  }
  ```

- **`src/Contracts/Workflows/IPatchRegistryWorkflow.cs`** —
  `[WorkflowRun] RunAsync(PatchRegistryState? carryover)`,
  `[WorkflowSignal] RegisterAsync(PatchKey)`, `[WorkflowSignal] UnregisterAsync(PatchKey)`,
  `[WorkflowQuery] PatchRegistryState List()`.
- **`src/Contracts/State/IPatchStateStore.cs`** — el puerto que consumen los specs 06, 07 y 08:

  ```csharp
  public interface IPatchStateStore
  {
      Task<PatchState> RecordAssessmentAsync(PatchAssessmentInput input, CancellationToken ct = default);
      Task<PatchState?> GetStateAsync(PatchKey key, CancellationToken ct = default);
      Task RegisterAsync(PatchKey key, CancellationToken ct = default);
      Task<IReadOnlyList<PatchKey>> ListAsync(CancellationToken ct = default);
      Task<IReadOnlyList<PhaseOverride>> LoadActiveOverridesAsync(CancellationToken ct = default);
      Task<PatchState> SetOverrideAsync(PhaseOverride ov, CancellationToken ct = default);
      Task<PatchState> ClearOverrideAsync(PatchKey key, CancellationToken ct = default);
  }
  ```

- **`src/Contracts/State/IDecisionSink.cs`** —
  `Task RecordAsync(PatchKey key, PatchState state, CancellationToken ct = default)`. Un solo
  método; el adaptador SQL es trabajo futuro.
- **`src/PatchMonitor/Workflows/PatchStateWorkflow.cs`** — implementación del entity. Acumula el
  estado, aplica el override vigente sobre la resolución recibida, detecta cambio (tupla
  `Phase`, `Verdict?.Outcome`, `Verdict?.NextPhase`) y avanza `Revision` **solo** cuando cambia,
  empujando un `PatchStateChange` al ring buffer. `RunAsync` espera con
  `Workflow.WaitConditionAsync` a que `AssessmentCount >= ContinueAsNewThreshold` **y**
  `Workflow.AllHandlersFinished`, y entonces lanza `Workflow.CreateContinueAsNewException(...)` con
  `ForCarryover(HistoryLimit)`. El validator de `SetOverrideAsync` rechaza fases fuera de
  `{Coexistence, Deprecated, Clean}`.
- **`src/PatchMonitor/Workflows/PatchRegistryWorkflow.cs`** — índice singleton sobre un
  `Dictionary`/`SortedSet` de `PatchKey` (idempotente: registrar dos veces no cambia nada), con el
  mismo patrón de `Continue-As-New` arrastrando el set completo.
- **`src/PatchMonitor/Services/TemporalPatchStateStore.cs`** — implementación de `IPatchStateStore`
  sobre el `TemporalClient` del **cluster propio del monitor** (`TEMPORAL_HOST`, no
  `TARGET_TEMPORAL_HOST`: el estado del monitor no vive en el namespace observado).
  `RecordAssessmentAsync` hace `signal-with-start` sobre `key.ToWorkflowId()` (spec 02), registra
  la clave en el registry con otro `signal-with-start`, consulta `GetState()` y se lo pasa al
  `IDecisionSink` dentro de un `try/catch` que **traga** el fallo del sink. `GetStateAsync`
  distingue "not found" de otros errores de RPC reusando `WorkflowValidator`
  (`Construction.md` §4.6) y devuelve `null` para el primero.
- **`src/PatchMonitor/Services/NoopDecisionSink.cs`** — `Task.CompletedTask`, registrado por
  default.
- **`src/PatchMonitor/Activities/PatchStateActivities.cs`** — clase `[Activity]` que el spec 06
  invoca:
  - `[Activity] Task<PatchState> RecordAssessmentAsync(PatchAssessmentInput input)`
  - `[Activity] Task<int> LoadPhaseOverridesAsync()` — hidrata `IPhaseOverrideStore` con los
    overrides vigentes: hace `Clear` de los que ya no vienen del entity y `Set` de los que sí, de
    modo que un override borrado en el entity desaparezca también de la caché de corrida. Devuelve
    la cantidad cargada.
  - `[Activity] Task<PatchState?> GetPatchStateAsync(PatchKey key)`
  - `[Activity] Task<IReadOnlyList<PatchKey>> ListPatchesAsync()`
- **`src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs`** _(modificado)_ — registra
  `StateOptions` (singleton desde `FromEnvironment()`), el `TemporalClient` propio del monitor
  (singleton lazy, mismo patrón que `TemporalExecutionSource`), `IDecisionSink → NoopDecisionSink`,
  `IPatchStateStore → TemporalPatchStateStore` y `PatchStateActivities` por **tipo concreto**.
  Reemplaza el comentario "INotifier, IDecisionSink llegan en los specs 05+".
- **`src/PatchMonitor/Program.cs`** _(modificado)_ — agrega `typeof(PatchStateActivities)` a
  `activityTypes` y
  `additionalWorkflowTypes: new[] { typeof(PatchStateWorkflow), typeof(PatchRegistryWorkflow) }`
  a `WorkerHost.RunAsync`.
- **`docker/docker-compose.yml`** _(modificado)_ — agrega `PATCH_STATE_CAN_THRESHOLD` y
  `PATCH_STATE_HISTORY_LIMIT` al `environment` del `patch-monitor-worker`.
- **`test/PatchMonitor.Tests/PatchMonitor.Tests.csproj`** _(modificado)_ — agrega
  `<PackageReference Include="Temporalio" Version="1.9.0" />` y reemplaza el comentario "el paquete
  entra en el spec 05" por la nota de que el binario del test-server se descarga una vez.
- **`test/PatchMonitor.Tests/State/`** — `PatchStateWorkflowTests.cs`,
  `PatchRegistryWorkflowTests.cs`, `ContinueAsNewTests.cs`, `TemporalPatchStateStoreTests.cs`
  (todos sobre `WorkflowEnvironment.StartTimeSkippingAsync`), más `StateOptionsTests.cs` y
  `PatchStateRegistrationTests.cs`, que son puros.

**No incluye (fuera de alcance de este spec):**

- **Cambiar `IPhaseResolver` o `IPhaseOverrideStore` de la spec 04.** Ambos contratos quedan
  intactos: el resolver sigue siendo cómputo puro, síncrono y sin `CancellationToken`. La
  durabilidad se resuelve **hidratando** el store en memoria al inicio de cada pasada, no
  convirtiéndolo en un cliente de Temporal.
- **`MonitorWorkflow`, `ScheduleBootstrapper` y el Schedule de 5 minutos:** spec 06. Acá nadie
  llama a `RecordAssessmentAsync` de forma automática; se ejercita desde tests y desde el harness
  manual del paso de verificación.
- **Armar el `PatchAssessmentInput`** combinando `IPhaseResolver` con `PhaseEvaluator`. Ese union
  es del spec 06; acá el input llega ya armado.
- **Notificar el cambio de veredicto.** Este spec entrega la señal (`Revision`, `PreviousVerdict`,
  `LastChangedAt`); `INotifier` y la entrega exactamente-una-vez son del spec 07.
- **La superficie HTTP.** `GET /patches`, `GET /patches/{key}`, `POST /patches/{key}/override`:
  spec 08. Acá el `[WorkflowUpdate]` y su validator existen y se prueban, pero nadie los expone.
- **Un adaptador `IDecisionSink` real (SQL u otro).** Solo el puerto y el no-op.
- **Aplicar `Workflow.Patched` a estos workflows.** El ciclo de versionado del propio monitor es el
  spec 10; este spec solo deja el `Continue-As-New` que lo hace posible.
- **Poda del registry por antigüedad.** El índice guarda el set de claves conocidas; descartar
  patches que ya no aparecen en el namespace observado no entra acá.

## Modelo de datos

Todo lo nuevo de `Contracts` vive en `namespace Contracts.State`, salvo las dos interfaces de
workflow, que van a `Contracts.Workflows` junto a `IHealthWorkflow`. Árbol tras este spec (solo lo
que cambia):

```text
proyecto_monitoreo/
├── src/
│   ├── Contracts/
│   │   ├── State/
│   │   │   ├── PatchState.cs              (PatchState + PatchStateChange)
│   │   │   ├── PatchAssessmentInput.cs
│   │   │   ├── PatchRegistryState.cs
│   │   │   ├── StateOptions.cs
│   │   │   ├── IPatchStateStore.cs
│   │   │   └── IDecisionSink.cs
│   │   └── Workflows/
│   │       ├── IPatchStateWorkflow.cs
│   │       └── IPatchRegistryWorkflow.cs
│   └── PatchMonitor/
│       ├── Workflows/
│       │   ├── PatchStateWorkflow.cs
│       │   └── PatchRegistryWorkflow.cs
│       ├── Services/
│       │   ├── TemporalPatchStateStore.cs
│       │   └── NoopDecisionSink.cs
│       ├── Activities/PatchStateActivities.cs
│       ├── Infrastructure/ServiceCollectionExtensions.cs   (modificado)
│       └── Program.cs                                      (modificado)
├── docker/docker-compose.yml                               (modificado)
└── test/
    └── PatchMonitor.Tests/
        ├── PatchMonitor.Tests.csproj                       (modificado)
        └── State/
            ├── PatchStateWorkflowTests.cs
            ├── PatchRegistryWorkflowTests.cs
            ├── ContinueAsNewTests.cs
            ├── TemporalPatchStateStoreTests.cs
            ├── StateOptionsTests.cs
            └── PatchStateRegistrationTests.cs
```

Env vars que lee `StateOptions.FromEnvironment()`:

| Variable                    | Default                    | Significado                                                          |
| --------------------------- | -------------------------- | -------------------------------------------------------------------- |
| `PATCH_STATE_CAN_THRESHOLD` | `500`                      | Assessments recibidos antes de hacer `Continue-As-New`.              |
| `PATCH_STATE_HISTORY_LIMIT` | `20`                       | Tope del ring buffer de cambios que sobrevive al `Continue-As-New`.  |
| `MONITOR_TASK_QUEUE`        | `patch-monitor-task-queue` | Task queue donde el store arranca los entity workflows (ya existía). |

### Identidad y arranque

- **Entity por patch:** `WorkflowId = key.ToWorkflowId()` — el id determinístico y saneado que la
  spec 02 ya construyó con el prefijo `patch-state::` exactamente para esto. Arranque por
  `signal-with-start`: la primera pasada que ve un patch lo crea; las siguientes solo señalan.
- **Registry:** `WorkflowId = StateOptions.RegistryWorkflowId` (`"patch-registry"`), fijo y único.
  También por `signal-with-start`.
- Ambos corren en el **cluster propio del monitor** (`TEMPORAL_HOST`) y en la task queue del worker,
  no en el namespace observado.

### Qué cuenta como "cambio"

`RecordAssessment` compara la tupla `(Phase, LastVerdict?.Outcome, LastVerdict?.NextPhase)` contra
la del estado vigente:

- **Distinta** ⇒ `Revision++`, `LastChangedAt = input.ObservedAt`, `PreviousVerdict = LastVerdict`,
  y se empuja un `PatchStateChange` al ring buffer (recortado a `HistoryLimit`).
- **Igual** ⇒ solo avanzan `AssessmentCount` y `LastObservedAt`. `Revision` no se mueve: es la
  señal que el spec 07 usa para notificar exactamente una vez por cambio.

El override vigente (`Override?.IsActiveAt(now)`) se aplica **dentro del entity** sobre la fase
recibida, además de aplicarse en el resolver vía hidratación. Es deliberadamente redundante: si una
pasada corre antes de que la hidratación traiga un override recién declarado, el entity igual
persiste la fase correcta.

## Plan de implementación

1. **DTOs de `Contracts/State`.** Crear `PatchState.cs` (con `PatchStateChange`, `Initial` y
   `ForCarryover`), `PatchAssessmentInput.cs` y `PatchRegistryState.cs`. `dotnet build` compila;
   `Contracts` no gana ninguna referencia de paquete nueva.

2. **`StateOptions` + `FromEnvironment` + tests.** Crear `State/StateOptions.cs` con el parseo
   tolerante y `RegistryWorkflowId`. `StateOptionsTests.cs`: ausentes → 500 / 20 /
   `patch-monitor-task-queue`; valores válidos respetados; `"basura"`, `"0"` y `"-3"` → default,
   sin excepción. `dotnet test` en verde, todavía sin `Temporalio` en el csproj de tests.

3. **Interfaces de workflow y puertos.** Crear `Contracts/Workflows/IPatchStateWorkflow.cs` e
   `IPatchRegistryWorkflow.cs` con sus atributos, más `State/IPatchStateStore.cs` e
   `State/IDecisionSink.cs`. Solo contratos: nada las implementa aún. `dotnet build` compila.

4. **`PatchRegistryWorkflow` + tests de time-skipping.** Agregar `Temporalio 1.9.0` al csproj de
   tests. Implementar el registry (registro idempotente, `Unregister`, query `List`) sin el
   `Continue-As-New` todavía. `PatchRegistryWorkflowTests.cs` sobre
   `WorkflowEnvironment.StartTimeSkippingAsync`: registrar dos claves distintas → `List` devuelve
   dos; registrar la misma dos veces → sigue una; `Unregister` la saca.

5. **`PatchStateWorkflow` — acumulación y detección de cambio.** Implementar `RunAsync` (sin CAN),
   el signal `RecordAssessment` con la aplicación del override y la regla de cambio, y la query
   `GetState`. `PatchStateWorkflowTests.cs`: primer assessment → `Revision == 1`,
   `AssessmentCount == 1`, `PreviousVerdict == null`; un segundo idéntico → `Revision` sigue en 1 y
   `AssessmentCount == 2`; un tercero con otra fase → `Revision == 2`, `PreviousVerdict` es el
   anterior y `History` tiene dos entradas.

6. **`PatchStateWorkflow` — updates de override + validator.** Agregar `SetOverrideAsync` /
   `ClearOverrideAsync` con `[WorkflowUpdateValidator]`. Tests: un override a `Deprecated` sobre un
   estado que venía en `Coexistence` deja `Phase == Deprecated` y `Source == Override`; un override
   con fase `Unknown` es rechazado por el validator (`WorkflowUpdateFailedException`) y el estado
   **no** cambia; `ClearOverrideAsync` vuelve el estado a la fase inferida en el siguiente
   assessment.

7. **`Continue-As-New` en ambos workflows.** Agregar el `WaitConditionAsync` sobre
   `AssessmentCount >= ContinueAsNewThreshold && Workflow.AllHandlersFinished` y el
   `CreateContinueAsNewException` con `ForCarryover(HistoryLimit)`; lo mismo en el registry con el
   set completo. `ContinueAsNewTests.cs` con `PATCH_STATE_CAN_THRESHOLD` bajo (3): mandar 4
   assessments, verificar que el `RunId` cambió, que `AssessmentCount` se reseteó, que `Revision`,
   `Phase` y `Override` sobrevivieron y que `History` quedó recortada a `HistoryLimit`.

8. **`TemporalPatchStateStore` + `NoopDecisionSink`.** Implementar el store con `signal-with-start`
   para entity y registry, la query de lectura, el `null` ante "not found" vía `WorkflowValidator`,
   los dos updates y la invocación al sink en `try/catch`. `TemporalPatchStateStoreTests.cs` contra
   el entorno de time-skipping: `RecordAssessmentAsync` sobre una key nueva crea el entity **y** la
   registra; `GetStateAsync` de una key inexistente → `null`; `ListAsync` devuelve las keys
   registradas; un `IDecisionSink` que lanza no rompe `RecordAssessmentAsync`;
   `LoadActiveOverridesAsync` devuelve solo los vigentes.

9. **`PatchStateActivities` + hidratación del store de overrides.** Crear las cuatro activities;
   `LoadPhaseOverridesAsync` limpia de `IPhaseOverrideStore` los que ya no vienen y setea los
   vigentes. Test: con un override en el entity y otro obsoleto en el store en memoria, tras la
   activity `GetAll()` devuelve exactamente el vigente.

10. **DI + `Program.cs` + compose.** Registrar `StateOptions`, el `TemporalClient` propio,
    `IDecisionSink`, `IPatchStateStore` y `PatchStateActivities`; agregar los dos workflows a
    `additionalWorkflowTypes` y la Activity a `activityTypes`; sumar las dos env vars al
    `patch-monitor-worker`. `PatchStateRegistrationTests.cs` resuelve todo desde
    `AddPatchMonitorServices()`. `dotnet build` y `dotnet test` en verde.

11. **Verificación end-to-end manual.** Con el stack de `docker/` arriba y el worker recompilado:
    - `dotnet build PatchMonitor.sln` → 0 errores, 0 advertencias.
    - `dotnet test PatchMonitor.sln` → todos verdes (primera corrida descarga el test-server).
    - Con un harness descartable que invoque `RecordAssessmentAsync` dos veces con el mismo
      veredicto y una tercera con otra fase: `temporal workflow query` sobre
      `patch-state::default::ProbeWorkflow::probe_patch` muestra `AssessmentCount == 3`,
      `Revision == 2` y `History` con dos entradas.
    - `temporal workflow query --workflow-id patch-registry --type List` devuelve la clave.
    - Declarar un override por `[WorkflowUpdate]`, correr `LoadPhaseOverridesAsync` y comprobar que
      `ResolvePhase` (spec 04) devuelve `Source = Override` sin haberlo escrito nunca en memoria.
    - `docker compose stop patch-monitor-worker` + `up -d`: el estado del entity sigue ahí.
    - Registrar la salida de estos comandos en este spec antes de marcar los criterios.

## Criterios de aceptación

- [ ] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [ ] `dotnet test PatchMonitor.sln` pasa con el stack de Docker **apagado**; los tests de `State/`
      corren sobre `WorkflowEnvironment.StartTimeSkippingAsync`.
- [ ] `src/Contracts/Phase/` y `src/PatchMonitor/Services/PhaseResolver.cs` **no cambian**:
      `git diff --stat` de este spec no los toca, y `IPhaseResolver` / `IPhaseOverrideStore`
      conservan su firma de la spec 04.
- [ ] El entity se crea por `signal-with-start` con `WorkflowId == key.ToWorkflowId()`; dos
      assessments para la misma `PatchKey` van a la misma ejecución, no crean una segunda.
- [ ] `Revision` avanza **solo** cuando cambia `(Phase, Outcome, NextPhase)`; dos assessments
      idénticos consecutivos lo dejan igual y `AssessmentCount` sí avanza.
- [ ] Superado `PATCH_STATE_CAN_THRESHOLD`, el entity hace `Continue-As-New`: el `RunId` cambia,
      `AssessmentCount` vuelve a 0 y `Revision`, `Phase`, `Override` y `History` (recortada a
      `HistoryLimit`) sobreviven.
- [ ] El `Continue-As-New` nunca corre con handlers en vuelo (`Workflow.AllHandlersFinished` en la
      condición de espera).
- [ ] `SetOverrideAsync` con fase fuera de `{Coexistence, Deprecated, Clean}` es rechazado por el
      `[WorkflowUpdateValidator]` de forma sincrónica y el estado del entity queda intacto.
- [ ] `PatchRegistryWorkflow` es idempotente: registrar la misma `PatchKey` N veces deja una sola
      entrada en `List()`; también hace `Continue-As-New` arrastrando el set completo.
- [ ] `IPatchStateStore.GetStateAsync` de una `PatchKey` sin entity devuelve `null` y **no** lanza;
      cualquier otro error de RPC sí propaga (se distingue con `WorkflowValidator`).
- [ ] Un `IDecisionSink` que lanza no hace fallar `RecordAssessmentAsync`; `NoopDecisionSink` es la
      implementación registrada por default.
- [ ] Tras `LoadPhaseOverridesAsync`, `IPhaseOverrideStore.GetAll()` contiene exactamente los
      overrides vigentes de los entity workflows: los borrados en el entity desaparecen de la caché.
- [ ] `StateOptions.FromEnvironment()` devuelve `500` / `20` / `patch-monitor-task-queue` con las
      env vars ausentes, respeta valores válidos y cae al default ante basura o valores ≤ 0.
- [ ] Un `ServiceProvider` construido con `AddPatchMonitorServices()` resuelve `StateOptions`,
      `IPatchStateStore`, `IDecisionSink` y `PatchStateActivities`.
- [ ] El `patch-monitor-worker` declara `PATCH_STATE_CAN_THRESHOLD` y `PATCH_STATE_HISTORY_LIMIT`,
      y el worker arranca con los dos workflows y la Activity registrados.

## Decisiones tomadas y descartadas

- **Sí:** entity workflow por `PatchKey` con `WorkflowId = key.ToWorkflowId()`. La spec 02 ya
  construyó ese id determinístico y saneado exactamente con este propósito; reusar es gratis y hace
  que el estado sea direccionable sin índice intermedio.
- **Sí:** `signal-with-start` como único camino de escritura. Un solo RPC crea-o-señala, sin
  carrera de "existe / no existe" y sin `DescribeAsync` previo.
- **Sí:** `RecordAssessment` por **signal** y el override por **`[WorkflowUpdate]`**. Son dos
  caminos con requisitos opuestos: el assessment es alta frecuencia, no necesita respuesta y no
  debe bloquear la pasada de monitoreo; el override lo escribe una persona y necesita **rechazo
  sincrónico** de una fase inválida, que solo un update con validator da (la API del spec 08
  devolvería 202 sobre un signal y aceptaría basura en silencio).
- **No:** todo por update. Acoplaría la pasada de 5 minutos a la latencia de N entity workflows.
- **No:** todo por signal. Traslada al spec 08 el problema de confirmar un override rechazado.
- **Sí:** `Continue-As-New` por **umbral propio configurable** (`PATCH_STATE_CAN_THRESHOLD`) más
  `Workflow.AllHandlersFinished`. Determinístico y testeable: un test baja el umbral a 3 y ejercita
  el camino real sin generar cientos de eventos.
- **No:** `Workflow.ContinueAsNewSuggested` como único criterio. Es más fiel al servidor pero un
  test tendría que generar historia real para dispararlo, y la restricción #4 pide una garantía, no
  una sugerencia.
- **Sí:** el estado guarda actual + anterior + ring buffer acotado de cambios. `PreviousVerdict` y
  `Revision` son lo que el spec 07 necesita para notificar una sola vez; el buffer da "cómo
  evolucionó este patch" a la API del spec 08 sin ningún store extra.
- **No:** guardar todos los assessments. `Continue-As-New` tendría que arrastrar un payload que
  crece sin techo, y la auditoría de largo plazo ya tiene su lugar: `IDecisionSink`.
- **Sí:** hidratar `InMemoryPhaseOverrideStore` una vez por corrida desde los entity workflows.
  Mantiene `IPhaseResolver` como cómputo puro, síncrono y sin `CancellationToken` —el contrato que
  la spec 04 eligió a propósito— y deja el store en memoria como caché de corrida, no como fuente
  de la verdad.
- **No:** convertir `IPhaseOverrideStore` en async o pasarle un `TemporalClient`. Metería I/O
  dentro del resolver y obligaría a reescribir la spec 04 entera para ganar cero.
- **Sí:** el entity **también** aplica el override sobre la fase recibida. Redundancia deliberada:
  cubre la ventana entre declarar un override y la siguiente hidratación.
- **Sí:** registry singleton con `WorkflowId` fijo como índice explícito. Listar todos los patches
  no puede depender de Visibility: sobre Postgres las queries compuestas dan
  `context deadline exceeded` (restricción #1).
- **No:** listar los entity workflows con `ListWorkflowsAsync` filtrando por prefijo de id. Es
  exactamente la query que la restricción #1 dice que no funciona.
- **Sí:** puerto `IPatchStateStore` con `PatchStateActivities` encima. El `MonitorWorkflow` del
  spec 06 solo invoca Activities; nada de RPC desde dentro de un workflow, y el store queda
  sustituible en tests sin levantar cluster.
- **Sí:** `IDecisionSink` con `NoopDecisionSink` por default, invocado desde la **Activity**, con
  el fallo tragado. Un sink externo caído no puede tirar abajo el monitoreo; es el mismo criterio
  que `Construction.md` fija para el notificador del spec 07.
- **No:** invocar el sink desde el workflow. Sería I/O dentro de código que tiene que ser
  determinístico y reproducible.
- **Sí:** `RecordAssessmentAsync` devuelve el `PatchState` resultante (query tras el signal). El
  spec 07 se ahorra un round-trip y el sink recibe el estado ya escrito.
- **Sí:** los entity workflows viven en el cluster propio del monitor (`TEMPORAL_HOST`), no en el
  namespace observado (`TARGET_TEMPORAL_HOST`). Escribir estado del monitor en el namespace del
  proyecto observado lo contaminaría y rompería el spec 09.

## Riesgos identificados

| Riesgo                                                                                                                             | Mitigación                                                                                                                                                                                      |
| ---------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| La query `GetState()` inmediatamente posterior al `signal-with-start` devuelve el estado **previo** si el signal no se aplicó aún. | El `AssessmentCount` del estado lo delata; el sink recibe el estado previo y la pasada siguiente corrige. No hay pérdida de datos: el signal ya está durablemente en la historia.               |
| Un namespace con miles de patches crea miles de entity workflows y `LoadActiveOverridesAsync` hace una query por cada uno.         | El registry acota el fan-out y las queries son baratas; si aparece un caso real grande, la optimización natural es cachear por `Revision`, aditiva y sin tocar el puerto.                       |
| `Continue-As-New` con un handler de update en vuelo perdería la actualización.                                                     | La condición de espera exige `Workflow.AllHandlersFinished` además del umbral; hay un test que lo cubre.                                                                                        |
| El registry singleton es un punto único: si su ejecución se corrompe, "listar patches" deja de funcionar.                          | Es reconstruible: cada `RecordAssessmentAsync` vuelve a registrar la clave, así que una pasada del spec 06 lo repuebla completo. El estado autoritativo de cada patch vive en su propio entity. |
| Cambiar el código de estos workflows con ejecuciones vivas rompe por no-determinismo — y **nunca cierran**.                        | Es exactamente el problema que el proyecto monitorea (restricción #4). El `Continue-As-New` de este spec es lo que habilita el ciclo `Patched → DeprecatePatch → limpio` del spec 10.           |
| El binario del test-server se descarga en la primera corrida de `dotnet test`: CI sin red falla.                                   | Documentado en `CLAUDE.md`; queda cacheado en el perfil de usuario tras la primera corrida.                                                                                                     |

## Qué NO entra en este spec

- `MonitorWorkflow`, `ScheduleBootstrapper` y el Temporal Schedule de 5 minutos.
- Armar el `PatchAssessmentInput` combinando `IPhaseResolver` con `PhaseEvaluator`.
- `INotifier` y la entrega exactamente-una-vez del cambio de veredicto.
- La superficie HTTP que lista patches, consulta un veredicto y declara el override.
- Un adaptador `IDecisionSink` real (SQL u otro).
- Aplicar `Workflow.Patched` / `DeprecatePatch` a estos workflows.
- Poda del registry por antigüedad o por patches desaparecidos del namespace observado.

Cada uno, si entra, va en su propio spec.
