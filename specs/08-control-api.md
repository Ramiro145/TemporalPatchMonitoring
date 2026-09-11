# 08 - API de control

**Estado:** Implementado
**Depende de:** [07-pluggable-notifier.md](07-pluggable-notifier.md)
**Fecha:** 2026-09-11

**Objetivo:** Una única superficie HTTP para operar el monitor —listar patches y su fase, consultar
uno, disparar el chequeo on-demand, pausar/reanudar el Schedule y declarar un override manual de
fase— sin agregar ningún camino de escritura que los specs anteriores no tuvieran ya.

## Por qué existe este spec

Los specs 03 a 07 construyeron un monitor completo y ciego. Descubre patches, resuelve su fase,
evalúa los gates, persiste el veredicto en entity workflows y notifica cuando cambia — pero la única
forma de **verle el estado** es la UI de Temporal o `temporal workflow query` desde una terminal, y
la única forma de **tocarlo** es un `temporal workflow update execute` a mano con el JSON del
`PhaseOverride` escrito de memoria. Es exactamente lo que `Construction.md` §2 promete en la fila
"Registra el veredicto de forma durable **y lo expone (API + `[WorkflowQuery]`)**": la mitad del
`[WorkflowQuery]` está hecha desde el spec 05; la mitad de la API sigue vacía.

El `MonitorApi` del spec 01 existe desde el primer día pero nunca creció: hoy son 65 líneas con
`/health` y `/health/workflow`. Este spec lo llena. No inventa capacidades nuevas: los cuatro verbos
que pide `Construction.md` §7 ya están implementados detrás de `IPatchStateStore`
(`ListAsync`, `GetStateAsync`, `SetOverrideAsync`, `ClearOverrideAsync`) desde el spec 05, probados
y usados por el `MonitorWorkflow`. Lo único realmente nuevo es **manejar el Schedule** —pausar,
reanudar, disparar— que el `ScheduleBootstrapper` del spec 06 sabe crear pero no operar.

Hay un obstáculo estructural que este spec tiene que resolver antes de escribir un solo endpoint: la
única implementación de `IPatchStateStore` vive en `src/PatchMonitor`, que es un `Exe` con los
workflows y las activities del worker, y `MonitorApi` referencia solo `Common` y `Contracts`. El
store no depende de nada del worker —solo de `Temporalio`, `Contracts` y el `WorkflowValidator` de
`Common`—, así que su lugar natural es `Common`, que `Construction.md` §5 define precisamente como
"plumbing genérico de Temporal" compartido. Mover dos archivos es más barato y más honesto que hacer
que la API linkee el worker entero para usar una clase de servicio.

El punto de diseño delicado es el **override manual**. `PhaseOverride` y `PhaseTransition` vienen
diciendo desde el spec 04 que la validación de legalidad del salto "es del spec 08, al escribir el
override por HTTP": el entity acepta cualquier fase de `{Coexistence, Deprecated, Clean}` porque el
operador es la autoridad, pero un `POST` que lleve `Coexistence → Clean` es casi siempre un dedazo,
no una decisión. La API lo rechaza con `409` y ofrece `?force=true` para el caso real en que el
operador está **corrigiendo** una fase mal inferida — que es la razón de ser del override. Rechazar
sin escape convertiría la validación en una jaula.

## Alcance

**Incluye:**

- **`src/Common/State/TemporalPatchStateStore.cs`** y **`src/Common/State/NoopDecisionSink.cs`**
  _(movidos desde `src/PatchMonitor/Services/`)_ — mismo código, namespace `Common.State`. Es un
  movimiento puro: ni una línea de lógica cambia. Se ajustan los `using` de
  `PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs` y de los tests que los nombran
  (`State/TemporalPatchStateStoreTests.cs`, `State/PatchStateRegistrationTests.cs`).

- **`src/Contracts/Monitor/IScheduleController.cs`** — el puerto de operación del Schedule, con su
  DTO de estado. Lo que el `ScheduleBootstrapper` (spec 06) no cubre:

  ```csharp
  public interface IScheduleController
  {
      /// <summary>Estado vigente del Schedule, o null si no existe todavía.</summary>
      Task<ScheduleStatus?> DescribeAsync(CancellationToken ct = default);

      /// <summary>Pausa/reanuda; false si el Schedule no existe. Idempotente.</summary>
      Task<bool> PauseAsync(string? note, CancellationToken ct = default);
      Task<bool> UnpauseAsync(string? note, CancellationToken ct = default);

      /// <summary>Dispara una corrida fuera de ciclo; false si el Schedule no existe.</summary>
      Task<bool> TriggerAsync(CancellationToken ct = default);
  }

  public sealed record ScheduleStatus(
      string ScheduleId,
      bool Paused,
      string? Note,
      TimeSpan Interval,
      DateTimeOffset? LastRunAt,
      DateTimeOffset? NextRunAt,
      int RunningCount,
      long NumActions);
  ```

  "No existe" se devuelve como `null`/`false` —nunca como excepción— reusando el criterio de
  `WorkflowValidator`: distinguir "el cluster está vivo pero eso no existe" de "el cluster es
  inalcanzable". Lo segundo sí propaga.

- **`src/Common/Temporal/ScheduleController.cs`** — `TemporalScheduleController`, implementación
  sobre `ITemporalClient.GetScheduleHandle(options.ScheduleId)`, al lado del
  `ScheduleBootstrapper` que creó ese mismo Schedule. `DescribeAsync` mapea
  `ScheduleDescription` → `ScheduleStatus`; `TriggerAsync` usa `TriggerAsync` del handle, que
  respeta la política `Overlap = Skip` que el spec 06 fijó. Captura
  `RpcException(StatusCode.NotFound)` como "no existe".

- **`src/Contracts/Api/ApiOptions.cs`** — mismo parseo tolerante "ausente / no numérico / no
  positivo ⇒ default, nunca lanza" de `DiscoveryOptions`, `PhaseOptions`, `MonitorOptions`,
  `StateOptions` y `NotificationOptions`:

  ```csharp
  public sealed record ApiOptions(int MaxListPatches, TimeSpan OverrideDefaultTtl)
  {
      public const int DefaultMaxListPatches = 100;
      public const int DefaultOverrideTtlHours = 24;

      public static ApiOptions FromEnvironment();   // API_MAX_LIST_PATCHES, API_OVERRIDE_DEFAULT_TTL_HOURS
  }
  ```

- **`src/Contracts/Api/PatchResponses.cs`** — los DTOs de salida, con factories `FromState` al
  estilo de `VerdictChangeNotification.FromState` (spec 07). El resumen **no** lleva `History`: un
  listado de 100 patches no puede arrastrar 100 ring buffers.

  ```csharp
  public sealed record PatchSummaryResponse(
      string Namespace, string WorkflowType, string PatchId,
      PatchPhase Phase, PhaseSource Source, GateOutcome? Outcome, PatchPhase? NextPhase,
      int BlockingExecutionCount, bool HasOverride, int Revision,
      DateTimeOffset? LastObservedAt, DateTimeOffset? LastChangedAt)
  {
      public static PatchSummaryResponse FromState(PatchState state);
      public static PatchSummaryResponse Unreadable(PatchKey key, string reason);
  }

  public sealed record PatchDetailResponse(
      PatchSummaryResponse Summary, string PhaseReason,
      PhaseVerdict? LastVerdict, PhaseVerdict? PreviousVerdict,
      PhaseOverride? Override, int AssessmentCount, int NotifiedRevision,
      IReadOnlyList<PatchStateChange> History)
  {
      public static PatchDetailResponse FromState(PatchState state);
  }

  public sealed record PatchListResponse(
      int Count, bool Truncated, IReadOnlyList<PatchSummaryResponse> Patches);
  ```

  `Unreadable` cubre la key que está en el registry pero cuyo entity no responde: entra al listado
  con `Phase = Unknown` y la razón, en vez de tirar abajo el request entero.

- **`src/Contracts/Api/SetOverrideRequest.cs`** — el único DTO de entrada:

  ```csharp
  public sealed record SetOverrideRequest(
      PatchPhase Phase, string DeclaredBy, DateTimeOffset? ExpiresAt);
  ```

  `ExpiresAt` ausente ⇒ `DeclaredAt + ApiOptions.OverrideDefaultTtl`. Un override permanente sigue
  siendo posible pasando un `ExpiresAt` lejano explícito; lo que no es posible es declararlo
  permanente **por olvido**, que es el riesgo que `PhaseOverride` documenta ("un override permanente
  olvidado congelaría el monitoreo del patch").

- **`src/MonitorApi/Endpoints/PatchEndpoints.cs`** — cuatro handlers `static` que reciben sus
  dependencias por parámetro y devuelven `IResult` construido con `TypedResults` (no `Results`), para
  que los tests puedan castear a `Ok<PatchDetailResponse>` y leer `.Value` sin deserializar nada:

  | Handler              | Ruta                                             | Respuestas                        |
  | -------------------- | ------------------------------------------------ | --------------------------------- |
  | `ListAsync`          | `GET /patches`                                   | `200 PatchListResponse`           |
  | `GetAsync`           | `GET /patches/{ns}/{type}/{patchId}`             | `200 PatchDetailResponse` · `404` |
  | `SetOverrideAsync`   | `POST /patches/{ns}/{type}/{patchId}/override`   | `200` · `400` · `404` · `409`     |
  | `ClearOverrideAsync` | `DELETE /patches/{ns}/{type}/{patchId}/override` | `200 PatchDetailResponse` · `404` |

  `SetOverrideAsync` valida en este orden: `Phase == Unknown` o `DeclaredBy` vacío ⇒ `400`;
  `ExpiresAt` ya pasado ⇒ `400`; el entity no existe (`GetStateAsync` devuelve `null`) ⇒ `404`;
  `PhaseTransition.IsLegal(state.Phase, request.Phase)` falso **y** `request.Phase != state.Phase`
  **y** sin `?force=true` ⇒ `409` con el detalle de la transición rechazada; si no, `SetOverrideAsync`
  del store y `200` con el detalle resultante. Redeclarar la **misma** fase no es un salto y nunca
  da `409`.

- **`src/MonitorApi/Endpoints/ScheduleEndpoints.cs`** — cuatro handlers más sobre
  `IScheduleController`. `503` (no `404`) cuando el Schedule no existe: significa que el worker
  nunca arrancó, que es una falla de disponibilidad del sistema, no un recurso ausente.

  | Handler         | Ruta                     | Respuestas                        |
  | --------------- | ------------------------ | --------------------------------- |
  | `DescribeAsync` | `GET /schedule`          | `200 ScheduleStatus` · `503`      |
  | `PauseAsync`    | `POST /schedule/pause`   | `200 ScheduleStatus` · `503`      |
  | `UnpauseAsync`  | `POST /schedule/unpause` | `200 ScheduleStatus` · `503`      |
  | `TriggerAsync`  | `POST /schedule/trigger` | `202 { triggered: true }` · `503` |

  `pause`/`unpause` aceptan una `note` opcional por query string y devuelven el `ScheduleStatus`
  ya actualizado. `trigger` responde `202` sin esperar la pasada: la corrida se observa después por
  `GET /patches` o en la UI de Temporal.

- **`src/MonitorApi/Infrastructure/ServiceCollectionExtensions.cs`** — `AddMonitorApiServices()`,
  espejo del `AddPatchMonitorServices()` del worker: `StateOptions`, `MonitorOptions` y `ApiOptions`
  desde el entorno, `IDecisionSink → NoopDecisionSink`,
  `IPatchStateStore → TemporalPatchStateStore`, `IScheduleController → TemporalScheduleController`.
  El `Lazy<Task<ITemporalClient>>` que el store espera se arma **envolviendo el `TemporalClient`
  singleton que la API ya conecta de forma ansiosa** (`Task.FromResult`), sin abrir una segunda
  conexión ni tocar el `/health` existente.

- **`src/MonitorApi/MonitorApi.csproj`** — sin cambios de referencias: `Common` + `Contracts` ya
  alcanzan, que es justamente el punto de mover el store a `Common`.

- **`src/MonitorApi/Program.cs`** _(modificado)_ — suma `AddMonitorApiServices()` y el cableado de
  las ocho rutas nuevas a los handlers, con `.WithName()`/`.WithTags()` para que Swagger las agrupe.
  `/health` y `/health/workflow` quedan intactos.

- **`docker/docker-compose.yml`** _(modificado)_ — el servicio `monitor-api` gana las env vars que
  ahora necesita: `PATCH_STATE_CAN_THRESHOLD`, `PATCH_STATE_HISTORY_LIMIT` (el store arranca
  entities), `MONITOR_SCHEDULE_ID`, `API_MAX_LIST_PATCHES`, `API_OVERRIDE_DEFAULT_TTL_HOURS`.

- **`test/PatchMonitor.Tests/PatchMonitor.Tests.csproj`** _(modificado)_ — `ProjectReference` a
  `MonitorApi.csproj` más `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, necesario
  para que el proyecto de tests (SDK no-Web) pueda usar `IResult` y los tipos de
  `Microsoft.AspNetCore.Http.HttpResults`.

- **`test/PatchMonitor.Tests/Api/`** — `ApiOptionsTests.cs`, `PatchResponsesTests.cs` (los
  `FromState`), `PatchEndpointsTests.cs`, `ScheduleEndpointsTests.cs`, `FakeScheduleController.cs`,
  `ApiRegistrationTests.cs`. Todos puros, sin host HTTP y sin Docker. `FakePatchStateStore.cs`
  (`test/PatchMonitor.Tests/Monitor/`) se extiende con lo que los endpoints ejercitan (listado
  sembrado, override, y una key que falle al leerse para el caso `Unreadable`).

**No incluye (fuera de alcance de este spec):**

- **Cualquier tipo de autenticación o autorización.** Ni API key, ni JWT, ni middleware. La API no
  se publica fuera de la red de Docker y ni el README ni `Construction.md` la piden; agregarla es
  aditivo. Queda anotada en `## Riesgos identificados`.
- **Un frontend.** `Construction.md` §3 lo declara fuera de alcance del proyecto entero; Swagger UI
  (ya habilitado desde el spec 01) es la interfaz de exploración.
- **Endpoints sobre notificaciones**: ni historial de avisos enviados, ni un `POST` de prueba del
  webhook. El spec 07 los dejó explícitamente para "spec 08, si llega a hacer falta"; con el log
  estructurado y `NotifiedRevision` visible en `GET /patches/...` no hace falta todavía.
- **Un endpoint que exponga "qué destino de notificación falló y por qué"** — la observación no
  bloqueante del spec 07. Sigue sin consumidor real; cuando exista, es un cambio en
  `CompositeNotifier` + `MonitorRunSummary`, no en la API.
- **Disparar `MonitorWorkflow` directo y esperar el `MonitorRunSummary`.** El chequeo on-demand va
  por el Schedule (`TriggerAsync`, `202`) para no saltearse el `Overlap = Skip` ni dejar la request
  colgada lo que dure una pasada.
- **Modificar el Schedule por HTTP** (cambiar el intervalo, la catchup window o el tope de patches).
  Eso es configuración del despliegue, vía env vars del worker; un `PATCH /schedule/spec` abriría
  una superficie de escritura que nadie pidió.
- **Paginación real del listado** (cursor, `page`/`size`). El tope + `truncated` alcanza para el
  orden de magnitud del registry; un cursor sin un caso que lo exija es complejidad especulativa.
- **Borrar un patch del registry o su entity.** No hay caso de uso y sería la única operación
  destructiva de toda la API.
- **Aplicar `Workflow.Patched` por los cambios de este spec.** No hay cambios en el código de
  ningún workflow: solo lecturas, updates ya existentes y operaciones de Schedule. El ciclo de
  versionado del propio monitor sigue siendo el spec 10.

## Modelo de datos

Lo nuevo de `Contracts` vive en `namespace Contracts.Api`, salvo `IScheduleController` /
`ScheduleStatus`, que van a `Contracts.Monitor` junto a `MonitorOptions` porque son del Schedule, no
de HTTP. Árbol tras este spec (solo lo que cambia):

```text
proyecto_monitoreo/
├── src/
│   ├── Contracts/
│   │   ├── Api/
│   │   │   ├── ApiOptions.cs
│   │   │   ├── PatchResponses.cs
│   │   │   └── SetOverrideRequest.cs
│   │   └── Monitor/
│   │       └── IScheduleController.cs            (+ ScheduleStatus)
│   ├── Common/
│   │   ├── Temporal/ScheduleController.cs
│   │   └── State/
│   │       ├── TemporalPatchStateStore.cs        (movido desde PatchMonitor/Services)
│   │       └── NoopDecisionSink.cs               (movido desde PatchMonitor/Services)
│   ├── PatchMonitor/
│   │   └── Infrastructure/ServiceCollectionExtensions.cs   (modificado: usings)
│   └── MonitorApi/
│       ├── Endpoints/
│       │   ├── PatchEndpoints.cs
│       │   └── ScheduleEndpoints.cs
│       ├── Infrastructure/ServiceCollectionExtensions.cs
│       └── Program.cs                                       (modificado)
├── docker/docker-compose.yml                                (modificado)
└── test/
    └── PatchMonitor.Tests/
        ├── PatchMonitor.Tests.csproj                        (modificado)
        ├── Monitor/FakePatchStateStore.cs                   (modificado)
        └── Api/
            ├── ApiOptionsTests.cs
            ├── PatchResponsesTests.cs
            ├── PatchEndpointsTests.cs
            ├── ScheduleEndpointsTests.cs
            ├── FakeScheduleController.cs
            └── ApiRegistrationTests.cs
```

Superficie HTTP completa tras este spec:

| Método   | Ruta                                             | Qué hace                                  |
| -------- | ------------------------------------------------ | ----------------------------------------- |
| `GET`    | `/health`                                        | (spec 01) reachability del cluster        |
| `POST`   | `/health/workflow`                               | (spec 01) arranca `HealthWorkflow`        |
| `GET`    | `/patches`                                       | lista patches conocidos con su fase       |
| `GET`    | `/patches/{ns}/{type}/{patchId}`                 | estado durable completo de un patch       |
| `POST`   | `/patches/{ns}/{type}/{patchId}/override?force=` | declara override manual de fase           |
| `DELETE` | `/patches/{ns}/{type}/{patchId}/override`        | quita el override vigente                 |
| `GET`    | `/schedule`                                      | estado del Schedule (pausado, próx. tick) |
| `POST`   | `/schedule/pause?note=`                          | pausa el monitoreo                        |
| `POST`   | `/schedule/unpause?note=`                        | lo reanuda                                |
| `POST`   | `/schedule/trigger`                              | chequeo on-demand (`202`)                 |

Env vars nuevas que lee `ApiOptions.FromEnvironment()`:

| Variable                         | Default | Significado                                                             |
| -------------------------------- | ------- | ----------------------------------------------------------------------- |
| `API_MAX_LIST_PATCHES`           | `100`   | Tope de patches enriquecidos por `GET /patches`; el resto, `truncated`. |
| `API_OVERRIDE_DEFAULT_TTL_HOURS` | `24`    | Vencimiento aplicado cuando el `POST` de override omite `ExpiresAt`.    |

### Por qué el store se muda a `Common`

`TemporalPatchStateStore` no toca ni un tipo del worker: depende de `Temporalio.Client`, de los
contratos de `Contracts` y del `WorkflowValidator` de `Common`. Que haya nacido en
`PatchMonitor/Services/` fue una consecuencia del spec 05 —su único consumidor entonces era el
worker—, no una decisión de capas. Con dos consumidores, `Common` es el lugar que `Construction.md`
§5 ya define para exactamente esto. La alternativa —`MonitorApi` referenciando el `Exe`
`PatchMonitor`— haría que la API linkeara workflows y activities que nunca ejecuta, y dejaría el
grafo de dependencias mintiendo sobre quién necesita a quién.

## Plan de implementación

1. **Mover el store y el sink a `Common`.** `TemporalPatchStateStore.cs` y `NoopDecisionSink.cs` de
   `src/PatchMonitor/Services/` a `src/Common/State/`, namespace `Common.State`; ajustar los `using`
   de `PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs` y de los dos tests que los
   nombran. Refactor puro: `dotnet build` y `dotnet test` quedan exactamente igual de verdes, sin un
   solo test nuevo.

2. **`IScheduleController` + `ScheduleStatus` + `TemporalScheduleController`.** El puerto y el DTO
   en `Contracts/Monitor/IScheduleController.cs`; la implementación en
   `Common/Temporal/ScheduleController.cs`, al lado del `ScheduleBootstrapper`. Confirmar las firmas
   exactas de `GetScheduleHandle` / `DescribeAsync` / `PauseAsync` / `UnpauseAsync` / `TriggerAsync`
   contra `Temporalio` 1.9.0 antes de escribirlas. Sin tests automatizados propios: el entorno de
   time-skipping no soporta Schedules, así que se verifica en el paso 10.

3. **`ApiOptions` + tests.** `Contracts/Api/ApiOptions.cs` con el parseo tolerante.
   `ApiOptionsTests.cs`: ausentes → defaults; válidos respetados; `"basura"`, `"0"` y `"-5"` →
   default sin excepción.

4. **DTOs de respuesta y de request + tests.** `PatchResponses.cs` (con los `FromState` y
   `Unreadable`) y `SetOverrideRequest.cs`. `PatchResponsesTests.cs`: `FromState` sobre un
   `PatchState` con override y con `LastVerdict` mapea `Outcome`/`NextPhase`/`HasOverride`; el
   resumen no expone `History`; `Unreadable` deja `Phase = Unknown` conservando la key.

5. **`PatchEndpoints`: listado y detalle + tests.** `ListAsync` (una query por key vía
   `GetStateAsync`, tope de `ApiOptions.MaxListPatches`, `truncated`, key ilegible → `Unreadable`) y
   `GetAsync` (`404` si el entity no existe). `PatchEndpointsTests.cs` con `FakePatchStateStore`:
   registry vacío → `200` con `Count = 0`; tres patches → tres resúmenes; tope en 2 con tres
   patches → `Truncated == true` y dos elementos; una key que tira al leerse → entra como `Unknown`
   y las otras siguen; `GetAsync` de un patch inexistente → `NotFound`.

6. **`PatchEndpoints`: override + tests.** `SetOverrideAsync` con el orden de validación del
   Alcance y `ClearOverrideAsync`. Tests: `Phase = Unknown` → `400`; `DeclaredBy` vacío → `400`;
   `ExpiresAt` en el pasado → `400`; patch inexistente → `404`; `Coexistence → Deprecated` → `200` y
   el store recibió el `PhaseOverride` con el `ExpiresAt` por default aplicado; `Coexistence →
Clean` → `409` sin tocar el store; el mismo caso con `force: true` → `200`; redeclarar la misma
   fase vigente → `200` (no `409`); `ClearOverrideAsync` sobre un patch sin override → `200`
   idempotente, sobre uno inexistente → `404`.

7. **`ScheduleEndpoints` + tests.** Los cuatro handlers sobre `IScheduleController`.
   `FakeScheduleController.cs` (configurable como "existe" / "no existe", registrando las llamadas).
   `ScheduleEndpointsTests.cs`: `GET /schedule` con Schedule presente → `200` con el `ScheduleStatus`;
   ausente → `503`; `pause` → `200` con `Paused == true` y la nota registrada; `unpause` → `Paused
== false`; `trigger` → `202` y una única invocación de `TriggerAsync`; los tres con Schedule
   ausente → `503`.

8. **DI de la API + cableado en `Program.cs` + `ApiRegistrationTests`.**
   `MonitorApi/Infrastructure/ServiceCollectionExtensions.cs` con `AddMonitorApiServices()` (el
   `Lazy<Task<ITemporalClient>>` envolviendo el singleton ya conectado); las ocho rutas mapeadas a
   los handlers en `Program.cs` con `WithName`/`WithTags`. `ApiRegistrationTests.cs`: un
   `ServiceProvider` de `AddMonitorApiServices()` resuelve `IPatchStateStore`,
   `IScheduleController`, `ApiOptions`, `StateOptions` y `MonitorOptions`. `dotnet build` y
   `dotnet test` en verde.

9. **Compose y Dockerfile.** Sumar las cinco env vars al servicio `monitor-api`; confirmar que
   `docker/Dockerfile.MonitorApi` ya copia `Common` y `Contracts` (lo hace) y que la imagen sigue
   construyendo con los archivos nuevos.

10. **Verificación end-to-end manual.** Con el stack de `docker/` arriba y al menos un patch ya
    descubierto por una pasada previa:
    - `dotnet build PatchMonitor.sln` → 0 errores, 0 advertencias; `dotnet test PatchMonitor.sln` →
      todos verdes con el stack de Docker apagado.
    - `docker compose build monitor-api patch-monitor-worker && docker compose up -d` → los cinco
      servicios arriba; `http://localhost:5100/swagger` muestra las diez rutas agrupadas.
    - `GET /patches` devuelve los patches conocidos con su fase; `GET /patches/{...}` de uno de
      ellos coincide con lo que `temporal workflow query ... GetState` reporta para el mismo patch.
    - `POST /schedule/pause` → `GET /schedule` muestra `paused: true`, y pasados >5 minutos no
      aparece ninguna corrida nueva de `MonitorWorkflow` en la UI de Temporal.
    - `POST /schedule/trigger` con el Schedule **pausado** → `202` y una corrida nueva visible en la
      UI: el disparo manual funciona sin reanudar.
    - `POST /schedule/unpause` → `paused: false` y el tick de 5 minutos vuelve.
    - `POST /patches/{...}/override` con un salto legal → `200`, y el tick siguiente refleja
      `source: "Override"` con una notificación del spec 07 en `docker compose logs
patch-monitor-worker`.
    - El mismo `POST` con un salto ilegal → `409`; repetido con `?force=true` → `200`.
    - `DELETE /patches/{...}/override` → `200`, y el tick siguiente vuelve a `source: "Inferred"`.
    - Registrar la salida de estos comandos en este spec antes de marcar los criterios.

    **Resultado (2026-09-11):** el volumen `temporal_data` traía patches de pasadas manuales
    anteriores (`core-patch`, `probe_patch`, `probe_patch_233838`, `probe_patch_233951`), lo que
    permitió un ciclo end-to-end real sin necesidad de sembrar nada:

    - `dotnet build` → 0 errores, 0 advertencias. `dotnet test` → **253/253 verdes** con el stack
      de Docker apagado (una corrida intermedia tuvo una falla puntual y ya conocida de
      `TemporalPatchStateStoreTests`/`PatchStateWorkflowTests` por timeout del test-server de
      time-skipping bajo carga; la repetición inmediata la confirmó como flaky, ajena a este spec).
    - `docker compose build monitor-api patch-monitor-worker && docker compose up -d` → los cinco
      servicios arriba y sanos. `GET /swagger/v1/swagger.json` expone exactamente **10 rutas**
      (4 `GET`, 5 `POST`, 1 `DELETE`).
    - `GET /patches` → `200` con los 4 patches reales del registry, sin `History` en los resúmenes.
    - `GET /patches/default/OrderWorkflow/core-patch` → `200` con el detalle completo (`History`,
      veredictos, `PhaseReason`); `GET` de un patch inventado → `404`.
    - `POST .../core-patch/override` `{Coexistence→Deprecated}` → `200`, con `ExpiresAt` en
      `DeclaredAt + 24h` (el default de `API_OVERRIDE_DEFAULT_TTL_HOURS`).
    - `POST .../probe_patch/override` `{Coexistence→Clean}` (salto ilegal) → `409` sin tocar el
      store; repetido con `?force=true` → `200`.
    - Redeclarar la fase vigente (`Clean→Clean` sobre `probe_patch`) → `200`, no `409`.
    - `DELETE .../probe_patch/override` → `200` con `override: null`; repetido → `200` idempotente;
      `DELETE` sobre un patch inexistente → `404`.
    - `POST .../core-patch/override` con `Phase: Unknown`, con `DeclaredBy` vacío y con `ExpiresAt`
      en el pasado → `400` en los tres casos, sin escribir nada.
    - `GET /schedule` → `200` con `paused/interval/lastRunAt/nextRunAt/numActions` reales.
      `POST /schedule/pause?note=mantenimiento-e2e` → `200` con `paused: true` y la nota
      registrada. `POST /schedule/trigger` **con el Schedule pausado** → `202` y `numActions` subió
      de 14 a 15 con una corrida real de `MonitorWorkflow` (confirmada en
      `docker compose logs patch-monitor-worker`, incluida la notificación del spec 07 con
      `"Reason":"Override de e2e-test..."` para `core-patch`, `source: Override` reflejado en el
      siguiente `GET`). `POST /schedule/unpause` → `200` con `paused: false`.
    - `DELETE .../core-patch/override` + `POST /schedule/trigger` → el tick siguiente devolvió
      `core-patch` con `source: 0` (Inferred) de nuevo, confirmando el ida y vuelta completo.
    - `POST /health/workflow` (spec 01) → `200` sin cambios de comportamiento.
    - Stack bajado con `docker compose down` al terminar; `dotnet test` repetido en verde
      (253/253) con Docker apagado.

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa con el stack de Docker **apagado**.
- [x] `MonitorApi.csproj` sigue referenciando solo `Common` y `Contracts` (no `PatchMonitor`).
- [x] Mover el store y el sink a `Common` no cambia ninguna aserción de los tests preexistentes.
- [x] `GET /patches` devuelve un `PatchListResponse` sin `History` en los resúmenes, acotado por
      `API_MAX_LIST_PATCHES`, con `Truncated == true` cuando el registry lo supera.
- [x] Una key del registry cuyo entity no se pueda leer aparece en el listado con `Phase = Unknown`
      y no hace fallar el request.
- [x] `GET /patches/{ns}/{type}/{patchId}` devuelve `404` para un patch desconocido y el estado
      completo —incluida `History` y el override— para uno conocido.
- [x] Un `POST` de override con fase `Unknown`, `DeclaredBy` vacío o `ExpiresAt` ya pasado devuelve
      `400` sin escribir nada.
- [x] Un `POST` de override con una transición ilegal según `PhaseTransition.IsLegal` devuelve `409`
      sin escribir nada, y `200` si se repite con `?force=true`.
- [x] Redeclarar la fase que el patch ya tiene devuelve `200`, no `409`.
- [x] Un `POST` de override sin `ExpiresAt` persiste un `PhaseOverride` con vencimiento
      `DeclaredAt + API_OVERRIDE_DEFAULT_TTL_HOURS`.
- [x] `DELETE` del override es idempotente (`200` incluso si no había override) y `404` si el patch
      no existe.
- [x] `GET /schedule` refleja `paused` y el próximo tick; `POST /schedule/pause` detiene los ticks y
      `unpause` los devuelve.
- [x] `POST /schedule/trigger` devuelve `202` y produce una corrida de `MonitorWorkflow` incluso con
      el Schedule pausado.
- [x] Los cuatro endpoints de `/schedule` devuelven `503` cuando el Schedule no existe (verificado
      con `FakeScheduleController` en `ScheduleEndpointsTests`; no hay forma de tumbar el Schedule
      real sin romper el resto del stack en la corrida manual).
- [x] `/health` y `/health/workflow` del spec 01 siguen respondiendo igual.
- [x] Un `ServiceProvider` de `AddMonitorApiServices()` resuelve `IPatchStateStore`,
      `IScheduleController` y `ApiOptions`.
- [x] Los contratos de los specs 02 a 07 no cambian: este spec solo agrega tipos nuevos y mueve dos
      archivos de proyecto.
- [x] El servicio `monitor-api` declara las cinco env vars nuevas y la imagen construye.

## Decisiones tomadas y descartadas

- **Sí:** mover `TemporalPatchStateStore` y `NoopDecisionSink` a `Common.State`. No dependen de nada
  del worker y ahora tienen dos consumidores; `Construction.md` §5 define `Common` exactamente como
  el plumbing compartido de Temporal. Es un movimiento de archivos, sin cambios de lógica.
- **No:** que `MonitorApi` referencie `PatchMonitor.csproj`. La API linkearía el `Exe` del worker
  completo —workflows, activities, su DI— para usar una clase de servicio, y el grafo de
  dependencias quedaría mintiendo.
- **No:** un proyecto `Infrastructure` nuevo para los adaptadores. Rompería el mapa de cuatro
  proyectos que `Construction.md` §5 fija y que el repo de referencia replica, a cambio de una
  pureza de capas que `Common` ya da.
- **No:** que la API hable con Temporal por su cuenta, sin `IPatchStateStore`. Duplicaría el camino
  `EnsureEntityAsync` + `WorkflowValidator` que el spec 05 ya resolvió y probó, con dos
  implementaciones de la misma semántica divergiendo con el tiempo.
- **Sí:** rutas con los tres segmentos de la `PatchKey` en el path. Legible, REST-idiomático y
  reversible a `PatchKey` sin pérdida (a diferencia del `workflowId`, cuyo saneo es irreversible).
- **Sí:** legalidad del override validada con `PhaseTransition.IsLegal` (`409`) más `?force=true`
  como escape explícito. Cierra el pendiente que `PhaseOverride` y `PhaseTransition` venían
  declarando desde el spec 04, sin quitarle al operador la capacidad de corregir una fase mal
  inferida — que es la razón de ser del override.
- **No:** legalidad estricta sin escape. Dejaría al operador sin forma de arreglar por HTTP un
  estado que el monitor infirió mal, justo el caso que el override existe para cubrir.
- **Sí:** `404` si el patch no existe al declarar un override, en vez de crear el entity.
  `IPatchStateStore.SetOverrideAsync` usa `EnsureEntityAsync`, que **crea** la ejecución: un `POST`
  con un `patchId` mal tipeado dejaría un entity fantasma y una entrada basura en el registry, para
  siempre. Un override es una corrección sobre algo que el monitor ya vio.
- **Sí:** `ExpiresAt` opcional con default de `API_OVERRIDE_DEFAULT_TTL_HOURS`. Un override
  permanente sigue siendo declarable con un `ExpiresAt` lejano explícito; lo que no se puede es
  dejarlo permanente por olvido, el riesgo que `PhaseOverride` documenta.
- **No:** `ExpiresAt` obligatorio (`400` si falta). Encarece el caso común sin eliminar ningún
  riesgo que el default no cubra.
- **Sí:** chequeo on-demand vía `ScheduleHandle.TriggerAsync`, con `202`. Respeta el `Overlap =
Skip` del spec 06, no deja la request colgada lo que dure una pasada, y es el
  `TriggerImmediatelyAsync` que `Construction.md` §7 nombra para este spec.
- **No:** arrancar un `MonitorWorkflow` ad-hoc y esperar su `MonitorRunSummary`. Se saltearía la
  política de overlap del Schedule y podría solaparse con el tick regular.
- **Sí:** DTOs propios en `Contracts.Api`, con el resumen sin `History`. Desacopla el contrato HTTP
  de la forma interna del estado durable y evita que un listado de 100 patches arrastre 100 ring
  buffers.
- **No:** serializar `PatchState` crudo. Cualquier cambio interno del estado —como el
  `NotifiedRevision` que agregó el spec 07— rompería el contrato HTTP sin que nadie lo decida.
- **Sí:** listado enriquecido con tope y `truncated`, y `Unreadable` para la key que no responde.
  Cumple "listar patches y su fase" de `Construction.md` §7 con un costo acotado y sin que un entity
  caído tire abajo el endpoint.
- **No:** paginación con cursor. Complejidad especulativa para un registry que hoy se mide en
  decenas.
- **Sí:** handlers `static` extraídos a `Endpoints/*.cs`, construidos con `TypedResults`, testeados
  invocándolos directo. Tests puros, sin `WebApplicationFactory`, sin stubear el `TemporalClient`
  ansioso del arranque, y consistentes con el resto de la suite.
- **No:** tests de integración HTTP con `Microsoft.AspNetCore.Mvc.Testing`. Sumaría un paquete y
  exigiría interceptar la conexión ansiosa del `TemporalClient` a cambio de cubrir el ruteo, que el
  paso 10 verifica a mano igual.
- **No:** autenticación. Ni el README ni `Construction.md` la piden, la API no sale de la red de
  Docker, y el repo de referencia expone su `OrderApi` igual de abierto. Es aditiva; queda como
  riesgo anotado.
- **No:** endpoints de notificaciones (historial, prueba del webhook) ni exponer qué destino del
  fan-out falló. El spec 07 los dejó condicionados a "si llega a hacer falta", y sigue sin haber un
  consumidor real de ese dato.

## Riesgos identificados

| Riesgo                                                                                                                                   | Mitigación                                                                                                                                                                                          |
| ---------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| La API queda sin autenticación y expone cuatro escrituras (override ×2, pause/unpause, trigger) a cualquiera que alcance el puerto 5100. | Aceptado por decisión: no se publica fuera de la red de Docker. Si alguna vez sale a una red compartida, el middleware de API key es aditivo y no cambia ningún handler.                            |
| Mover el store a `Common` rompe `using` en el worker o en los tests y deja la rama sin compilar a mitad del spec.                        | Es el paso 1, aislado y sin lógica nueva: `dotnet build` + `dotnet test` verdes son la condición para pasar al paso 2.                                                                              |
| `GET /patches` hace una query por patch: con un registry grande el endpoint se vuelve lento.                                             | Acotado por `API_MAX_LIST_PATCHES` (default 100) con `truncated` explícito en la respuesta, en vez de degradar en silencio.                                                                         |
| Un `POST` de override con un `patchId` mal tipeado crearía un entity fantasma vía `EnsureEntityAsync`.                                   | El handler exige que `GetStateAsync` devuelva un estado antes de escribir; si no, `404` sin tocar el store.                                                                                         |
| El Schedule queda pausado por HTTP y nadie lo reanuda: el monitoreo se detiene en silencio.                                              | `GET /schedule` expone `paused` y la nota de quién lo pausó, y `pause`/`unpause` devuelven el estado resultante. Una alerta sobre el Schedule pausado sería un `INotifier` más, fuera de este spec. |
| Las firmas de Schedule de `Temporalio` 1.9.0 difieren de lo asumido y el paso 2 no compila.                                              | El paso 2 empieza confirmando las firmas reales contra el paquete antes de escribir el adaptador; el puerto `IScheduleController` no cambia por eso.                                                |
| El proyecto de tests (SDK no-Web) no compila al usar `IResult` de `Microsoft.AspNetCore.Http`.                                           | `<FrameworkReference Include="Microsoft.AspNetCore.App" />` en `PatchMonitor.Tests.csproj`, declarado en el Alcance como parte del paso 5.                                                          |

## Qué NO entra en este spec

- Autenticación, autorización o rate limiting de la API.
- Un frontend propio (Swagger UI es la única interfaz de exploración).
- Endpoints sobre notificaciones: historial de avisos, prueba de webhook, o qué destino del fan-out falló.
- Disparar `MonitorWorkflow` directo y esperar su `MonitorRunSummary`.
- Modificar la configuración del Schedule por HTTP (intervalo, catchup window, tope de patches).
- Paginación con cursor del listado de patches.
- Borrar un patch del registry o su entity workflow.
- Aplicar `Workflow.Patched` / `DeprecatePatch` por los cambios de este spec.

Cada uno, si entra, va en su propio spec.
