# 03 - Descubrimiento de patches en dos niveles

**Estado:** Implementado
**Depende de:** [02-patch-lifecycle-domain-model.md](02-patch-lifecycle-domain-model.md)
**Fecha:** 2026-09-10

**Objetivo:** Descubrir qué `patchId` hay en juego en un namespace de Temporal y producir, por cada
uno, el `ExecutionSnapshotSet` que los gates del spec 02 evalúan — con `MarkerPresence` ya resuelto,
tier 1 sobre el search attribute `TemporalChangeVersion` y fallback a escaneo de Event History.

## Por qué existe este spec

El spec 02 dejó los dos gates (`CoexistenceToDeprecatedGate`, `DeprecatedToCleanGate`) y el
`PhaseEvaluator` como funciones puras sobre una lista de `ExecutionSnapshot`, pero nadie llena esa
lista con datos reales: `src/Contracts/Domain/` no tiene un solo `using Temporalio`. Este spec es
**el único punto que habla con Temporal como fuente de datos** (`Construction.md` §7): lista
ejecuciones, lee historias y arma los `ExecutionSnapshotSet` que el spec 06 va a pasarle al
evaluador.

El descubrimiento es en **dos niveles** por la restricción #1 de `Construction.md` §4. El search
attribute `TemporalChangeVersion` es un `KeywordList` que `Workflow.Patched` escribe solo
(`<patchId>-<version>`), pero consultarlo con una query compuesta por _standard visibility_ sobre
Postgres —el stack del repo de referencia— da `context deadline exceeded` de forma reproducible. Lo
que sí responde es el listado con filtros simples (`ExecutionStatus='Running'`, `--limit N`) y
después `FetchHistoryAsync` por ejecución (restricción #5). De ahí el diseño: **tier 1** lee el
atributo `TemporalChangeVersion` que ya viene en la respuesta de `ListWorkflowsAsync` —sin query
compuesta— y **tier 2** cae a la Event History para cada ejecución donde el atributo no alcanza.

El tier 2 también es el que resuelve el flag `deprecated` del marker (restricción #2): el search
attribute dice que hubo un patch, pero no si está deprecado. Fase 1 y fase 2 escriben **ambas** el
marker `core_patch`; lo único que cambia es el flag interno. Por eso `IPatchDiscovery` devuelve el
`MarkerPresence` completo de cuatro valores —incluido `PresentDeprecated`— y no un booleano: quien
interpreta esos snapshots como fase 1/2/3 es el spec 04, pero los datos crudos para hacerlo salen
de acá en una sola pasada por la historia.

Ese `IsTruncated` que el spec 02 puso en `ExecutionSnapshotSet` recién en este spec tiene quién lo
ponga en `true`. El barrido tiene dos topes (`DISCOVERY_MAX_EXECUTIONS`, `DISCOVERY_MAX_HISTORIES`)
y ante un tope alcanzado o un error de RPC parcial, el conjunto sale marcado como truncado, lo que
fuerza a los gates a devolver `Inconclusive` en vez de un `Ready` falso construido sobre datos
incompletos.

## Alcance

**Incluye:**

- **`src/Contracts/Discovery/IPatchDiscovery.cs`** — el puerto que consume el spec 06:

  ```csharp
  public interface IPatchDiscovery
  {
      Task<IReadOnlyList<PatchDiscoveryResult>> DiscoverAsync(CancellationToken ct = default);
  }
  ```

- **`src/Contracts/Discovery/PatchDiscoveryResult.cs`** —
  `record PatchDiscoveryResult(PatchKey Key, ExecutionSnapshotSet Executions)`. Un elemento por
  `patchId` distinto descubierto en el namespace.
- **`src/Contracts/Discovery/IExecutionSource.cs`** — puerto **angosto** sobre Temporal, con DTOs
  propios (nada de tipos del SDK):

  ```csharp
  public interface IExecutionSource
  {
      Task<ExecutionListPage> ListExecutionsAsync(
          ExecutionListFilter filter, CancellationToken ct = default);

      Task<IReadOnlyList<PatchMarker>> ReadPatchMarkersAsync(
          string workflowId, string runId, CancellationToken ct = default);
  }
  ```

- **`src/Contracts/Discovery/ExecutionListItem.cs`** — `ExecutionListItem` (workflowId, runId,
  workflowType, `ExecutionStatus`, startTime, `IReadOnlyList<string> ChangeVersions` tomado del
  search attribute), `ExecutionListFilter` (openOnly / lookback / limit), `ExecutionListPage`
  (items + `bool LimitReached`).
- **`src/Contracts/Discovery/PatchMarker.cs`** — `record PatchMarker(string PatchId, bool Deprecated)`,
  el resultado de leer un evento `MarkerRecorded` de nombre `core_patch` en la historia.
- **`src/Contracts/Discovery/DiscoveryOptions.cs`** — `record` con `Namespace`, `LookbackDays`,
  `MaxExecutions`, `MaxHistories` y un `static DiscoveryOptions FromEnvironment()` que lee las env
  vars de la tabla del Modelo de datos con sus defaults.
- **`src/PatchMonitor/Services/TemporalExecutionSource.cs`** — implementación de `IExecutionSource`
  sobre `TemporalClient`: `ListWorkflowsAsync` con query **simple** (`ExecutionStatus='Running'` para
  las abiertas; `StartTime > <lookback>` para las cerradas), lectura del search attribute
  `TemporalChangeVersion`, `FetchHistoryAsync` + recorrido de eventos `MarkerRecorded` para extraer
  `core_patch` y su payload `deprecated`, y mapeo de `WorkflowExecutionStatus` a `ExecutionStatus`.
  **Es el único archivo nuevo con `using Temporalio`.**
- **`src/PatchMonitor/Services/PatchDiscoveryService.cs`** — implementación de `IPatchDiscovery` que
  orquesta los dos niveles: pide el listado, deriva `MarkerPresence` de cada ejecución (tier 1 por
  atributo, tier 2 por historia cuando el atributo está vacío), agrupa por `PatchKey`
  (namespace + workflowType + patchId), aplica los topes y setea `IsTruncated`.
- **`src/PatchMonitor/Activities/DiscoveryActivities.cs`** — clase `[Activity]` que envuelve
  `IPatchDiscovery.DiscoverAsync`; un fallo de configuración (namespace inexistente) se relanza como
  `ApplicationFailureException(nonRetryable: true)`, un fallo de RPC transitorio se deja propagar
  para que Temporal reintente.
- **`src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs`** _(modificado)_ —
  `AddPatchMonitorServices` registra `DiscoveryOptions` (singleton, desde `FromEnvironment()`),
  `IExecutionSource → TemporalExecutionSource`, `IPatchDiscovery → PatchDiscoveryService` y
  `DiscoveryActivities` por **tipo concreto**.
- **`src/PatchMonitor/Program.cs`** _(modificado)_ — pasa `typeof(DiscoveryActivities)` en
  `activityTypes` a `WorkerHost.RunAsync`.
- **`test/PatchMonitor.Tests/Discovery/FakeExecutionSource.cs`** — `IExecutionSource` en memoria:
  se le cargan `ExecutionListItem` y, por (workflowId, runId), una lista de `PatchMarker` o una
  excepción a lanzar.
- **`test/PatchMonitor.Tests/Discovery/HistoryFixtures.cs`** — helpers para construir ejecuciones
  sintéticas: abierta/cerrada, con atributo `TemporalChangeVersion`, con marker no deprecado, con
  marker deprecado, sin marker, con la lectura de historia rota.
- **`test/PatchMonitor.Tests/Discovery/PatchDiscoveryServiceTests.cs`** y
  **`DiscoveryOptionsTests.cs`** — tests unitarios puros, sin `Temporalio` (el `.csproj` de tests
  no gana ninguna referencia nueva).
- **`docker/docker-compose.yml`** _(modificado)_ — se agregan `DISCOVERY_LOOKBACK_DAYS`,
  `DISCOVERY_MAX_EXECUTIONS` y `DISCOVERY_MAX_HISTORIES` al `environment` de `patch-monitor-worker`.
  El namespace ya viaja en `TARGET_TEMPORAL_NAMESPACE`, que el spec 01 dejó puesto.

**No incluye (fuera de alcance de este spec):**

- **Interpretar los snapshots como fase 1 / 2 / 3.** `IPhaseResolver` y la lectura del flag
  `deprecated` _como decisión de fase_ son del spec 04. Acá el flag viaja crudo dentro de
  `MarkerPresence.PresentDeprecated`; nadie decide todavía qué fase implica.
- **El override manual de fase** declarado por el operador: spec 04.
- **Persistir lo descubierto.** `PatchStateWorkflow`, `PatchRegistryWorkflow`, `IDecisionSink` y el
  `Continue-As-New` son del spec 05. Acá `PatchDiscoveryResult` es un valor en memoria.
- **`MonitorWorkflow` y el Temporal Schedule de 5 minutos:** spec 06. Este spec entrega la Activity;
  quién la agenda es del 06.
- **`INotifier` y detectar que un veredicto cambió:** spec 07.
- **Endpoints HTTP:** spec 08.
- **Descubrir en más de un namespace por corrida.** `DiscoveryOptions.Namespace` es uno solo; una
  lista multi-namespace, si entra, es otro spec.
- **Tests con el entorno de time-skipping de `Temporalio`:** desde el spec 05. Este spec se prueba
  con el fake en memoria, sin Docker ni cluster ni descarga de test-server.

## Modelo de datos

Todo lo nuevo de `Contracts` vive en `namespace Contracts.Discovery`. Árbol tras este spec (solo lo
que cambia):

```text
proyecto_monitoreo/
├── src/
│   ├── Contracts/
│   │   └── Discovery/
│   │       ├── IPatchDiscovery.cs
│   │       ├── PatchDiscoveryResult.cs
│   │       ├── IExecutionSource.cs
│   │       ├── ExecutionListItem.cs      (ExecutionListItem + ExecutionListFilter + ExecutionListPage)
│   │       ├── PatchMarker.cs
│   │       └── DiscoveryOptions.cs
│   └── PatchMonitor/
│       ├── Services/
│       │   ├── TemporalExecutionSource.cs
│       │   └── PatchDiscoveryService.cs
│       ├── Activities/
│       │   └── DiscoveryActivities.cs
│       ├── Infrastructure/ServiceCollectionExtensions.cs   (modificado)
│       └── Program.cs                                      (modificado)
├── docker/docker-compose.yml                               (modificado)
└── test/
    └── PatchMonitor.Tests/
        └── Discovery/
            ├── FakeExecutionSource.cs
            ├── HistoryFixtures.cs
            ├── PatchDiscoveryServiceTests.cs
            └── DiscoveryOptionsTests.cs
```

Tipos centrales:

```csharp
namespace Contracts.Discovery;

public sealed record PatchDiscoveryResult(PatchKey Key, ExecutionSnapshotSet Executions);

public sealed record ExecutionListItem(
    string WorkflowId,
    string RunId,
    string WorkflowType,
    ExecutionStatus Status,
    DateTimeOffset StartTime,
    IReadOnlyList<string> ChangeVersions);   // search attribute TemporalChangeVersion, ya parseado

public sealed record ExecutionListFilter(bool OpenOnly, int LookbackDays, int Limit);

public sealed record ExecutionListPage(
    IReadOnlyList<ExecutionListItem> Items,
    bool LimitReached);

public sealed record PatchMarker(string PatchId, bool Deprecated);

public sealed record DiscoveryOptions(
    string Namespace,
    int LookbackDays,
    int MaxExecutions,
    int MaxHistories)
{
    public static DiscoveryOptions FromEnvironment();
}
```

Env vars que lee `DiscoveryOptions.FromEnvironment()`:

| Variable                    | Default   | Significado                                                         |
| --------------------------- | --------- | ------------------------------------------------------------------- |
| `TARGET_TEMPORAL_NAMESPACE` | `default` | Namespace contra el que corre el descubrimiento (ya lo puso el 01). |
| `DISCOVERY_LOOKBACK_DAYS`   | `7`       | Ventana de `StartTime` para incluir ejecuciones **cerradas**.       |
| `DISCOVERY_MAX_EXECUTIONS`  | `500`     | Tope de ejecuciones listadas por corrida.                           |
| `DISCOVERY_MAX_HISTORIES`   | `200`     | Tope de historias leídas en tier 2 por corrida.                     |

Derivación de `MarkerPresence` por ejecución (el primero que aplica gana):

| Situación de la ejecución                                                     | `MarkerPresence`    | Tier |
| ----------------------------------------------------------------------------- | ------------------- | ---- |
| El search attribute `TemporalChangeVersion` trae una entrada para el patchId  | `Present`           | 1    |
| Tier 2 lee la historia y hay marker `core_patch` con `deprecated = false`     | `Present`           | 2    |
| Tier 2 lee la historia y hay marker `core_patch` con `deprecated = true`      | `PresentDeprecated` | 2    |
| Tier 2 lee la historia completa y **no** hay ningún marker `core_patch`       | `Absent`            | 2    |
| Historia no leída (tope `MaxHistories` alcanzado) o `FetchHistoryAsync` falló | `Unknown`           | —    |

El search attribute no distingue deprecado de no deprecado: una ejecución que resuelve a `Present`
por tier 1 y de la que además interesa el flag `deprecated` **igual pasa a tier 2** para leerlo. El
tier 1 sirve para no leer historia de ejecuciones que claramente no tienen el patch, no para
saltearse la historia de las que sí.

Qué dispara `ExecutionSnapshotSet.IsTruncated = true` para un patch:

- `ExecutionListPage.LimitReached` es `true` (se alcanzó `MaxExecutions` al listar), **o**
- alguna ejecución de ese patch quedó en `MarkerPresence.Unknown` por el tope `MaxHistories` o por
  una excepción al leer su historia.

Agrupación: cada `ExecutionListItem` produce cero o más `(PatchKey, ExecutionSnapshot)` — cero si no
tiene ningún patch asociado en ningún tier. Se agrupa por `PatchKey(Namespace, WorkflowType,
PatchId)`; el `Namespace` sale de `DiscoveryOptions`, el `WorkflowType` de la ejecución.

## Plan de implementación

1. **DTOs y puertos en `Contracts`.** Crear `Discovery/IExecutionSource.cs`,
   `ExecutionListItem.cs`, `PatchMarker.cs`, `IPatchDiscovery.cs` y `PatchDiscoveryResult.cs`.
   `dotnet build` compila. Sin tests todavía; `Contracts` no gana ninguna referencia de paquete.

2. **`DiscoveryOptions` + `FromEnvironment` + tests.** Crear `Discovery/DiscoveryOptions.cs` con el
   parseo de las cuatro env vars y sus defaults (valores fuera de rango o no numéricos → default, sin
   excepción). `test/PatchMonitor.Tests/Discovery/DiscoveryOptionsTests.cs`: todas ausentes → todos
   los defaults; cada una seteada se respeta; valor basura → default. `dotnet test` en verde.

3. **`FakeExecutionSource` + `HistoryFixtures`.** Crear el fake en memoria y el builder de
   ejecuciones sintéticas para que los tests del servicio se lean como la tabla de derivación de
   `MarkerPresence`. Sin lógica de producción todavía.

4. **`PatchDiscoveryService` — tier 1.** Crear el servicio: pide el listado con
   `ExecutionListFilter`, y para cada ejecución con entradas en `ChangeVersions` emite un
   `ExecutionSnapshot` con `MarkerPresence.Present`, agrupado por `PatchKey`. Tests: dos ejecuciones
   con el mismo patchId y distinto workflowType → dos `PatchDiscoveryResult`; ejecución sin
   `ChangeVersions` → todavía no aparece.

5. **Tier 2 y fusión.** Para cada ejecución sin `ChangeVersions` (o cuando hace falta el flag
   `deprecated`), llamar `ReadPatchMarkersAsync` y derivar `Present` / `PresentDeprecated` / `Absent`
   según la tabla. Fusionar con lo de tier 1 por `PatchKey`. Tests: patch descubierto **solo** por
   historia; `deprecated = true` → `PresentDeprecated`; historia sin markers → la ejecución entra
   como `Absent` en los patches que ya existían por otras ejecuciones, pero no crea un patch nuevo.

6. **Topes e `IsTruncated`.** Aplicar `MaxExecutions` al listado (propagando `LimitReached`) y
   `MaxHistories` a las lecturas de tier 2; toda ejecución no inspeccionada queda `Unknown`. Setear
   `ExecutionSnapshotSet.IsTruncated` por patch según las reglas del Modelo de datos. Tests:
   `LimitReached = true` → todos los sets truncados; `MaxHistories` chico → los patches con
   ejecuciones sin inspeccionar salen truncados, el resto no; excepción en una lectura → esa
   ejecución `Unknown` y su patch truncado, las demás intactas.

7. **`TemporalExecutionSource` real.** Implementar `IExecutionSource` sobre `TemporalClient`:
   `ListWorkflowsAsync` con las dos queries simples (abiertas / cerradas en ventana), extracción del
   search attribute `TemporalChangeVersion`, `FetchHistoryAsync` con recorrido de eventos
   `MarkerRecorded` filtrando por nombre `core_patch` y parseo del payload `deprecated`, y el mapeo
   de estados. No hay test unitario de este archivo (se ejercita en el paso 9); el `grep` de
   `Temporalio` debe encontrarlo **solo acá** dentro de `src/PatchMonitor/`.

8. **Activity + DI + `Program.cs` + compose.** Crear `Activities/DiscoveryActivities.cs`, registrar
   en `ServiceCollectionExtensions` (`DiscoveryOptions`, `IExecutionSource`, `IPatchDiscovery`,
   `DiscoveryActivities` por tipo concreto), pasar `typeof(DiscoveryActivities)` en `Program.cs`, y
   agregar las tres env vars `DISCOVERY_*` al `patch-monitor-worker` del `docker-compose.yml`.
   `dotnet build` y `dotnet test` en verde; el worker sigue arrancando.

9. **Verificación end-to-end manual.** Con el stack de Docker arriba y `ReleaseOrderDemo` corriendo
   en su propia red (o al menos un workflow con un `Workflow.Patched` vivo en el namespace
   `default`):
   - `dotnet build PatchMonitor.sln` → 0 errores, 0 advertencias.
   - `dotnet test PatchMonitor.sln` → todos verdes, stack de Docker apagado, sin descarga de
     test-server.
   - `grep -rn "Temporalio" src/PatchMonitor/` → coincidencias **solo** en
     `Services/TemporalExecutionSource.cs`.
   - Invocar `DiscoveryActivities` una vez (script de arranque one-shot o `temporal` CLI) y
     comprobar en los logs del worker que descubre al menos un `patchId`, con al menos una ejecución
     resuelta por tier 1 (atributo) y, si hay una ejecución pre-patch viva, otra en `Absent` por
     tier 2.
   - Registrar la salida de estos comandos en este spec antes de marcar los criterios de aceptación.

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa con el stack de Docker **apagado** y sin descargar el
      binario del test-server de Temporal; `test/PatchMonitor.Tests/PatchMonitor.Tests.csproj` no
      referencia `Temporalio`. (85 tests, ~2 s, con `docker compose stop`.)
- [x] La **lógica de descubrimiento** no referencia `Temporalio`:
      `grep -rn "Temporalio\|TemporalClient" src/Contracts/Discovery/ src/PatchMonitor/Services/PatchDiscoveryService.cs`
      no devuelve nada. Dentro de `src/PatchMonitor/` el `using Temporalio` queda en
      `Services/TemporalExecutionSource.cs` (adaptador) y `Activities/DiscoveryActivities.cs`
      (la envoltura `[Activity]` necesita `Temporalio.Activities` y la
      `ApplicationFailureException` que pide la sección Alcance) — más `Workflows/HealthWorkflow.cs`,
      preexistente del spec 01. Ver Resultado de la implementación, desvío 1.
- [x] `IPatchDiscovery.DiscoverAsync` devuelve un `PatchDiscoveryResult` por cada `patchId` distinto,
      con `PatchKey` armado como `namespace` (de `DiscoveryOptions`) + `workflowType` (de la
      ejecución) + `patchId`. Verificado en vivo: `default / ProbeWorkflow / probe_patch`.
- [x] Tier 1 (search attribute) y tier 2 (Event History) cubiertos por tests distintos en el mismo
      barrido. **En vivo, contra el stack de referencia, tier 1 no aporta nada**: el
      `ListWorkflowExecutions` de la standard visibility sobre Postgres devuelve
      `SearchAttributes = null`, así que el descubrimiento degrada a tier‑2‑completo sin romperse
      (fila prevista en Riesgos identificados). Tier 2 verificado en vivo. Ver desvío 2.
- [x] Un marker `core_patch` con `deprecated = true` produce `MarkerPresence.PresentDeprecated` y uno
      con `deprecated = false` produce `Present` — test con nombre explícito para la distinción.
- [x] Una ejecución **abierta** cuya historia se leyó completa y no tiene marker `core_patch` entra
      como `MarkerPresence.Absent`; el `CoexistenceToDeprecatedGate` del spec 02 la ve como
      bloqueante.
- [x] Con `ExecutionListPage.LimitReached = true`, **todos** los `ExecutionSnapshotSet` salen con
      `IsTruncated = true`. Con `MaxHistories` alcanzado, solo los patches con alguna ejecución en
      `MarkerPresence.Unknown` salen truncados.
- [x] Una excepción al leer la historia de una ejecución deja esa ejecución en `Unknown` y su patch
      con `IsTruncated = true`, sin abortar el descubrimiento de los demás patches.
- [x] `DiscoveryOptions.FromEnvironment()` devuelve los defaults `default` / `7` / `500` / `200` con
      las env vars ausentes, y respeta cada una cuando está seteada con un valor válido.
- [x] El `patch-monitor-worker` del `docker-compose.yml` declara `DISCOVERY_LOOKBACK_DAYS`,
      `DISCOVERY_MAX_EXECUTIONS` y `DISCOVERY_MAX_HISTORIES`, y el worker arranca con
      `DiscoveryActivities` registrada (`docker compose config` válido; test de registro en DI).

## Resultado de la implementación

Implementado el 2026-09-10 en la rama `spec-03-patch-discovery-two-tier`. Suite: **85 tests**
(53 del spec 02 + 32 nuevos: `DiscoveryOptionsTests` 9, `PatchDiscoveryServiceTests` 22,
`DiscoveryRegistrationTests` 1), en verde con el stack de Docker detenido.

**Verificación end-to-end (paso 9).** Contra el stack de `docker/` con un `ProbeWorkflow`
descartable parcheado (`Workflow.Patched("probe_patch")`) y una ejecución viva en el namespace
`default`:

- `DiscoverAsync` completó en ~80 ms — **sin colgarse** — y devolvió
  `[default / ProbeWorkflow / probe_patch] IsTruncated=False snapshots=1` con la ejecución en
  `status=Running marker=Present`.
- `ReadPatchMarkersAsync("probe-live-1")` → `patchId=probe_patch deprecated=False`, leído del
  detail `patch-data` del marker `core_patch`.
- El estado inicial (sin patches vivos) devolvió `0 patch(es)` sin error.

**Desvíos respecto de la spec (resueltos durante la implementación):**

1. **`DiscoveryActivities` referencia `Temporalio`.** El criterio de aceptación decía "únicamente en
   `TemporalExecutionSource.cs`", pero la propia sección Alcance manda que la Activity relance
   `ApplicationFailureException(nonRetryable: true)` —un tipo de `Temporalio.Exceptions`— y toda
   clase `[Activity]` necesita `Temporalio.Activities`. La **lógica** de descubrimiento
   (`PatchDiscoveryService`, todo `Contracts/Discovery/`) sí queda Temporalio‑free. `HealthWorkflow`
   y `IHealthWorkflow` con `using Temporalio.Workflows` son del spec 01 y los reemplaza el spec 06.

2. **Tier 1 inerte en el backend de visibility del repo de referencia.** El `ListWorkflowExecutions`
   de la standard visibility sobre Postgres (`temporalio/auto-setup:1.23.0`, sin Elasticsearch)
   devuelve `SearchAttributes = null` en cada ejecución del listado, aunque `DescribeWorkflowExecution`
   sí trae `TemporalChangeVersion`. Es la fragilidad exacta de `Construction.md` §4 restricción #1 y
   de la fila "el namespace no tiene registrado `TemporalChangeVersion`" de Riesgos. El diseño de
   dos niveles **degrada a tier‑2‑completo sin romperse** (verificado en vivo). Recuperar tier 1
   contra un backend rico (Elasticsearch) o vía `Describe` por ejecución queda para el spec 09.

3. **El adaptador usa gRPC crudo, no el wrapper de alto nivel.**
   `client.ListWorkflowsAsync(...)` del SDK 1.9.0 no expone los search attributes de sistema, así
   que `ListExecutionsAsync` llama a `WorkflowService.ListWorkflowExecutionsAsync` y lee
   `info.SearchAttributes.IndexedFields["TemporalChangeVersion"]` (payload `json/plain` = array de
   strings). `ReadPatchMarkersAsync` ya usaba `GetWorkflowExecutionHistoryAsync` crudo.

4. **Formato real del marker `core_patch`.** El sdk-core no guarda `patch_id` + `deprecated` como
   claves separadas sino un único detail `patch-data` con payload
   `{"id":"<patchId>","deprecated":<bool>}`. `TryReadPatchData` lo parsea con `System.Text.Json`.

5. **Query de cerradas.** La standard visibility rechaza `ExecutionStatus != 'Running'` y exige
   `StartTime BETWEEN '<desde>' AND '<hasta>'` (no `>`). La segunda query quedó como
   `StartTime BETWEEN <now-lookback> AND <now+1d>`, deduplicada por `runId` contra la de abiertas.

6. **Tests del servicio reescritos para el modelo de fusión del paso 5.** Los dos casos que
   codificaban semántica de "solo tier 1" (`todavía no aparece`, `no lee la Event History`) se
   reemplazaron: el servicio lee la historia de toda ejecución para resolver el flag `deprecated`,
   y tier 1 pasó a ser la garantía de que un patch no se pierda (`Present` en vez de `Unknown`) si
   esa lectura se saltea o falla.

## Decisiones tomadas y descartadas

- **Sí:** `IPatchDiscovery` devuelve `ExecutionSnapshotSet` con `MarkerPresence` completo, incluido
  `PresentDeprecated`. Una sola pasada por la Event History; el spec 04 interpreta esos snapshots sin
  volver a leer la historia.
- **No:** que el spec 03 devuelva solo la lista de `patchId` y deje la recolección de snapshots al
  spec 04. Partiría el I/O sobre Temporal en dos specs y obligaría a escanear la misma historia dos
  veces.
- **No:** que tier 1 nunca escriba `PresentDeprecated` y el spec 04 relea la historia para el flag.
  Es la separación más estricta según `Construction.md` §7, pero el costo de un segundo escaneo de la
  Event History no lo justifica; el flag sale gratis en la misma lectura de tier 2.
- **Sí:** un namespace por env var (`TARGET_TEMPORAL_NAMESPACE`), sin filtrar por workflow type. Es
  el "apuntar a cualquier namespace sin configuración específica del proyecto observado" del criterio
  de listo de `Construction.md` §8.
- **No:** una lista de namespaces separada por comas. Obliga a un `TemporalClient` por namespace y a
  decidir cómo se fusionan resultados; si hace falta, es su propio spec.
- **No:** `TARGET_WORKFLOW_TYPES` para acotar el escaneo. Es más barato pero rompe la genericidad:
  habría que configurar el monitor por proyecto observado.
- **Sí:** barrer ejecuciones abiertas **más** cerradas dentro de `DISCOVERY_LOOKBACK_DAYS`. Un patch
  cuyas ejecuciones ya drenaron sigue apareciendo y puede llegar a fase 3; con solo las abiertas
  desaparecería del monitor justo cuando está por completarse.
- **No:** listar todo el namespace sin filtro temporal. El tope se consumiría con historia vieja e
  `IsTruncated` quedaría casi siempre en `true`.
- **Sí:** puerto angosto `IExecutionSource` con DTOs propios y adaptador `TemporalExecutionSource` en
  `PatchMonitor/Services`. Los tests usan un fake en memoria con historia sintética y el proyecto de
  tests no gana `Temporalio` (`Construction.md` §7: spec 03 = unitarios puros).
- **No:** mockear los tipos del SDK `Temporalio` directo. El SDK expone clases selladas, no
  interfaces: frágil y a veces imposible.
- **No:** probar el spec 03 contra el test-server de time-skipping. Adelanta al 03 lo que
  `Construction.md` pone recién en el 05 y mete descarga de binario y Docker en una suite que ahora
  corre en segundos.
- **Sí:** dos topes separados (`MaxExecutions` para el listado, `MaxHistories` para las historias de
  tier 2). Un único tope global dejaría que una corrida gaste todo el presupuesto en historias sin
  terminar de listar.
- **No:** un tope por tiempo (`DISCOVERY_BUDGET_SECONDS`). Se ajustaría solo a la ventana de 5
  minutos del Schedule, pero vuelve los tests no determinísticos.
- **Sí:** entregar la clase `[Activity]` (`DiscoveryActivities`) además del servicio, registrada en
  DI por tipo concreto como en el repo de referencia. El spec 06 solo la invoca; ya queda
  verificable desde el worker en este spec.
- **No:** dejar la envoltura `[Activity]` para el spec 06. Sin ella no hay forma de ejercitar el
  descubrimiento contra el cluster hasta el 06.

## Riesgos identificados

| Riesgo                                                                                                                                 | Mitigación                                                                                                                                                                                                                |
| -------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Caer sin querer en una query compuesta sobre `TemporalChangeVersion` (`KeywordList`) y colgar el descubrimiento (restricción #1).      | `TemporalExecutionSource` usa solo queries simples (`ExecutionStatus`, `StartTime`); el atributo se lee del response, nunca del `--query`. Un test de revisión de código lo fija en el criterio de aceptación del `grep`. |
| El costo de RPC del tier 2 (una `FetchHistoryAsync` por ejecución sin atributo) hace que la corrida no termine en la ventana de 5 min. | Tope `MaxHistories` (default 200) con `IsTruncated` cuando se alcanza; tier 1 evita leer historia de las ejecuciones que claramente no tienen el patch.                                                                   |
| `FetchHistoryAsync` sobre una ejecución con historia enorme trae megabytes y satura memoria.                                           | Recorrer la historia como stream y cortar apenas se encontraron todos los markers `core_patch` de interés; no materializar la historia completa.                                                                          |
| Una historia truncada por retención hace ver `Absent` donde en realidad hubo un patch, y el gate 1→2 se abre de más.                   | `Absent` solo se asigna si la lectura llegó al final de la historia sin error; cualquier lectura parcial o fallida queda `Unknown`, que fuerza `Inconclusive`.                                                            |
| El namespace objetivo no tiene registrado el search attribute `TemporalChangeVersion` y tier 1 nunca aporta nada.                      | El diseño degrada a tier 2 completo sin romper; se documenta que en ese namespace el costo de RPC es mayor y conviene subir `MaxHistories`.                                                                               |
| `DiscoveryActivities` propaga un error de configuración como retryable y Temporal lo reintenta para siempre.                           | Namespace inexistente / credenciales inválidas se relanzan como `ApplicationFailureException(nonRetryable: true)`; solo los errores transitorios de RPC se dejan propagar.                                                |

## Qué NO entra en este spec

- Interpretar `MarkerPresence` como fase 1 / 2 / 3, `IPhaseResolver` y el override manual de fase.
- Persistir `PatchDiscoveryResult`: entity workflows, registry singleton, `IDecisionSink`,
  `Continue-As-New`.
- `MonitorWorkflow`, `ScheduleBootstrapper` y el Temporal Schedule de 5 minutos.
- `INotifier` y la detección de cambio de veredicto entre corridas.
- Endpoints HTTP que expongan patches, fases o veredictos.
- Descubrimiento en más de un namespace por corrida.
- Tests con el entorno de time-skipping de `Temporalio`.

Cada uno, si entra, va en su propio spec.
