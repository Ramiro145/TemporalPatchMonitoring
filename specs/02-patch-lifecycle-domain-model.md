# 02 - Modelo de dominio del ciclo de vida de un patch

**Estado:** Implementado
**Depende de:** [01-solution-scaffolding-docker-stack.md](01-solution-scaffolding-docker-stack.md)
**Fecha:** 2026-09-10

**Objetivo:** Fijar el vocabulario del ciclo de vida de un patch de Temporal (`PatchPhase`,
`PatchKey`, `ExecutionSnapshot`, `PhaseVerdict`) y los dos gates de salto de fase como código
puro sin I/O ni dependencia de Temporal, con su proyecto de tests unitarios.

## Por qué existe este spec

El spec 01 dejó cuatro proyectos que compilan y un stack que arranca, pero ni una línea de la lógica
del monitor. Este spec escribe esa lógica antes de que nada hable con Temporal, y por una razón
concreta: los criterios de salto de fase son el corazón del proyecto y son **asimétricos**
(`Construction.md` §4 restricción #3). El gate 1→2 mira ejecuciones **abiertas sin marker** —las
pre-patch, que todavía corren el código viejo—. El gate 2→3 mira que **no quede ninguna ejecución
abierta con el marker**. Son dos predicados distintos sobre el mismo conjunto de datos; escribirlos
mezclados con el código que consulta Temporal es la forma más rápida de confundirlos y no notarlo.

La restricción #2 explica por qué el vocabulario necesita más precisión que un booleano: fase 1 y
fase 2 escriben **ambas** el marker `MarkerRecorded · core_patch`. Lo único que las distingue es el
flag `deprecated` dentro del marker. Por eso `ExecutionSnapshot` no lleva un `bool HasMarker` sino un
`MarkerPresence` de cuatro valores, y por eso `Absent` y `Unknown` son cosas distintas: "inspeccioné
la historia y no hay marker" habilita el gate 1→2, mientras que "no llegué a inspeccionar esta
ejecución" no habilita nada.

Ese `Unknown` es la razón del veredicto tri-estado. El spec 03 va a inspeccionar un **tope** de
ejecuciones por corrida (restricción #5), así que el dominio tiene que poder decir "no sé" sin que
se confunda con "todavía no". Un `bool CanAdvance` colapsaría los dos casos en `false` y el monitor
terminaría reportando "bloqueado" cuando en realidad los datos estaban truncados. `GateOutcome`
tiene tres valores para que esa distinción sobreviva hasta la API (spec 08) y el notificador
(spec 07).

Todo esto vive en `Contracts` porque `Construction.md` §5 lo define como la raíz de dependencias del
resto de los proyectos, y no arrastra nada nuevo: los tipos y los gates no usan `Temporalio`, se
compilan y se testean sin cluster, sin Docker y sin red. El spec 03 en adelante llenan estos tipos
con datos reales; acá solo se fija la forma y las reglas.

## Alcance

**Incluye:**

- **`src/Contracts/Domain/PatchPhase.cs`** — `enum PatchPhase { Unknown = 0, Coexistence = 1,
Deprecated = 2, Clean = 3 }`, las tres fases de `Construction.md` §1 más el valor no resuelto.
- **`src/Contracts/Domain/PatchKey.cs`** — `record PatchKey(string Namespace, string WorkflowType,
string PatchId)` con `ToWorkflowId()`: el id determinístico y saneado que el spec 05 va a usar como
  `WorkflowId` del entity workflow (ver Modelo de datos).
- **`src/Contracts/Domain/ExecutionStatus.cs`** — `enum ExecutionStatus` con los estados de una
  ejecución de Temporal y la propiedad de extensión `IsOpen()` (`Running` y `ContinuedAsNew`
  abiertos; el resto cerrados).
- **`src/Contracts/Domain/MarkerPresence.cs`** — `enum MarkerPresence { Unknown = 0, Absent = 1,
Present = 2, PresentDeprecated = 3 }`. `Present` = marker sin `deprecated`; `PresentDeprecated` =
  marker con el flag puesto; `Unknown` = historia no inspeccionada o truncada.
- **`src/Contracts/Domain/ExecutionSnapshot.cs`** — `record ExecutionSnapshot` con la foto de una
  ejecución relevante para un patch, y **`ExecutionSnapshotSet`**, el envoltorio que agrega el flag
  `IsTruncated` (ver Modelo de datos).
- **`src/Contracts/Domain/GateOutcome.cs`** — `enum GateOutcome { Inconclusive = 0, Blocked = 1,
Ready = 2 }`.
- **`src/Contracts/Domain/PhaseVerdict.cs`** — `record PhaseVerdict` con el resultado de evaluar un
  gate: outcome, fase actual, fase siguiente, conteo de bloqueantes, muestra de hasta 5 `workflowId`,
  razón legible y `EvaluatedAt`. Con factories `Ready(...)`, `Blocked(...)`, `Inconclusive(...)`.
- **`src/Contracts/Domain/PhaseTransition.cs`** — clase estática pura con la tabla de saltos legales:
  `IsLegal(PatchPhase from, PatchPhase to)` (solo `Coexistence→Deprecated` y `Deprecated→Clean`) y
  `NextOf(PatchPhase)` (devuelve `null` para `Unknown` y para `Clean`). Es el "pasar de los pasos 1
  al 2 y del 2 al 3 en orden" del README, y lo consumen el override manual del spec 04 y la API del
  spec 08.
- **`src/Contracts/Domain/Gates/IPhaseGate.cs`** — puerto:
  `PatchPhase From { get; }`, `PatchPhase To { get; }`,
  `PhaseVerdict Evaluate(PatchKey key, ExecutionSnapshotSet executions)`.
- **`src/Contracts/Domain/Gates/CoexistenceToDeprecatedGate.cs`** — gate 1→2. Bloquean las
  ejecuciones **abiertas** con `MarkerPresence.Absent` (pre-patch todavía vivas).
- **`src/Contracts/Domain/Gates/DeprecatedToCleanGate.cs`** — gate 2→3. Bloquean las ejecuciones
  **abiertas** con `Present` o `PresentDeprecated` (cualquier ejecución abierta que lleve el marker).
- **`src/Contracts/Domain/Gates/PhaseEvaluator.cs`** — clase que recibe `IEnumerable<IPhaseGate>` y
  elige el gate por `From == currentPhase`; devuelve `Inconclusive` para `Unknown` y un veredicto
  terminal (`NextPhase = null`) para `Clean`.
- **`src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs`** _(modificado)_ —
  `AddPatchMonitorServices` registra los dos `IPhaseGate` como singleton y `PhaseEvaluator` como
  singleton, reemplazando el placeholder vacío del spec 01.
- **`test/PatchMonitor.Tests/PatchMonitor.Tests.csproj`** — `net8.0`, `IsTestProject`,
  `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4 (mismas
  versiones que `test/ReleaseOrder.Tests` del repo de referencia). `ProjectReference` a `Contracts` y
  `PatchMonitor`. **Sin `Temporalio`**: no hace falta hasta el spec 05.
- **`test/PatchMonitor.Tests/Domain/`** — `PatchKeyTests.cs`, `PhaseTransitionTests.cs`,
  `CoexistenceToDeprecatedGateTests.cs`, `DeprecatedToCleanGateTests.cs`, `PhaseEvaluatorTests.cs`,
  `ServiceRegistrationTests.cs` y `ExecutionSnapshotBuilder.cs` (helper de fixtures).
- **`PatchMonitor.sln`** _(modificado)_ — se agrega el proyecto de tests bajo una carpeta `test`.

**No incluye (fuera de alcance de este spec):**

- **Cualquier lectura de Temporal.** Ni `ListWorkflowsAsync`, ni `FetchHistoryAsync`, ni
  `TemporalClient`. `src/Contracts/Domain/` no tiene un solo `using Temporalio`.
- **`IPatchDiscovery`** y el descubrimiento en dos niveles: spec 03. Este spec define la **forma** de
  los datos que ese puerto va a producir, no cómo se obtienen.
- **`IPhaseResolver`** y la lectura del flag `deprecated` del marker: spec 04. Acá `MarkerPresence`
  llega ya resuelto en el snapshot; quién lo resuelve es problema del 04.
- **El override manual de fase** declarado por el operador: spec 04. `PhaseTransition.IsLegal` es la
  regla que ese override va a validar, pero el override en sí no se modela acá.
- **`PatchStateWorkflow`, `PatchRegistryWorkflow`, `IDecisionSink`, `Continue-As-New`:** spec 05.
  `PatchKey.ToWorkflowId()` existe acá como función pura; quién la usa para hacer signal-with-start
  es del 05.
- **`MonitorWorkflow`, `ScheduleBootstrapper` y el Schedule de 5 minutos:** spec 06.
- **`INotifier` y la detección de "el veredicto cambió":** spec 07. `PhaseVerdict` es el tipo que se
  va a comparar, pero la comparación y la deduplicación viven allá.
- **Endpoints HTTP que expongan veredictos:** spec 08.
- **Tests con el entorno de time-skipping de `Temporalio`:** desde el spec 05. Este proyecto de
  tests corre sin Docker, sin cluster y sin descargar el test-server.
- **Persistencia de `ExecutionSnapshot` o de `PhaseVerdict`:** son tipos en memoria; su durabilidad
  es del spec 05.

## Modelo de datos

Todo lo nuevo vive en `namespace Contracts.Domain` y `Contracts.Domain.Gates`. Árbol tras este spec
(solo lo que cambia):

```text
proyecto_monitoreo/
├── PatchMonitor.sln                       (modificado: + proyecto de tests)
├── src/
│   ├── Contracts/
│   │   └── Domain/
│   │       ├── PatchPhase.cs
│   │       ├── PatchKey.cs
│   │       ├── ExecutionStatus.cs
│   │       ├── MarkerPresence.cs
│   │       ├── ExecutionSnapshot.cs        (ExecutionSnapshot + ExecutionSnapshotSet)
│   │       ├── GateOutcome.cs
│   │       ├── PhaseVerdict.cs
│   │       ├── PhaseTransition.cs
│   │       └── Gates/
│   │           ├── IPhaseGate.cs
│   │           ├── CoexistenceToDeprecatedGate.cs
│   │           ├── DeprecatedToCleanGate.cs
│   │           └── PhaseEvaluator.cs
│   └── PatchMonitor/
│       └── Infrastructure/ServiceCollectionExtensions.cs   (modificado)
└── test/
    └── PatchMonitor.Tests/
        ├── PatchMonitor.Tests.csproj
        └── Domain/
            ├── ExecutionSnapshotBuilder.cs
            ├── PatchKeyTests.cs
            ├── PhaseTransitionTests.cs
            ├── CoexistenceToDeprecatedGateTests.cs
            ├── DeprecatedToCleanGateTests.cs
            ├── PhaseEvaluatorTests.cs
            └── ServiceRegistrationTests.cs
```

Tipos centrales:

```csharp
namespace Contracts.Domain;

public enum PatchPhase { Unknown = 0, Coexistence = 1, Deprecated = 2, Clean = 3 }

public enum MarkerPresence { Unknown = 0, Absent = 1, Present = 2, PresentDeprecated = 3 }

public enum GateOutcome { Inconclusive = 0, Blocked = 1, Ready = 2 }

public enum ExecutionStatus
{
    Unknown = 0, Running = 1, Completed = 2, Failed = 3,
    Canceled = 4, Terminated = 5, ContinuedAsNew = 6, TimedOut = 7
}

public sealed record PatchKey(string Namespace, string WorkflowType, string PatchId)
{
    // "patch-state::<ns>::<type>::<patchId>", saneado y estable.
    public string ToWorkflowId();
}

public sealed record ExecutionSnapshot(
    string WorkflowId,
    string RunId,
    string WorkflowType,
    ExecutionStatus Status,
    MarkerPresence Marker,
    DateTimeOffset StartTime);

public sealed record ExecutionSnapshotSet(
    IReadOnlyList<ExecutionSnapshot> Snapshots,
    bool IsTruncated);

public sealed record PhaseVerdict(
    GateOutcome Outcome,
    PatchPhase CurrentPhase,
    PatchPhase? NextPhase,
    int BlockingExecutionCount,
    IReadOnlyList<string> BlockingSample,   // hasta 5 workflowId
    string Reason,
    DateTimeOffset EvaluatedAt);
```

Reglas de `PatchKey.ToWorkflowId()`:

- Formato `patch-state::{Namespace}::{WorkflowType}::{PatchId}`.
- Cada segmento se sanea reemplazando todo carácter fuera de `[A-Za-z0-9._-]` por `_`. **No** se
  cambia el casing: los `patchId` son sensibles a mayúsculas.
- Si el id compuesto supera los 200 caracteres, se trunca a 191 y se le concatena `_` más los
  primeros 8 hex del `SHA-256` del id **sin truncar**. Determinístico y sin colisiones prácticas.

Tabla de decisión de los gates (`Status.IsOpen()` filtra primero; las cerradas nunca bloquean):

| Gate                          | Fase origen → destino | Bloquea                                                            | Inconclusive                                                  | Ready                                        |
| ----------------------------- | --------------------- | ------------------------------------------------------------------ | ------------------------------------------------------------- | -------------------------------------------- |
| `CoexistenceToDeprecatedGate` | 1 → 2                 | ejecución **abierta** con `Marker = Absent`                        | alguna abierta con `Marker = Unknown`, o `IsTruncated = true` | ninguna abierta sin marker y datos completos |
| `DeprecatedToCleanGate`       | 2 → 3                 | ejecución **abierta** con `Marker = Present` o `PresentDeprecated` | alguna abierta con `Marker = Unknown`, o `IsTruncated = true` | ninguna abierta con marker y datos completos |

Precedencia dentro de un gate: **`Blocked` gana sobre `Inconclusive`**. Si ya hay una ejecución que
bloquea con certeza, que otras estén sin inspeccionar no cambia la respuesta. `Inconclusive` solo
aparece cuando no hay bloqueantes conocidos pero los datos no alcanzan para afirmar `Ready`.

Conjunto vacío y no truncado ⇒ `Ready`: no hay ninguna ejecución que bloquee. Queda documentado en
el XML-doc de cada gate porque es contraintuitivo cuando el descubrimiento falló y devolvió nada;
ese caso el spec 03 lo marca con `IsTruncated = true`.

`PhaseTransition`: legales `Coexistence→Deprecated` y `Deprecated→Clean`. Ilegales todas las demás,
incluidas `Coexistence→Clean` (saltear), los retrocesos, `X→X` y cualquiera desde o hacia `Unknown`.

`PhaseEvaluator.Evaluate(PatchKey, PatchPhase current, ExecutionSnapshotSet)`:

- `current = Unknown` → `Inconclusive`, `NextPhase = null`, razón "fase actual no resuelta".
- `current = Clean` → `Blocked`, `NextPhase = null`, razón "fase final: no hay transición siguiente".
- `current = Coexistence | Deprecated` → delega en el `IPhaseGate` cuyo `From` coincide.
- Si no hay gate para esa fase → `InvalidOperationException` (error de cableado de DI, no de datos).

## Plan de implementación

1. **Enums y `PatchKey`.** Crear `PatchPhase.cs`, `MarkerPresence.cs`, `GateOutcome.cs`,
   `ExecutionStatus.cs` (con `IsOpen()`) y `PatchKey.cs` con `ToWorkflowId()`. `dotnet build`
   compila. Sin tests todavía.

2. **Proyecto de tests + tests de `PatchKey`.** Crear `test/PatchMonitor.Tests/` con el `.csproj`
   (xUnit, sin `Temporalio`), agregarlo a `PatchMonitor.sln` bajo la carpeta `test`, y escribir
   `PatchKeyTests.cs`: determinismo (dos llamadas dan el mismo id), unicidad (keys distintas dan ids
   distintos), saneo de caracteres inválidos, y truncado + hash estable para un `patchId` largo.
   `dotnet test` en verde.

3. **`ExecutionSnapshot` + `ExecutionSnapshotSet` + builder de fixtures.** Crear los records y
   `test/PatchMonitor.Tests/Domain/ExecutionSnapshotBuilder.cs`, un helper fluido
   (`Open().WithoutMarker()`, `Open().WithMarker()`, `Open().WithDeprecatedMarker()`,
   `Closed()...`, `Uninspected()`) para que los tests de los gates se lean como la tabla de decisión.
   `dotnet test` sigue en verde.

4. **`PhaseVerdict` y `PhaseTransition`.** Crear el record con sus tres factories y la clase estática
   con la tabla de saltos. Escribir `PhaseTransitionTests.cs` cubriendo las dos transiciones legales
   y las ilegales (salteo 1→3, retrocesos, `X→X`, desde/hacia `Unknown`).

5. **`IPhaseGate` + `CoexistenceToDeprecatedGate`.** Crear el puerto y el gate 1→2 con su tabla de
   decisión, el tope de 5 en `BlockingSample` y la precedencia `Blocked` > `Inconclusive`. Escribir
   `CoexistenceToDeprecatedGateTests.cs`: vacío→`Ready`; todas abiertas con marker→`Ready`; una
   abierta sin marker→`Blocked` con `BlockingExecutionCount = 1`; cerrada sin marker→no bloquea;
   una abierta `Unknown`→`Inconclusive`; `IsTruncated = true` sin bloqueantes→`Inconclusive`;
   bloqueante + `Unknown` juntos→`Blocked`; 9 bloqueantes→`BlockingSample.Count == 5` y
   `BlockingExecutionCount == 9`.

6. **`DeprecatedToCleanGate`.** El gate 2→3 con su predicado propio y
   `DeprecatedToCleanGateTests.cs`: abierta con `Present`→`Blocked`; abierta con
   `PresentDeprecated`→`Blocked`; abierta con `Absent`→**no** bloquea (la asimetría de la
   restricción #3, con un test cuyo nombre lo diga); cerradas con marker→no bloquean; `Unknown`
   abierta→`Inconclusive`.

7. **`PhaseEvaluator`.** Crear la clase que resuelve el gate por `From` y sus cuatro casos de borde
   (`Unknown`, `Clean`, delegación correcta a cada gate, gate faltante→excepción) en
   `PhaseEvaluatorTests.cs`.

8. **Registro en DI.** Reemplazar el cuerpo placeholder de
   `src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs` por el registro de los dos
   `IPhaseGate` y del `PhaseEvaluator` como singletons. `ServiceRegistrationTests.cs` construye un
   `ServiceProvider` con `AddPatchMonitorServices()` y verifica que resuelve un `PhaseEvaluator` y
   exactamente dos `IPhaseGate`, uno por cada `From`.

9. **Verificación end-to-end manual.** Desde la raíz del repo:
   - `dotnet build PatchMonitor.sln` → 0 errores, 0 advertencias.
   - `dotnet test PatchMonitor.sln` → todos los tests pasan, sin Docker levantado y sin descargar
     ningún test-server (el comando termina en segundos).
   - `grep -r "Temporalio" src/Contracts/Domain/` no devuelve nada.
   - `docker compose ps` apagado durante toda la corrida, para dejar constancia de que los tests no
     dependen del stack.
     Registrar la salida de estos comandos en este spec antes de marcar los criterios de aceptación.

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa con el stack de Docker **apagado** y sin descargar el
      binario del test-server de Temporal.
- [x] `grep -r "Temporalio\|TemporalClient" src/Contracts/Domain/` no devuelve ninguna coincidencia.
- [x] `PatchMonitor.sln` tiene 5 proyectos: los 4 del spec 01 más `test/PatchMonitor.Tests`.
- [x] `PatchKey.ToWorkflowId()` es determinístico entre llamadas y entre procesos, sanea caracteres
      fuera de `[A-Za-z0-9._-]`, y para un id compuesto de más de 200 caracteres devuelve ≤ 200 con
      sufijo de hash estable — los 4 casos con test.
- [x] `CoexistenceToDeprecatedGate` devuelve `Blocked` con una ejecución **abierta sin marker** y
      `Ready` cuando esa misma ejecución está **cerrada** — dos tests distintos.
- [x] `DeprecatedToCleanGate` devuelve `Blocked` tanto con `Present` como con `PresentDeprecated` en
      una ejecución abierta, y `Ready` con una ejecución abierta `Absent` — la asimetría queda
      cubierta por tests con nombre explícito.
- [x] Ambos gates devuelven `Inconclusive` cuando hay una ejecución abierta con `Marker = Unknown` o
      cuando `ExecutionSnapshotSet.IsTruncated` es `true`, **salvo** que ya exista un bloqueante
      conocido, en cuyo caso devuelven `Blocked`.
- [x] Con 9 ejecuciones bloqueantes, el veredicto trae `BlockingExecutionCount == 9` y
      `BlockingSample.Count == 5`.
- [x] `PhaseTransition.IsLegal` acepta exactamente `Coexistence→Deprecated` y `Deprecated→Clean`, y
      rechaza `Coexistence→Clean`, los retrocesos, `X→X` y todo lo que involucre `Unknown`.
- [x] `PhaseEvaluator` devuelve `Inconclusive` para `PatchPhase.Unknown` y un veredicto con
      `NextPhase == null` para `PatchPhase.Clean`.
- [x] Un `ServiceProvider` construido con `AddPatchMonitorServices()` resuelve `PhaseEvaluator` y
      exactamente dos `IPhaseGate`, con `From` distintos.

## Resultado de la implementación

Verificado el 2026-09-10 sobre SDK .NET 10.0.302 (el `net8.0` compila con el reference pack de NuGet).
Rama `spec-02-patch-lifecycle-domain-model`.

**Salida de la verificación end-to-end (paso 9):**

- `dotnet build PatchMonitor.sln` → `Compilación correcta. 0 Advertencia(s) 0 Errores`.
- `dotnet test PatchMonitor.sln` → `Con error: 0, Superado: 53, Omitido: 0, Total: 53`, en ~2,3 s,
  con `docker compose ps` sin ningún contenedor (stack detenido con `docker compose stop` durante
  la corrida) y sin descarga de test-server.
- `grep -rn "Temporalio\|TemporalClient" src/Contracts/Domain/` → sin coincidencias.
- `dotnet sln PatchMonitor.sln list` → 5 proyectos: `Common`, `Contracts`, `MonitorApi`,
  `PatchMonitor` y `test/PatchMonitor.Tests`.
- Reparto de los 53 tests: `PatchKeyTests` 10, `PhaseTransitionTests` 16, `CoexistenceToDeprecatedGateTests`
  10, `DeprecatedToCleanGateTests` 9, `PhaseEvaluatorTests` 5, `ServiceRegistrationTests` 3.

**Desviaciones respecto del plan (resueltas durante la implementación):**

1. **`IsOpen()` es método de extensión, no "propiedad de extensión".** C# en `net8.0` no admite
   propiedades de extensión sobre un enum; `ExecutionStatusExtensions.IsOpen(this ExecutionStatus)`
   conserva el nombre y la semántica de la spec (`Running` y `ContinuedAsNew` abiertos).
2. **El recorte de `BlockingSample` a 5 vive solo en `PhaseVerdict.Blocked(...)`**, no también en
   cada gate: un único punto de control. Los gates pasan la lista completa de `workflowId` y el
   conteo total; el factory recorta.
3. **`EvaluatedAt` se toma con `DateTimeOffset.UtcNow` dentro de cada gate y del `PhaseEvaluator`.**
   La firma de `IPhaseGate.Evaluate` de la spec no recibe reloj; el timestamp no es I/O sobre
   Temporal/Docker/red, así que no rompe la pureza que exige el spec. Inyectar `TimeProvider` queda
   disponible para un spec posterior si hiciera falta determinismo del sello temporal.
4. **`ExecutionSnapshotSet.Empty`** agregado como conveniencia (conjunto vacío y no truncado);
   lo usan los tests y no estaba enumerado en el Modelo de datos.
5. **Comentario de `ExecutionStatus.cs` reformulado** para no contener la cadena `Temporalio` y
   así cumplir literalmente el criterio del `grep`.
6. **Docker se detuvo y se volvió a levantar** solo para dejar constancia de que `dotnet test`
   pasa con el stack apagado; el stack estaba corriendo desde la verificación del spec 01 y quedó
   en ese mismo estado al terminar.

## Decisiones tomadas y descartadas

- **Sí:** dominio y gates dentro de `src/Contracts/Domain/`. `Construction.md` §5 hace de `Contracts`
  la raíz de dependencias; worker y API acceden sin proyectos ni referencias nuevas.
- **No:** un quinto proyecto `src/Domain`. Aislaría el dominio también del paquete `Temporalio`, pero
  obliga a tocar la solución y los dos Dockerfiles a cambio de una pureza que los tests ya verifican
  con un `grep`.
- **Sí:** `IPhaseGate` con dos implementaciones y un `PhaseEvaluator` que elige por `From`. Es la
  inversión de dependencias que pide el README y deja la asimetría de la restricción #3 en dos
  archivos separados, imposibles de confundir.
- **No:** métodos estáticos en una clase `PhaseGates`. Igual de testeables, pero no inyectables y no
  extensibles sin editar el llamador.
- **Sí:** `GateOutcome` tri-estado. El tope de ejecuciones inspeccionadas del spec 03 hace que "no sé"
  sea un resultado normal, no un error; colapsarlo en `false` haría que el monitor mienta.
- **No:** `bool CanAdvance` con razón en texto. Obligaría a parsear prosa río abajo para distinguir
  "bloqueado" de "sin datos".
- **Sí:** `MarkerPresence` de cuatro valores en vez de dos booleanos. La restricción #2 exige
  distinguir marker-con-`deprecated` de marker-sin-`deprecated`, y el enum no admite las
  combinaciones imposibles que sí admitirían dos `bool?`.
- **Sí:** los gates reciben **todas** las ejecuciones y filtran por `Status.IsOpen()` internamente.
  Cada gate es dueño de su predicado completo; si el filtrado lo hiciera el spec 03, el criterio
  quedaría repartido entre descubrimiento y dominio.
- **Sí:** envolver la lista en `ExecutionSnapshotSet` con `IsTruncated`. La señal de datos parciales
  viaja con los datos en vez de como parámetro suelto que un llamador puede olvidar.
- **Sí:** `Blocked` tiene precedencia sobre `Inconclusive`. Un bloqueante conocido ya responde la
  pregunta; degradar a "no sé" sería perder información cierta.
- **Sí:** `PatchPhase.Unknown = 0`. El default de un enum no debe mentir diciendo "fase 1"; el spec 04
  necesita un valor honesto para cuando la historia no alcanza.
- **Sí:** `PhaseTransition` en esta spec. Es el "en orden correcto" del README y el spec 04 (override
  manual) y el 08 (API) lo van a validar; escribirlo acá lo deja probado antes de tener consumidores.
- **No:** modelar el override manual acá. Es entrada del operador, no una regla del ciclo de vida;
  `Construction.md` §7 lo pone en el spec 04.
- **Sí:** `PatchKey.ToWorkflowId()` en esta spec. Es una función pura sin I/O; el spec 05 la consume
  ya testeada en determinismo y en saneo.
- **Sí:** registrar los gates en DI ahora, cerrando el placeholder que dejó el spec 01. El test de
  registro convierte un error de cableado en un test rojo en vez de en un `NullReferenceException`
  dentro de un workflow en el spec 06.
- **No:** `FluentAssertions`. Las versiones actuales tienen licencia comercial; el `Assert` de xUnit
  alcanza para aserciones sobre records y enums.
- **No:** `Temporalio` como `PackageReference` del proyecto de tests. Entra en el spec 05, junto con
  el primer test de time-skipping.

## Riesgos identificados

| Riesgo                                                                                                                                                       | Mitigación                                                                                                                                                                                |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Confundir los predicados de los dos gates: usar "sin marker" para el 2→3 o "con marker" para el 1→2 (restricción #3).                                        | Un gate por archivo, la tabla de decisión en el Modelo de datos, y tests con nombre explícito para el caso asimétrico (`abierta_sin_marker_no_bloquea_el_paso_a_clean`).                  |
| Asumir que `DeprecatePatch` deja de escribir el marker y modelar el marker como booleano (restricción #2).                                                   | `MarkerPresence` separa `Present` de `PresentDeprecated`; ninguno de los dos gates mira el flag `deprecated` para decidir bloqueo, solo la presencia.                                     |
| Un `ExecutionSnapshotSet` vacío por un fallo del descubrimiento se interpreta como `Ready` y el monitor reporta "listo para avanzar" sin datos.              | `Ready` con conjunto vacío está documentado en el XML-doc de cada gate; el spec 03 debe marcar `IsTruncated = true` cuando el descubrimiento falló o topeó, lo que fuerza `Inconclusive`. |
| `PatchKey.ToWorkflowId()` genera ids distintos entre corridas o supera el límite de `WorkflowId` de Temporal, y el spec 05 crea entity workflows duplicados. | Saneo determinístico sin dependencia de cultura, tope de 200 caracteres con sufijo `SHA-256`, y tests de determinismo y de largo.                                                         |
| `PhaseVerdict` con muestra de `workflowId` del proyecto observado se serializa en el estado del entity workflow (spec 05) y engorda la Event History.        | `BlockingSample` topeado en 5 elementos; el conteo total va aparte en `BlockingExecutionCount`.                                                                                           |
| Estos tipos se convierten en el DTO de la API sin capa intermedia y un cambio de dominio rompe el contrato HTTP (spec 08).                                   | Los records viven en `Contracts.Domain`, no en `Contracts.Api`; el spec 08 decide si expone estos tipos o mapea a DTOs propios.                                                           |

## Qué NO entra en este spec

- Cualquier llamada a Temporal: `ListWorkflowsAsync`, `FetchHistoryAsync`, `TemporalClient`.
- `IPatchDiscovery` y el descubrimiento en dos niveles con fallback a Event History.
- `IPhaseResolver` y la lectura del flag `deprecated` dentro del marker.
- El override manual de fase declarado por el operador.
- `PatchStateWorkflow`, `PatchRegistryWorkflow`, `IDecisionSink` y el `Continue-As-New`.
- `MonitorWorkflow`, `ScheduleBootstrapper` y el Temporal Schedule de 5 minutos.
- `INotifier` y la detección de cambio de veredicto entre corridas.
- Endpoints HTTP que expongan fases o veredictos.
- Tests con el entorno de time-skipping de `Temporalio`.

Cada uno, si entra, va en su propio spec.
