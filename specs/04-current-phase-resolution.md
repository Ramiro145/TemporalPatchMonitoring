# 04 - Resolución de la fase actual de un patch

**Estado:** Aprobado
**Depende de:** [03-patch-discovery-two-tier.md](03-patch-discovery-two-tier.md)
**Fecha:** 2026-09-10

**Objetivo:** Resolver en qué fase (1 Convivencia / 2 Deprecación / 3 Código limpio) está cada patch
descubierto, interpretando el flag `deprecated` del marker `core_patch` sobre los snapshots del
spec 03, con soporte para un override manual de fase declarado por el operador.

## Por qué existe este spec

El spec 03 entrega, por cada `patchId`, un `ExecutionSnapshotSet` con el `MarkerPresence` de cuatro
valores ya resuelto —incluido `PresentDeprecated`— pero **nadie interpreta ese dato como una fase**.
El spec 02 dejó `PhaseEvaluator`, que recibe la fase actual **como parámetro** (`PhaseEvaluator.
Evaluate(PatchKey, PatchPhase current, ExecutionSnapshotSet)`); hoy no hay quién calcule ese
`current`. Este spec es ese cálculo.

Está separado del 03 a propósito (`Construction.md` §7): el 03 extrae datos crudos de Temporal, el
04 los interpreta sin volver a tocar el cluster. La sutileza que justifica el corte es la
restricción #2 de `Construction.md` §4: **fase 1 y fase 2 escriben ambas el marker `core_patch`**;
lo único que cambia es el flag `deprecated` adentro. Asumir que `DeprecatePatch` deja de escribir el
marker es el error típico que el artifact marca en rojo. Mezclar esa lógica con el descubrimiento
entierra la distinción; acá vive sola, en un servicio puro y testeable con fixtures.

La fase 3 (código limpio) es más difícil de detectar que las otras dos, porque el código limpio **no
menciona el patch**: no hay marker que leer. La única señal disponible sin tocar el código fuente
del proyecto observado es temporal — ejecuciones nuevas que ya no escriben el marker, arrancadas
después de que las últimas que sí lo escribían — y por eso el resolver necesita un reloj y un margen
de gracia configurable.

El override manual entra en este spec porque `Construction.md` §7 lo pone acá y lo consumen los
specs 05 y 08. La persistencia durable es del spec 05, así que acá el override vive detrás de un
puerto (`IPhaseOverrideStore`) con una implementación en memoria; el spec 05 la reemplaza por el
entity workflow y el 08 la escribe vía HTTP.

## Alcance

**Incluye:**

- **`src/Contracts/Phase/IPhaseResolver.cs`** — el puerto que consume el spec 06:

  ```csharp
  public interface IPhaseResolver
  {
      PhaseResolution Resolve(PatchDiscoveryResult result);
  }
  ```

  Síncrono y sin `CancellationToken`: es cómputo puro sobre datos ya en memoria, sin I/O.

- **`src/Contracts/Phase/PhaseResolution.cs`** —
  `record PhaseResolution(PatchPhase Phase, PhaseSource Source, string Reason, DateTimeOffset
ResolvedAt)` más `enum PhaseSource { Inferred = 0, Override = 1 }`. `Inferred` = fase deducida de
  los snapshots; `Override` = fase impuesta por el operador (la inferida viaja en `Reason` para
  diagnóstico).
- **`src/Contracts/Phase/PhaseOverride.cs`** —
  `record PhaseOverride(PatchKey Key, PatchPhase Phase, string DeclaredBy, DateTimeOffset DeclaredAt,
DateTimeOffset? ExpiresAt)` con `bool IsActiveAt(DateTimeOffset now)` (`ExpiresAt is null ||
ExpiresAt > now`).
- **`src/Contracts/Phase/IPhaseOverrideStore.cs`** — puerto de almacenamiento:

  ```csharp
  public interface IPhaseOverrideStore
  {
      PhaseOverride? Get(PatchKey key);
      void Set(PhaseOverride ov);
      void Clear(PatchKey key);
      IReadOnlyList<PhaseOverride> GetAll();
  }
  ```

- **`src/Contracts/Phase/PhaseOptions.cs`** — `record PhaseOptions(TimeSpan CleanGrace)` con un
  `static PhaseOptions FromEnvironment()` que lee `PHASE_CLEAN_GRACE_HOURS` (default `24`), con el
  mismo patrón "ausente / no numérico / no positivo ⇒ default, nunca lanza" de
  `src/Contracts/Discovery/DiscoveryOptions.cs`.
- **`src/PatchMonitor/Services/PhaseResolver.cs`** — implementación de `IPhaseResolver`.
  Constructor `PhaseResolver(IPhaseOverrideStore overrides, PhaseOptions options, TimeProvider
clock)`. Aplica la tabla de decisión del Modelo de datos. **Sin `using Temporalio`.**
- **`src/PatchMonitor/Services/InMemoryPhaseOverrideStore.cs`** — implementación de
  `IPhaseOverrideStore` sobre `ConcurrentDictionary<string, PhaseOverride>` con la clave
  `key.ToWorkflowId()` (ya determinística y saneada, spec 02). `Get` y `GetAll` descartan (y borran)
  los overrides con `ExpiresAt` vencido — pero `TimeProvider` no está disponible acá, así que la
  expiración se chequea contra `DateTimeOffset.UtcNow`; el resolver, que sí tiene reloj, vuelve a
  chequear `IsActiveAt` con su `clock`. Ver Decisiones.
- **`src/PatchMonitor/Activities/PhaseActivities.cs`** — clase `[Activity]` que envuelve el resolver
  y el store, para que el spec 06 y el 08 la invoquen desde workflows:
  - `[Activity] PhaseResolution ResolvePhase(PatchDiscoveryResult result)`
  - `[Activity] void SetPhaseOverride(PhaseOverride ov)`
  - `[Activity] void ClearPhaseOverride(PatchKey key)`

    Un `PhaseOverride` con fase fuera de `{Coexistence, Deprecated, Clean}` se rechaza como
    `ApplicationFailureException(nonRetryable: true)` (error del operador, no transitorio).

- **`src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs`** _(modificado)_ —
  `AddPatchMonitorServices` registra `PhaseOptions` (singleton desde `FromEnvironment()`),
  `TimeProvider.System` (singleton), `IPhaseOverrideStore → InMemoryPhaseOverrideStore` (singleton),
  `IPhaseResolver → PhaseResolver` (singleton) y `PhaseActivities` por **tipo concreto**. Reemplaza
  el comentario "IPhaseResolver, INotifier, IDecisionSink llegan en los specs 04+".
- **`src/PatchMonitor/Program.cs`** _(modificado)_ — agrega `typeof(PhaseActivities)` a
  `activityTypes` en `WorkerHost.RunAsync`.
- **`docker/docker-compose.yml`** _(modificado)_ — agrega `PHASE_CLEAN_GRACE_HOURS` al `environment`
  del `patch-monitor-worker`.
- **`test/PatchMonitor.Tests/Phase/FakeTimeProvider.cs`** — subclase propia de `TimeProvider` que
  sobrescribe `GetUtcNow()` con un valor fijo y mutable. **No** se agrega el paquete
  `Microsoft.Extensions.TimeProvider.Testing`: el `.csproj` de tests no gana ninguna referencia.
- **`test/PatchMonitor.Tests/Phase/PhaseResolverTests.cs`**,
  **`PhaseOptionsTests.cs`**, **`InMemoryPhaseOverrideStoreTests.cs`** — tests unitarios puros, sin
  `Temporalio`, sin Docker, sin descarga de test-server.
- **`test/PatchMonitor.Tests/Phase/SnapshotSetBuilder.cs`** — helper de fixtures para construir
  `ExecutionSnapshotSet` que se lean como la tabla de decisión (reusa
  `ExecutionSnapshotBuilder` del spec 02 si aplica; si no, un builder propio para el conjunto).

**No incluye (fuera de alcance de este spec):**

- **Combinar `PhaseResolution` con `PhaseEvaluator`.** El resolver **no** llama al evaluador del
  spec 02. Unir "la fase actual es X" con "el gate X→X+1 dice Ready/Blocked/Inconclusive" en un
  `PatchAssessment` es del `MonitorWorkflow` (spec 06).
- **Persistir la fase resuelta o el override.** `PatchStateWorkflow`, `PatchRegistryWorkflow`,
  `IDecisionSink` y el `Continue-As-New` son del spec 05. Acá `PhaseResolution` es un valor en
  memoria y el override vive en un `ConcurrentDictionary` que se pierde al reiniciar el worker.
- **La superficie HTTP que declara el override.** Endpoint `POST /patches/{key}/override` y la
  validación con `[WorkflowUpdateValidator]`: spec 08. Acá el override se escribe por la Activity.
- **Validar el override contra la fase inferida o contra `PhaseTransition.IsLegal`.** El override
  vigente gana siempre. `PhaseTransition.IsLegal` se usa en el spec 08 al aceptar la petición HTTP,
  no acá.
- **`MonitorWorkflow` y el Temporal Schedule de 5 minutos:** spec 06.
- **`INotifier` y detectar que la fase de un patch cambió entre corridas:** spec 07.
- **Tests con el entorno de time-skipping de `Temporalio`:** desde el spec 05.
- **Descubrir los patches.** Este spec recibe `PatchDiscoveryResult` ya armado por el spec 03; no
  lista ejecuciones ni lee historias.

## Modelo de datos

Todo lo nuevo de `Contracts` vive en `namespace Contracts.Phase`. Árbol tras este spec (solo lo que
cambia):

```text
proyecto_monitoreo/
├── src/
│   ├── Contracts/
│   │   └── Phase/
│   │       ├── IPhaseResolver.cs
│   │       ├── PhaseResolution.cs        (PhaseResolution + PhaseSource)
│   │       ├── PhaseOverride.cs
│   │       ├── IPhaseOverrideStore.cs
│   │       └── PhaseOptions.cs
│   └── PatchMonitor/
│       ├── Services/
│       │   ├── PhaseResolver.cs
│       │   └── InMemoryPhaseOverrideStore.cs
│       ├── Activities/
│       │   └── PhaseActivities.cs
│       ├── Infrastructure/ServiceCollectionExtensions.cs   (modificado)
│       └── Program.cs                                      (modificado)
├── docker/docker-compose.yml                               (modificado)
└── test/
    └── PatchMonitor.Tests/
        └── Phase/
            ├── FakeTimeProvider.cs
            ├── SnapshotSetBuilder.cs
            ├── PhaseResolverTests.cs
            ├── PhaseOptionsTests.cs
            └── InMemoryPhaseOverrideStoreTests.cs
```

Tipos centrales:

```csharp
namespace Contracts.Phase;

public enum PhaseSource { Inferred = 0, Override = 1 }

public sealed record PhaseResolution(
    PatchPhase Phase,
    PhaseSource Source,
    string Reason,
    DateTimeOffset ResolvedAt);

public sealed record PhaseOverride(
    PatchKey Key,
    PatchPhase Phase,
    string DeclaredBy,
    DateTimeOffset DeclaredAt,
    DateTimeOffset? ExpiresAt)
{
    public bool IsActiveAt(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}

public sealed record PhaseOptions(TimeSpan CleanGrace)
{
    public static readonly TimeSpan DefaultCleanGrace = TimeSpan.FromHours(24);
    public static PhaseOptions FromEnvironment();
}
```

Env var que lee `PhaseOptions.FromEnvironment()`:

| Variable                  | Default | Significado                                                                               |
| ------------------------- | ------- | ----------------------------------------------------------------------------------------- |
| `PHASE_CLEAN_GRACE_HOURS` | `24`    | Horas que una ejecución sin marker debe superar al último marker para leerse como fase 3. |

### Algoritmo de `Resolve(PatchDiscoveryResult result)`

`now = clock.GetUtcNow()`. Sea `snaps = result.Executions.Snapshots`. Gana el **primer** caso que
aplica:

| #   | Situación                                                                                                  | `PhaseResolution`                                                                                     |
| --- | ---------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| 1   | `overrides.Get(result.Key)` existe y `IsActiveAt(now)`                                                     | `Phase = ov.Phase`, `Source = Override`, `Reason = "override de <DeclaredBy>; la inferida era <inf>"` |
| 2   | `snaps` vacío, **o** todas con `Marker == Unknown`                                                         | `Phase = Unknown`, `Source = Inferred`, `Reason = "sin evidencia de marker"`                          |
| 3   | ninguna **abierta** con marker, **y** existe una sin marker con `StartTime > lastMarkerStart + CleanGrace` | `Phase = Clean`, `Source = Inferred`, `Reason = "sin marker desde <lastMarkerStart>; código limpio"`  |
| 4   | el marker de `newestWithMarker` es `PresentDeprecated`                                                     | `Phase = Deprecated`, `Source = Inferred`, `Reason = "marker más reciente deprecado"`                 |
| 5   | el marker de `newestWithMarker` es `Present`                                                               | `Phase = Coexistence`, `Source = Inferred`, `Reason = "marker más reciente sin deprecar"`             |
| 6   | hay ejecuciones pero ninguna con marker legible (todas `Absent`, y no aplica el caso 3)                    | `Phase = Unknown`, `Source = Inferred`, `Reason = "ninguna ejecución lleva el marker"`                |

Definiciones:

- **"con marker"** = `Marker` es `Present` **o** `PresentDeprecated`.
- **`lastMarkerStart`** = máximo `StartTime` entre las ejecuciones con marker (abiertas **y**
  cerradas).
- **`newestWithMarker`** = la ejecución con marker de `StartTime` máximo. En empate exacto de
  `StartTime` entre una `Present` y una `PresentDeprecated`, gana `PresentDeprecated` (el ciclo solo
  avanza; nunca retrocede de deprecado a convivencia).
- **"abierta"** = `Status.IsOpen()` (`Running` o `ContinuedAsNew`), la misma definición del spec 02.
- El caso 1 calcula igual la fase inferida (`inf`) corriendo los casos 2–6, solo para armar el
  `Reason`.

Notas:

- **`ExecutionSnapshotSet.IsTruncated` no cambia la fase.** El resolver infiere con lo que hay; la
  incertidumbre por datos parciales la aporta el gate del spec 02, que ya devuelve `Inconclusive`
  ante `IsTruncated = true`. Duplicar esa señal en la fase haría que el monitor no resuelva nada en
  el backend de visibility del repo de referencia (tier 1 inerte, desvío 2 del spec 03).
- El caso 3 exige **ambas** condiciones: que no quede ninguna ejecución **abierta** con marker (si
  queda, el patch sigue en fase 2) y que exista evidencia positiva de código nuevo sin marker pasado
  el margen. Sin la segunda condición, un patch cuyas ejecuciones con marker simplemente drenaron
  sin que se limpiara el código se leería como `Clean` de más.

## Plan de implementación

1. **Tipos de `Contracts/Phase`.** Crear `PhaseResolution.cs` (con `PhaseSource`),
   `PhaseOverride.cs`, `IPhaseOverrideStore.cs` e `IPhaseResolver.cs`. `dotnet build` compila;
   `Contracts` no gana ninguna referencia de paquete.

2. **`PhaseOptions` + `FromEnvironment` + tests.** Crear `Phase/PhaseOptions.cs` con el parseo de
   `PHASE_CLEAN_GRACE_HOURS` (valor ausente, no numérico o ≤ 0 → 24 h, sin excepción).
   `PhaseOptionsTests.cs`: ausente → 24 h; `"48"` → 48 h; `"basura"` → 24 h; `"0"` y `"-3"` → 24 h.
   `dotnet test` en verde.

3. **`InMemoryPhaseOverrideStore` + `FakeTimeProvider` + tests.** Crear el store sobre
   `ConcurrentDictionary` con clave `key.ToWorkflowId()` y `FakeTimeProvider` (subclase de
   `TimeProvider`). `InMemoryPhaseOverrideStoreTests.cs`: `Set` luego `Get` devuelve el mismo
   override; `Clear` lo borra; `Get` de una key inexistente → `null`; un override con `ExpiresAt` en
   el pasado no lo devuelve `Get` ni `GetAll`; `GetAll` devuelve todos los vigentes.

4. **`PhaseResolver` — inferencia (casos 2, 4, 5, 6).** Crear el servicio con el constructor de tres
   dependencias; implementar la selección de `newestWithMarker` y los casos que no dependen del
   margen. `PhaseResolverTests.cs`, un test por fila: conjunto vacío → `Unknown`; todas `Unknown` →
   `Unknown`; todas `Absent` (sin caso 3) → `Unknown` con la razón "ninguna ejecución lleva el
   marker"; `newestWithMarker` `Present` → `Coexistence`; `newestWithMarker` `PresentDeprecated` →
   `Deprecated`; empate de `StartTime` `Present` vs `PresentDeprecated` → `Deprecated` (test con
   nombre explícito del desempate); una ejecución vieja `Present` y una nueva `PresentDeprecated` →
   `Deprecated`.

5. **`PhaseResolver` — fase `Clean` (caso 3) con `CleanGrace`.** Agregar la rama del margen usando
   `clock.GetUtcNow()`. Tests con `FakeTimeProvider`: ninguna abierta con marker + una sin marker
   arrancada 25 h después del último marker, `CleanGrace = 24 h` → `Clean`; la misma sin marker
   arrancada 10 h después → **no** `Clean`, cae a `Coexistence`/`Deprecated` según el último marker;
   una ejecución **abierta** con marker presente aunque haya código nuevo sin marker → sigue
   `Deprecated`/`Coexistence`, nunca `Clean`.

6. **`PhaseResolver` — precedencia del override (caso 1).** Consultar `overrides.Get(key)` primero;
   si `IsActiveAt(now)`, devolver `Source = Override` con la fase inferida en `Reason`. Tests:
   override a `Deprecated` sobre snapshots que inferirían `Coexistence` → `Deprecated` con
   `Source = Override` y el `Reason` menciona "la inferida era Coexistence"; override con `ExpiresAt`
   vencido → se ignora, gana la inferida con `Source = Inferred`; sin override → siempre
   `Source = Inferred`.

7. **`PhaseActivities` + DI + `Program.cs` + compose.** Crear `Activities/PhaseActivities.cs` con las
   tres activities (rechazo `nonRetryable` de fase inválida en `SetPhaseOverride`), registrar en
   `ServiceCollectionExtensions` (`PhaseOptions`, `TimeProvider.System`, `IPhaseOverrideStore`,
   `IPhaseResolver`, `PhaseActivities` por tipo concreto), agregar `typeof(PhaseActivities)` en
   `Program.cs` y `PHASE_CLEAN_GRACE_HOURS` al `patch-monitor-worker` del `docker-compose.yml`.
   `dotnet build` y `dotnet test` en verde; el worker sigue arrancando.

8. **Verificación end-to-end manual.** Con el stack de `docker/` arriba y el `ProbeWorkflow`
   descartable del spec 03:
   - `dotnet build PatchMonitor.sln` → 0 errores, 0 advertencias.
   - `dotnet test PatchMonitor.sln` → todos verdes, stack de Docker apagado, sin descarga de
     test-server.
   - `grep -rn "Temporalio" src/Contracts/Phase/ src/PatchMonitor/Services/PhaseResolver.cs` → sin
     coincidencias.
   - Con un `ProbeWorkflow` parcheado (`Workflow.Patched("probe_patch")`) y una ejecución viva:
     invocar `ResolvePhase` (script one-shot o `temporal` CLI) sobre el `PatchDiscoveryResult` que
     devuelve `DiscoverPatchesAsync` y comprobar `Phase = Coexistence`, `Source = Inferred`.
   - Escribir un override a `Deprecated` con `SetPhaseOverride` y repetir → `Phase = Deprecated`,
     `Source = Override`.
   - Registrar la salida de estos comandos en este spec antes de marcar los criterios de aceptación.

## Criterios de aceptación

- [ ] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [ ] `dotnet test PatchMonitor.sln` pasa con el stack de Docker **apagado** y sin descargar el
      binario del test-server; `test/PatchMonitor.Tests/PatchMonitor.Tests.csproj` no gana ninguna
      referencia nueva (ni `Temporalio`, ni `Microsoft.Extensions.TimeProvider.Testing`).
- [ ] `grep -rn "Temporalio\|TemporalClient" src/Contracts/Phase/ src/PatchMonitor/Services/PhaseResolver.cs`
      no devuelve nada. Dentro de `src/PatchMonitor/` el `using Temporalio` nuevo queda solo en
      `Activities/PhaseActivities.cs` (la envoltura `[Activity]` y la `ApplicationFailureException`).
- [ ] Hay un test por cada fila de la tabla de decisión del Modelo de datos (casos 1 a 6), más el
      test con nombre explícito del desempate `Present` vs `PresentDeprecated` en empate de
      `StartTime`.
- [ ] `newestWithMarker` `Present` → `Coexistence`; `PresentDeprecated` → `Deprecated`. Una ejecución
      vieja `Present` no cambia el resultado si la más reciente con marker es `PresentDeprecated`.
- [ ] `Clean` se devuelve **solo** cuando no queda ninguna ejecución abierta con marker **y** existe
      una sin marker arrancada más de `CleanGrace` después del último marker — dos tests, uno que da
      `Clean` y uno que no por no superar el margen.
- [ ] Una ejecución **abierta** con marker presente nunca produce `Clean`, aunque haya código nuevo
      sin marker.
- [ ] Conjunto vacío o todas las ejecuciones en `Marker == Unknown` → `PhaseResolution.Phase ==
    PatchPhase.Unknown`. `ExecutionSnapshotSet.IsTruncated == true` por sí solo **no** fuerza
      `Unknown`.
- [ ] Un override vigente gana: `PhaseResolution.Source == Override`, `Phase == ov.Phase`, y el
      `Reason` incluye la fase que se habría inferido. Un override con `ExpiresAt` en el pasado se
      ignora y el resultado sale `Source == Inferred`.
- [ ] `PhaseOptions.FromEnvironment()` devuelve `CleanGrace == 24 h` con `PHASE_CLEAN_GRACE_HOURS`
      ausente, y respeta un valor válido; un valor no numérico o ≤ 0 cae a 24 h sin lanzar.
- [ ] `InMemoryPhaseOverrideStore`: `Set` + `Get` devuelve el override; `Clear` lo borra; un
      override vencido no aparece en `Get` ni en `GetAll`.
- [ ] Un `ServiceProvider` construido con `AddPatchMonitorServices()` resuelve `IPhaseResolver`,
      `IPhaseOverrideStore`, `PhaseOptions`, `TimeProvider` y `PhaseActivities`.
- [ ] El `patch-monitor-worker` del `docker-compose.yml` declara `PHASE_CLEAN_GRACE_HOURS` y el
      worker arranca con `PhaseActivities` registrada (`docker compose config` válido; test de
      registro en DI).

## Decisiones tomadas y descartadas

- **Sí:** la fase la fija el marker de la ejecución **con marker más reciente** (`Present` →
  fase 1, `PresentDeprecated` → fase 2). Es la pregunta real: qué código está desplegado hoy. Las
  ejecuciones viejas son historia, no estado.
- **No:** "cualquier ejecución `Present` ⇒ fase 1, solo `all(PresentDeprecated)` ⇒ fase 2". Es más
  conservador pero refleja la historia acumulada en la ventana de lookback, no el estado actual: un
  patch recién deprecado seguiría leyéndose como fase 1 por sus ejecuciones viejas.
- **No:** mirar solo ejecuciones abiertas. Un patch cuyas ejecuciones ya drenaron se volvería
  irresoluble (`Unknown`) justo cuando está por completar su ciclo — y el spec 03 barre cerradas
  dentro del lookback precisamente para no perder ese caso.
- **Sí:** fase 3 por comparación temporal — ejecuciones sin marker arrancadas después del último
  marker más un margen `CleanGrace`. Es la única señal de "el código ya no menciona el patch"
  disponible sin leer el código fuente del proyecto observado.
- **No:** fase 3 solo por override manual. Evita todo falso positivo, pero el ciclo de vida nunca se
  cerraría solo y el monitor mentiría reportando fase 2 indefinidamente.
- **No:** "sin markers en el lookback ⇒ `Clean`". Se confunde con "el descubrimiento no llegó a leer
  las historias" (`IsTruncated` / `Unknown`); por eso el caso 3 exige evidencia **positiva** de una
  ejecución nueva sin marker.
- **Sí:** `IPhaseResolver` devuelve solo `PhaseResolution` (fase + evidencia). Combinarla con el
  `PhaseEvaluator` del spec 02 en un `PatchAssessment` es del `MonitorWorkflow` (spec 06):
  `Construction.md` §7 dice "06 une 03 + 04 + 05 en una pasada".
- **No:** que el spec 04 entregue el `PatchAssessment` completo llamando al evaluador. Mezcla la
  responsabilidad de "en qué fase estoy" con "puedo avanzar", que el spec 02 dejó explícitamente
  separada.
- **Sí:** `Unknown` solo ante ausencia total de evidencia (conjunto vacío o todo `Unknown`).
  `IsTruncated` con al menos un marker legible infiere la fase igual; el gate del spec 02 ya degrada
  a `Inconclusive` por su cuenta.
- **No:** `IsTruncated ⇒ Unknown`. Con el tier 1 inerte del backend de visibility del repo de
  referencia (desvío 2 del spec 03) casi todos los barridos salen truncados y el monitor nunca
  resolvería fase.
- **Sí:** override vigente gana siempre, sin validar contra la fase inferida. El operador sabe qué
  desplegó; la inferida viaja en `Reason` para que el desacuerdo sea visible.
- **No:** aceptar el override solo si `PhaseTransition.IsLegal(inferida, override)`. Esa validación
  vive en el spec 08, cuando se **escribe** el override vía HTTP; acá el resolver solo lo consume.
- **Sí:** override con `ExpiresAt` opcional; vencido se ignora y se limpia. Un override permanente
  olvidado congelaría el monitoreo de ese patch sin dejar rastro.
- **Sí:** `IPhaseOverrideStore` como puerto en `Contracts` con `InMemoryPhaseOverrideStore` en
  `PatchMonitor`. El spec 05 la reemplaza por el entity workflow y el 08 la escribe vía HTTP; el
  proyecto de tests no gana `Temporalio`.
- **Sí:** `TimeProvider` inyectado en el resolver. `CleanGrace` compara `StartTime` contra "ahora";
  sin un reloj inyectable los tests de la fase 3 serían no determinísticos. `TimeProvider.System` en
  DI, `FakeTimeProvider` propio en tests.
- **No:** `DateTimeOffset.UtcNow` interno como en el desvío 3 del spec 02. Ahí el timestamp era solo
  un sello; acá el reloj participa de la **decisión** de fase.
- **No:** agregar `Microsoft.Extensions.TimeProvider.Testing`. Una subclase de `TimeProvider` de
  cinco líneas evita sumar un paquete a la suite.
- **Sí:** entregar `PhaseActivities` además del servicio, registrada por tipo concreto como
  `DiscoveryActivities`. El spec 06 la invoca; ya queda verificable desde el worker en este spec.
- **Sí:** `PhaseOptions.FromEnvironment()` con el mismo patrón tolerante de `DiscoveryOptions`.
  Consistencia con el spec 03 y ningún crash del worker por una env var mal escrita.

## Riesgos identificados

| Riesgo                                                                                                                                                | Mitigación                                                                                                                                                                          |
| ----------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Un redeploy que pasa a `DeprecatePatch` pero todavía no arrancó ninguna ejecución nueva: el resolver sigue leyendo fase 1 por las ejecuciones viejas. | Es correcto por diseño (no hay evidencia aún del código nuevo); el operador puede forzar fase 2 con el override manual. Se documenta en el XML-doc del resolver.                    |
| Una ejecución vieja rezagada sin marker (workflow que nunca escribió el patch) dispara `Clean` falso.                                                 | El caso 3 exige `StartTime > lastMarkerStart + CleanGrace` **y** cero ejecuciones abiertas con marker; una rezagada anterior al último marker no cuenta.                            |
| Un override manual olvidado congela la fase de un patch y el monitor deja de reflejar la realidad.                                                    | `ExpiresAt` opcional con descarte automático del vencido; `GetAll` expone los vigentes para que el spec 08 los liste y el operador los revise.                                      |
| El `InMemoryPhaseOverrideStore` pierde todos los overrides al reiniciar el worker.                                                                    | Aceptado y documentado: la durabilidad del override es del spec 05 (entity workflow). En este spec el override es una capacidad operativa de corta vida, no estado persistente.     |
| `TimeProvider.System` en DI pero `InMemoryPhaseOverrideStore` chequea expiración con `DateTimeOffset.UtcNow`: dos relojes distintos.                  | El store solo hace una limpieza best-effort; la decisión autoritativa de "override vigente" la toma el resolver con `IsActiveAt(clock.GetUtcNow())`. Se documenta el doble chequeo. |

## Qué NO entra en este spec

- Combinar `PhaseResolution` con `PhaseEvaluator` en un `PatchAssessment`.
- Persistir la fase resuelta o el override: entity workflows, registry singleton, `IDecisionSink`,
  `Continue-As-New`.
- La superficie HTTP que declara el override y su `[WorkflowUpdateValidator]`.
- Validar el override contra `PhaseTransition.IsLegal`.
- `MonitorWorkflow`, `ScheduleBootstrapper` y el Temporal Schedule de 5 minutos.
- `INotifier` y la detección de cambio de fase entre corridas.
- Tests con el entorno de time-skipping de `Temporalio`.

Cada uno, si entra, va en su propio spec.
