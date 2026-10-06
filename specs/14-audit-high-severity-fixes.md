# 14 - Correcciones de severidad alta de la auditoría

**Estado:** Implementado
**Depende de:** [02-patch-lifecycle-domain-model.md](02-patch-lifecycle-domain-model.md), [05-durable-state-entity-workflows.md](05-durable-state-entity-workflows.md), [06-monitor-workflow-temporal-schedule.md](06-monitor-workflow-temporal-schedule.md), [07-pluggable-notifier.md](07-pluggable-notifier.md)
**Fecha:** 2026-10-02

**Objetivo:** que un fallo del notificador nunca cuente como enviado, que una run `ContinuedAsNew`
no bloquee los gates y que los workflows del monitor no lean el entorno, dejando la suite en verde
de forma estable.

## Por qué existe este spec

`Audtioria_pathmonitor.md` (commit `dc208ec`) reportó 21 hallazgos: 4 altos, 9 medios y 8 bajos. No
caben en un solo spec, así que se dividen por severidad: este spec cubre los altos (A-1 a A-4); los
medios (M-2 a M-9) irán en el spec 15 y los bajos más la seguridad opcional (M-1, B-7) en el spec 16.

Los cuatro altos atacan funciones centrales: notificar, decidir si un gate está bloqueado y la
determinación de los propios workflows del monitor.

## Alcance

**Incluye:**

- **A-1 — Notificación (orden consultar → enviar → reclamar).**
  - `src/PatchMonitor/Activities/NotificationActivities.cs`: leer el estado con
    `IPatchStateStore.GetStateAsync`; si `NotifiedRevision >= n.Revision` devolver `false` sin
    enviar. Si no, invocar `CompositeNotifier.NotifyAsync` (si lanza, la activity falla y la
    `RetryPolicy` con `NOTIFIER_MAX_ATTEMPTS` reintenta) y recién tras el éxito llamar a
    `TryClaimNotificationAsync`. Devuelve `true` tras un envío exitoso aunque el claim devuelva
    `false` (otra corrida reclamó en paralelo: duplicado aceptado, at-least-once).
  - `src/PatchMonitor/Services/CompositeNotifier.cs`: lanza si **cualquier** notificador falla
    (mensaje con todos los fallos), no solo si fallan todos. Actualizar su doc-comment.
  - `src/PatchMonitor/Workflows/MonitorWorkflow.cs`: notificar cuando las notificaciones están
    habilitadas y `state.NotifiedRevision < state.Revision` (no solo cuando la `Revision` acaba de
    avanzar), de modo que un fallo se reintente en los ticks siguientes. Se notifica solo el estado
    vigente (un aviso, no uno por revisión intermedia). `verdictsChanged` sigue midiéndose con
    `revisionBefore`. Un `false` de la activity pasa a significar "ya notificado" y deja de contarse
    como `notificationsFailed`; se reemplaza el comentario de las líneas 99-104.
  - Sin cambios en `PatchStateWorkflow` por A-1: no exige `Workflow.Patched`.

- **A-2 — `ContinuedAsNew` es una run cerrada.**
  - `src/Contracts/Domain/ExecutionStatus.cs`: `IsOpen()` pasa a ser `true` solo para `Running`;
    actualizar el doc-comment.
  - `specs/02-patch-lifecycle-domain-model.md` (líneas ~50 y ~332): nota de corrección con la razón
    (en Temporal una run `CONTINUED_AS_NEW` está cerrada; su sucesora aparece como `Running` y se
    evalúa por su cuenta) y referencia a este spec.

- **A-3 — Sin lectura del entorno dentro de código de workflow.**
  - Nuevo record `MonitorRunConfig` en `src/Contracts/Monitor/`.
  - Nueva activity `ConfigActivities.GetMonitorRunConfig()` en `src/PatchMonitor/Activities/`,
    construida con los singletons `MonitorOptions` y `NotificationOptions` ya registrados en
    `src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs`; registrada en DI y en la lista
    de tipos de `WorkerHost`. `MonitorWorkflow.RunAsync` la ejecuta primero y deja de llamar a
    `*.FromEnvironment()`; el resultado queda grabado en la historia.
  - `IPatchStateWorkflow.RunAsync(PatchKey key, PatchState? carryover, StateOptions? options)`
    e `IPatchRegistryWorkflow.RunAsync(PatchRegistryState? carryover, StateOptions? options)`
    (`src/Contracts/Workflows/`). En `PatchStateWorkflow` (`[WorkflowInit]`) y en
    `PatchRegistryWorkflow`: `_options = options ?? StateOptions.FromEnvironment()`. El fallback al
    entorno es **solo el camino legado** de ejecuciones vivas arrancadas sin el argumento, para que
    su replay no cambie; el `Continue-As-New` pasa `_options` como argumento, así que tras su
    próximo CAN esas ejecuciones quedan determinísticas. Reescribir el comentario de
    `PatchStateWorkflow.cs:44-45`.
  - **Nota de corrección (spec 17, 2026-10-02):** el fallback al entorno no cerraba A-3 para las
    ejecuciones antiguas. Esas ejecuciones nunca llegan al umbral, así que nunca hacen su "próximo
    CAN" y siguen leyendo el entorno: la prueba e2e de cierre bajó `PATCH_STATE_CAN_THRESHOLD` y
    rompió el replay de 3 de 6 entities (`Nondeterminism error`). El
    [spec 17](17-legacy-entity-options-migration.md) elimina el fallback y las migra
    automáticamente.
  - `src/Common/State/TemporalPatchStateStore.cs`: pasar su `_options` (ya inyectado) como argumento
    en los start de `SignalWithStartAsync` (entity y registry) y de `EnsureEntityAsync`.
  - No exige `Workflow.Patched`: agregar un argumento opcional y elegir de dónde sale el umbral no
    emite comandos distintos mientras el valor sea el mismo.

- **A-4 — Suite estable.**
  - `[Collection(EnvVarCollection.Name)]` en toda clase de test que arranque `PatchStateWorkflow` o
    `PatchRegistryWorkflow` sin opciones explícitas (`PatchStateWorkflowTests`,
    `PatchRegistryWorkflowTests` y las que aparezcan con grep de `StartWorkflowAsync`).
  - Donde sea posible, los tests que dependen del umbral pasan `StateOptions` por argumento en vez
    de mutar el entorno.

- **Documentación.**
  - `README.md`: semántica nueva de notificación (reintento entre ticks, ráfaga única de avisos al
    habilitar notificaciones con atraso, duplicado posible) y que un cambio en `PATCH_STATE_*` aplica
    a cada entity tras su próximo Continue-As-New.
  - `Construction.md` §7: filas 14, 15 y 16.
  - `CLAUDE.md`, "Estado actual": sumar el spec 14 al cerrarlo.

**Fuera de alcance (para otro spec):**

- Hallazgos medios M-2 a M-9 (spec 15) y bajos B-1 a B-8 (spec 16).
- Seguridad: token de la API (M-1) y contenedores no-root (B-7), opcionales en el spec 16.
- `Workflow.Patched` en `MonitorWorkflow`: es efímero y el `WorkerHost` drena 30 s en SIGTERM.
- Notificar cada revisión intermedia: solo se avisa el estado vigente.
- Auto-versionado del `MonitorWorkflow` (spec 10, diferido).

## Modelo de datos

```csharp
// src/Contracts/Monitor/MonitorRunConfig.cs
public sealed record MonitorRunConfig(
    int MaxPatchesPerRun,
    bool NotificationsEnabled,
    int NotifierMaxAttempts);
```

Cambios de firma, sin records nuevos: `IPatchStateWorkflow.RunAsync` y `IPatchRegistryWorkflow.RunAsync`
ganan un `StateOptions? options` final (obligatorio en la interfaz: C# no admite argumentos opcionales omitidos en expression trees; el `= null` vive solo en las clases de workflow, para historias viejas con menos argumentos). `PatchState` no cambia (ya tiene `NotifiedRevision`).

## Plan de implementación

Cada paso deja el sistema compilando y con `dotnet test` en verde.

1. **A-4 inmediato.** Agregar `[Collection(EnvVarCollection.Name)]` a las clases afectadas. Correr
   la suite completa tres veces seguidas.
2. **A-2.** Cambiar `IsOpen()` y ajustar los tests existentes que asumían lo contrario. Tests nuevos
   en `test/PatchMonitor.Tests/Domain/CoexistenceToDeprecatedGateTests.cs` (run `ContinuedAsNew` sin
   marker + sucesora `Running` con marker ⇒ `Ready`) y el análogo en `DeprecatedToCleanGateTests.cs`.
   Nota de corrección en el spec 02.
3. **A-3a.** `MonitorRunConfig`, `ConfigActivities` y su uso en `MonitorWorkflow`. Test de registro
   DI y test en `MonitorWorkflowTests` con `MaxPatchesPerRun = 1` respetado.
4. **A-3b.** Argumento `StateOptions?` en ambos entity workflows y en `TemporalPatchStateStore`.
   Tests: un umbral pasado por argumento provoca el CAN sin tocar el entorno; el CAN arrastra las
   opciones; con `null` se cae al entorno (camino legado).
5. **A-1a.** `CompositeNotifier` lanza ante cualquier fallo; actualizar `CompositeNotifierTests`.
6. **A-1b.** Orden consultar → enviar → reclamar en `NotificationActivities` y condición
   `NotifiedRevision < Revision` en `MonitorWorkflow`. Tests en `NotificationClaimTests` y
   `MonitorWorkflowTests` con `FakeNotifier`: notificador que falla ⇒ `notificationsFailed = 1` y
   `NotifiedRevision` sin avanzar; el run siguiente con notificador sano envía y deja
   `NotifiedRevision == Revision`; ya notificado ⇒ no reenvía.
7. **Documentación.** README, `Construction.md` y `CLAUDE.md` según Alcance.

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa en verde tres corridas completas seguidas, sin Docker.
- [x] `FromEnvironment()` en `src/PatchMonitor/Workflows/` aparece solo en el fallback legado
      `options ?? StateOptions.FromEnvironment()`.
- [x] Un notificador que falla deja `NotifiedRevision` sin avanzar y el run siguiente reintenta el
      envío (test).
- [x] Un patch ya notificado no se vuelve a enviar (test).
- [x] Un test de gate con una run `ContinuedAsNew` sin marker más su sucesora `Running` con marker
      da `Ready`.
- [x] `CompositeNotifier` lanza cuando falla un solo notificador (test).
- [x] `specs/02-...`, `README.md` y `Construction.md` reflejan los cambios.

## Decisiones

- **Sí:** consultar → enviar → reclamar. Corrige el falso "enviado" sin tocar un entity vivo.
- **No:** claim reversible con un update nuevo en `PatchStateWorkflow`; tocaría un entity que nunca
  cierra y obligaría a `Workflow.Patched`.
- **Sí:** `CompositeNotifier` falla ante cualquier fallo. El log local nunca falla y absolvía al
  webhook caído.
- **Sí:** reintento entre ticks vía `NotifiedRevision < Revision`, notificando solo el estado
  vigente. Una ráfaga única al habilitar notificaciones con atraso queda documentada.
- **Sí:** activity para las opciones del monitor y argumento de arranque para los entity workflows.
- **No:** pasar las opciones desde el Schedule (acopla con M-3: habría que actualizar el Schedule
  para cambiar un valor) ni fijarlas como constantes (pierde configurabilidad).
- **Sí:** fallback al entorno solo en el camino legado de ejecuciones vivas.
- **No:** defaults constantes para `null`: si el entorno difería del default, el replay de una
  ejecución viva podría fallar con `NonDeterminismError`.
- **No:** `Workflow.Patched` en `MonitorWorkflow`. Las corridas duran segundos y el drenaje es de
  30 s; a lo sumo falla un tick y el siguiente lo reemplaza.
  **Corrección (spec 18):** esa frase era falsa. Con `Overlap = Skip` y sin tope de ejecución, una
  corrida con replay roto no falla: reintenta sin fin y bloquea los ticks siguientes. El spec 18 hace
  fallar el no-determinismo en `MonitorWorkflow` y le pone `ExecutionTimeout` al Schedule.
- **Sí:** `ContinuedAsNew` fuera de `IsOpen()`, coherente con la restricción #3 de `Construction.md`.

## Riesgos identificados

| Riesgo | Mitigación |
| --- | --- |
| Ráfaga de avisos al habilitar notificaciones con entities atrasados | Un aviso por patch (estado vigente); documentado en README |
| Duplicado si el envío sale bien y el claim falla | At-least-once aceptado y documentado |
| Webhook caído de forma permanente reintenta en cada tick | Queda en `errors` y `notificationsFailed` del run; visible en `/runs` |
| Corrida de `MonitorWorkflow` en vuelo durante el deploy | ~~Falla ese tick; el siguiente la reemplaza~~ **Corregido en el spec 18:** la corrida quedaba abierta y el Schedule saltaba todos los ticks; ahora falla al instante (`FailureExceptionTypes`) o se corta a los 15 min (`ExecutionTimeout`) |
| Tests existentes que asumían `ContinuedAsNew` abierta | Se ajustan en el paso 2 |

## Qué **no** está en este spec

- Hallazgos medios (M-2 a M-9) ni bajos (B-1 a B-8).
- Autenticación de la API (M-1) y contenedores no-root (B-7).
- `Workflow.Patched` en `MonitorWorkflow`.
- Notificar cada revisión intermedia.

Cada uno de estos, si hace falta, va en su propio spec.
