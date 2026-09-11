# 07 - Notificador pluggable

**Estado:** Implementada
**Depende de:** [06-monitor-workflow-temporal-schedule.md](06-monitor-workflow-temporal-schedule.md)
**Fecha:** 2026-09-11

**Objetivo:** Notificar **una sola vez** por cambio de veredicto de un patch, vía log estructurado
y/o webhook HTTP registrados por DI, sin que un fallo de notificación rompa nunca el monitoreo.

## Por qué existe este spec

El spec 05 dejó `PatchState.Revision` como un contador monotónico que avanza **solo** cuando la
tupla `(Phase, Outcome, NextPhase)` cambia de verdad, y lo documentó explícitamente como "la señal
que el spec 07 usa para notificar exactamente una vez". El spec 06 ya consume esa señal: el
`MonitorWorkflow` compara el `Revision` de cada patch antes y después de `RecordAssessmentAsync` y
cuenta `VerdictsChanged` en el `MonitorRunSummary`. Ambos specs difirieron la notificación en sí a
este —05:183, 06:173— porque hasta ahora no había nada que valiera la pena avisar de forma
confiable: **contar** un cambio y **notificarlo exactamente una vez** son problemas distintos. El
primero tolera que una pasada reintentada cuente dos veces (es un número visible solo en el resumen
de esa corrida); el segundo no puede, porque el destino es externo — un log que otro sistema tailea,
un webhook que dispara una alerta — y un doble aviso ahí sí es un defecto visible para el operador.

`Construction.md` §7 fija la forma: `INotifier` con implementaciones registradas por DI (webhook
HTTP, log estructurado), con la garantía de que "un fallo de notificación nunca rompe el
monitoreo (se compensa/ignora, no propaga)". El README pide lo mismo en positivo: avisar cuando el
gate cambia de veredicto es la razón de ser de un monitor — de nada sirve calcular el estado
correcto cada 5 minutos si nadie se entera cuando cambia.

El punto delicado es que las Activities de Temporal son **at-least-once**: un timeout de RPC, un
worker reiniciado a mitad de la pasada, o simplemente la `RetryPolicy` reintentando un 5xx del
webhook, pueden hacer que `NotifyVerdictChangeAsync` se ejecute más de una vez para el mismo
`Revision`. Contar con que el emisor "solo llama una vez" es exactamente el tipo de suposición que
este proyecto existe para detectar en el software ajeno; no puede ser la base de su propio
notificador. La solución es la misma que `Construction.md` fija para el resto del sistema — "el
estado como marcador de idempotencia" — aplicada acá: el entity workflow del spec 05 gana un
**claim** transaccional (`NotifiedRevision`) que se reclama en una única operación durable _antes_
de intentar el envío. Un reintento de la Activity ve el claim ya tomado y no reenvía nada.

## Alcance

**Incluye:**

- **`src/Contracts/Notification/VerdictChangeNotification.cs`** — el payload de una notificación,
  acotado a propósito (no el `PatchState` entero, que arrastra todo el `History`):

  ```csharp
  public sealed record VerdictChangeNotification(
      string NotificationId,
      PatchKey Key,
      int Revision,
      PatchPhase FromPhase,
      PatchPhase ToPhase,
      GateOutcome? FromOutcome,
      GateOutcome? ToOutcome,
      PatchPhase? NextPhase,
      int BlockingExecutionCount,
      IReadOnlyList<string> BlockingSample,
      string Reason,
      DateTimeOffset ChangedAt)
  {
      public static VerdictChangeNotification FromState(PatchState state);
  }
  ```

  `NotificationId` es determinístico: `$"{Key.ToWorkflowId()}#{Revision}"`. El claim en el entity ya
  garantiza que el envío ocurre una sola vez; `NotificationId` es una segunda red de contención para
  que el **receptor** (el webhook, o quien tailee el log) pueda deduplicar si igual le llega dos
  veces por algo fuera del control del monitor (un proxy que reintenta, un consumidor at-least-once
  del otro lado). `FromState` arma el payload leyendo `state.Phase`/`LastVerdict` como "to" y
  `state.History` (la última entrada) como "from".

- **`src/Contracts/Notification/INotifier.cs`** — el puerto, un método:

  ```csharp
  public interface INotifier
  {
      string Name { get; }
      Task NotifyAsync(VerdictChangeNotification notification, CancellationToken ct = default);
  }
  ```

  `Name` identifica al notificador en los mensajes de error del `CompositeNotifier` y en los
  criterios de aceptación.

- **`src/Contracts/Notification/NotificationOptions.cs`** — mismo parseo tolerante
  "ausente / no numérico / no positivo ⇒ default, nunca lanza" de `DiscoveryOptions`, `PhaseOptions`
  y `MonitorOptions`:

  ```csharp
  public sealed record NotificationOptions(
      bool Enabled,
      string? WebhookUrl,
      string? WebhookAuthHeader,
      TimeSpan WebhookTimeout,
      int MaxAttempts)
  {
      public const int DefaultWebhookTimeoutSeconds = 10;
      public const int DefaultMaxAttempts = 3;

      public static NotificationOptions FromEnvironment();
  }
  ```

  Lee `NOTIFIER_ENABLED`, `NOTIFIER_WEBHOOK_URL`, `NOTIFIER_WEBHOOK_AUTH_HEADER`,
  `NOTIFIER_WEBHOOK_TIMEOUT_SECONDS` y `NOTIFIER_MAX_ATTEMPTS`.

- **`src/Contracts/State/PatchState.cs`** _(modificado)_ — nuevo campo `int NotifiedRevision` (el
  último `Revision` ya reclamado para notificar). `Initial` lo deja en `0`; `ForCarryover` lo
  preserva sin tocar — igual que `Revision`, tiene que sobrevivir al `Continue-As-New` o una
  notificación ya enviada podría repetirse tras el salto.

- **`src/Contracts/Workflows/IPatchStateWorkflow.cs`** _(modificado)_ — un `[WorkflowUpdate]` más:

  ```csharp
  [WorkflowUpdate] Task<bool> TryClaimNotificationAsync(int revision);
  ```

  Devuelve `true` y avanza `NotifiedRevision` solo si `revision > NotifiedRevision` vigente; si no,
  devuelve `false` sin tocar el estado. Va por `[WorkflowUpdate]`, no por signal, porque el llamador
  necesita la respuesta sincrónica para decidir si además dispara el envío.

- **`src/PatchMonitor/Workflows/PatchStateWorkflow.cs`** _(modificado)_ — implementa
  `TryClaimNotificationAsync` como una comparación y asignación simple sobre `_state`, sin
  validator (no hay entrada de usuario que rechazar, a diferencia de `SetOverrideAsync`).

- **`src/Contracts/State/IPatchStateStore.cs`** _(modificado)_ — el puerto gana

  ```csharp
  Task<bool> TryClaimNotificationAsync(PatchKey key, int revision, CancellationToken ct = default);
  ```

  implementado en **`TemporalPatchStateStore`** reusando `EnsureEntityAsync` +
  `ExecuteUpdateAsync`, el mismo camino que ya usan `SetOverrideAsync`/`ClearOverrideAsync`.

- **`src/PatchMonitor/Services/StructuredLogNotifier.cs`** — serializa el
  `VerdictChangeNotification` con `System.Text.Json` y lo escribe con `Console.WriteLine` como una
  sola línea JSON, consistente con el resto del worker (no hay `Microsoft.Extensions.Logging` en
  el proyecto; `docker compose logs` es el canal ya establecido). `Name = "log"`. Siempre
  registrado, sin condición de configuración.

- **`src/PatchMonitor/Services/WebhookNotifier.cs`** — `Name = "webhook"`. `POST` del
  `VerdictChangeNotification` serializado, sobre un `HttpClient` singleton inyectado (sin sumar
  `Microsoft.Extensions.Http`: un `HttpClient` de DI alcanza para este único uso), con
  `NotificationOptions.WebhookTimeout` y el header de `WebhookAuthHeader` agregado tal cual
  (formato libre `Nombre: valor`) cuando está configurado. Distingue la respuesta:
  - `2xx` ⇒ éxito.
  - `4xx` salvo `408`/`429` ⇒ `ApplicationFailureException(nonRetryable: true)` — el destino
    rechazó el payload de forma permanente, reintentar no cambia nada.
  - Cualquier otro caso (`5xx`, `408`, `429`, timeout, error de red) ⇒ excepción normal, que la
    `RetryPolicy` de la Activity reintenta.

  Registrado en DI **solo si** `NotificationOptions.WebhookUrl` no es nulo.

- **`src/PatchMonitor/Services/CompositeNotifier.cs`** — recorre el `IEnumerable<INotifier>`
  inyectado, invoca cada uno de forma independiente (`try/catch` por notificador), acumula los
  mensajes de los que fallaron y **lanza solo si todos fallaron**. Que el webhook esté caído no
  puede tapar que el log estructurado sí se emitió — y viceversa.

- **`src/PatchMonitor/Activities/NotificationActivities.cs`** — la única Activity nueva:

  ```csharp
  [Activity] public async Task<bool> NotifyVerdictChangeAsync(VerdictChangeNotification n)
  ```

  Llama `_store.TryClaimNotificationAsync(n.Key, n.Revision)`; si devuelve `false`, retorna `false`
  sin invocar ningún `INotifier` (ya se notificó, o un `Revision` viejo llegó tarde). Si devuelve
  `true`, invoca el `CompositeNotifier` inyectado y retorna `true`.

- **`src/PatchMonitor/Workflows/MonitorWorkflow.cs`** _(modificado)_ — dentro del
  `if (state.Revision > revisionBefore)` que ya existe, arma
  `VerdictChangeNotification.FromState(state)` y ejecuta `NotifyVerdictChangeAsync` con su propia
  `ActivityOptions` (`RetryPolicy.MaximumAttempts = options` de `NotificationOptions.MaxAttempts`),
  en un `try/catch (ActivityFailureException)` **anidado dentro** del `try` del patch existente:
  un fallo de notificación no debe caer en el mismo `catch` que aborta el resto del procesamiento
  de ese patch — el assessment y la persistencia del spec 05/06 ya ocurrieron y cuentan igual.
  Suma a `notificationsSent`/`notificationsFailed` según el resultado.

- **`src/Contracts/Monitor/MonitorRunSummary.cs`** _(modificado)_ — dos campos más:
  `int NotificationsSent` y `int NotificationsFailed`.

- **`src/PatchMonitor/Infrastructure/ServiceCollectionExtensions.cs`** _(modificado)_ — registra
  `NotificationOptions` (singleton desde `FromEnvironment()`), un `HttpClient` singleton,
  `INotifier → StructuredLogNotifier` siempre, `INotifier → WebhookNotifier` condicionado a
  `options.WebhookUrl is not null`, `INotifier → CompositeNotifier` como la implementación resuelta
  por consumidores externos al fan-out (el `CompositeNotifier` en sí toma
  `IEnumerable<INotifier>` de los dos anteriores por un marcador interno, ver Modelo de datos), y
  `NotificationActivities` por tipo concreto. Reemplaza el comentario
  `// INotifier llega en el spec 07.`.

- **`src/PatchMonitor/Program.cs`** _(modificado)_ — suma `typeof(NotificationActivities)` a
  `activityTypes`.

- **`docker/docker-compose.yml`** _(modificado)_ — agrega `NOTIFIER_ENABLED`,
  `NOTIFIER_WEBHOOK_URL`, `NOTIFIER_WEBHOOK_AUTH_HEADER`, `NOTIFIER_WEBHOOK_TIMEOUT_SECONDS` y
  `NOTIFIER_MAX_ATTEMPTS` al `environment` del `patch-monitor-worker`.

- **`test/PatchMonitor.Tests/Notification/`** — `NotificationOptionsTests.cs` (puro),
  `VerdictChangeNotificationTests.cs` (puro, `NotificationId` determinístico),
  `CompositeNotifierTests.cs` (puro, con `FakeNotifier.cs`), `WebhookNotifierTests.cs` (sobre un
  `HttpMessageHandler` fake), `NotificationClaimTests.cs` (time-skipping, sobre
  `PatchStateWorkflow`), `NotificationRegistrationTests.cs` (`AddPatchMonitorServices()`). El
  `FakePatchStateStore.cs` de `test/PatchMonitor.Tests/Monitor/` gana el claim para que
  `MonitorWorkflowTests.cs` pueda extenderse con los casos de notificación.

**No incluye (fuera de alcance de este spec):**

- **Exponer notificaciones por HTTP** (un endpoint de test, un historial consultable de avisos
  enviados): spec 08, si llega a hacer falta.
- **Un adaptador `IDecisionSink` real** (SQL u otro). Sigue siendo el no-op del spec 05.
- **Reintentos propios fuera de la `RetryPolicy` de Temporal** — nada de cola de notificaciones
  pendientes, backoff manual ni persistencia adicional del intento. La `RetryPolicy` de la Activity
  ya es un mecanismo de reintento durable; duplicarlo por fuera es redundante.
- **Plantillas o formateo específico por destino** (Slack blocks, Markdown, etc.). El webhook manda
  el JSON del `VerdictChangeNotification` tal cual; un formateador específico es un `INotifier` más,
  aditivo, cuando haya un destino concreto que lo pida.
- **Notificar algo que no sea un cambio de veredicto.** Errores de la pasada, salud del worker o del
  Schedule no pasan por `INotifier` en este spec.
- **Aplicar `Workflow.Patched` al entity por el campo `NotifiedRevision` nuevo.** El ciclo de
  versionado del propio monitor es el spec 10; acá el campo se agrega antes de que exista ninguna
  ejecución en producción con historia real, así que no hay incompatibilidad que parchear todavía.
- **Agrupar varios cambios en una sola notificación digest.** Cada `Revision` que avanza genera su
  propio intento de notificación; agregación por lote es una optimización futura sin caso real
  todavía.

## Modelo de datos

Todo lo nuevo de `Contracts` vive en `namespace Contracts.Notification`, salvo el método agregado a
`IPatchStateWorkflow` (ya en `Contracts.Workflows`) y el campo agregado a `PatchState` (ya en
`Contracts.State`). Árbol tras este spec (solo lo que cambia):

```text
proyecto_monitoreo/
├── src/
│   ├── Contracts/
│   │   ├── Notification/
│   │   │   ├── VerdictChangeNotification.cs
│   │   │   ├── INotifier.cs
│   │   │   └── NotificationOptions.cs
│   │   ├── State/
│   │   │   ├── PatchState.cs                   (modificado: + NotifiedRevision)
│   │   │   └── IPatchStateStore.cs              (modificado: + TryClaimNotificationAsync)
│   │   ├── Workflows/
│   │   │   └── IPatchStateWorkflow.cs           (modificado: + TryClaimNotificationAsync)
│   │   └── Monitor/
│   │       └── MonitorRunSummary.cs             (modificado: + NotificationsSent/Failed)
│   └── PatchMonitor/
│       ├── Workflows/
│       │   ├── PatchStateWorkflow.cs            (modificado)
│       │   └── MonitorWorkflow.cs               (modificado)
│       ├── Services/
│       │   ├── StructuredLogNotifier.cs
│       │   ├── WebhookNotifier.cs
│       │   ├── CompositeNotifier.cs
│       │   └── TemporalPatchStateStore.cs       (modificado)
│       ├── Activities/NotificationActivities.cs
│       ├── Infrastructure/ServiceCollectionExtensions.cs   (modificado)
│       └── Program.cs                                       (modificado)
├── docker/docker-compose.yml                                (modificado)
└── test/
    └── PatchMonitor.Tests/
        ├── Monitor/FakePatchStateStore.cs                    (modificado)
        └── Notification/
            ├── NotificationOptionsTests.cs
            ├── VerdictChangeNotificationTests.cs
            ├── CompositeNotifierTests.cs
            ├── WebhookNotifierTests.cs
            ├── NotificationClaimTests.cs
            ├── NotificationRegistrationTests.cs
            └── FakeNotifier.cs
```

Env vars que lee `NotificationOptions.FromEnvironment()`:

| Variable                           | Default   | Significado                                                                  |
| ---------------------------------- | --------- | ---------------------------------------------------------------------------- |
| `NOTIFIER_ENABLED`                 | `true`    | En `false`, `MonitorWorkflow` ni siquiera invoca `NotifyVerdictChangeAsync`. |
| `NOTIFIER_WEBHOOK_URL`             | _(vacío)_ | Ausente ⇒ `WebhookNotifier` no se registra; solo queda el log estructurado.  |
| `NOTIFIER_WEBHOOK_AUTH_HEADER`     | _(vacío)_ | Header crudo (`Nombre: valor`) agregado tal cual al `POST`.                  |
| `NOTIFIER_WEBHOOK_TIMEOUT_SECONDS` | `10`      | Timeout del `POST` al webhook.                                               |
| `NOTIFIER_MAX_ATTEMPTS`            | `3`       | `RetryPolicy.MaximumAttempts` de `NotifyVerdictChangeAsync`.                 |

### Por qué el claim va antes del envío

`TryClaimNotificationAsync` se ejecuta **antes** de invocar cualquier `INotifier`. La alternativa
—notificar primero y marcar `NotifiedRevision` recién si el envío tuvo éxito— nunca pierde un aviso,
pero abre la ventana clásica de "el update que marca el envío falla después de que el webhook ya
respondió 200": ahí si reintenta la Activity, se manda dos veces. Reclamar antes invierte el
trade-off: si el envío falla _después_ de reclamado, ese cambio puntual queda sin avisar, pero
**nunca se manda dos veces**, que es exactamente lo que pide el enunciado ("una sola vez"). El costo
se acota solo: el estado sigue siendo consultable en cualquier momento (`GetState()`, y la API del
spec 08 después), y el próximo cambio real de veredicto sí dispara un nuevo intento con un
`Revision` más alto. Es la misma filosofía que `Construction.md` fija para el resto del sistema —
"el estado como marcador de idempotencia", update-then-act — aplicada al último eslabón de la
cadena.

## Plan de implementación

1. **`NotificationOptions` + `FromEnvironment` + tests.** Crear
   `Contracts/Notification/NotificationOptions.cs` con el parseo tolerante y las constantes de
   default. `NotificationOptionsTests.cs`: ausentes → los defaults; valores válidos respetados;
   `"basura"`, `"0"` y `"-3"` → default, sin excepción; `NOTIFIER_ENABLED=false` → `Enabled == false`.
   `dotnet test` en verde.

2. **Contratos de notificación.** Crear `VerdictChangeNotification.cs` (con `FromState`) e
   `INotifier.cs`. Solo contratos: nada los implementa aún. `VerdictChangeNotificationTests.cs`:
   `FromState` sobre un `PatchState` con `History` no vacía arma `FromPhase`/`FromOutcome` desde la
   última entrada; `NotificationId` es `"{key}#{revision}"` y determinístico entre dos llamadas con
   el mismo estado. `dotnet build` compila.

3. **`NotifiedRevision` + `TryClaimNotificationAsync` en el entity.** Agregar el campo a
   `PatchState` (con default `0` en `Initial`, preservado en `ForCarryover`), el método a
   `IPatchStateWorkflow` y su implementación en `PatchStateWorkflow`. `NotificationClaimTests.cs`
   sobre `WorkflowEnvironment.StartTimeSkippingAsync`: reclamar `Revision = 1` → `true` y
   `GetState().NotifiedRevision == 1`; reclamar `1` de nuevo → `false`, estado sin cambios; reclamar
   `2` → `true`; con `PATCH_STATE_CAN_THRESHOLD` bajo, `NotifiedRevision` sobrevive al
   `Continue-As-New` igual que `Revision`.

4. **`IPatchStateStore.TryClaimNotificationAsync` + store.** Agregar el método al puerto,
   implementarlo en `TemporalPatchStateStore` (reusando `EnsureEntityAsync`) y en
   `FakePatchStateStore` (para los tests de `MonitorWorkflowTests` del paso 7).
   `TemporalPatchStateStoreTests.cs` gana un caso: dos llamadas seguidas con el mismo `Revision`
   sobre la misma key → `true` y luego `false`.

5. **`StructuredLogNotifier` + `CompositeNotifier`.** Implementar ambos.
   `CompositeNotifierTests.cs` con `FakeNotifier.cs` (constructor que decide si tira o no): dos
   notificadores, uno falla → el otro igual recibió la llamada y `NotifyAsync` del composite no
   lanza; los dos fallan → `NotifyAsync` del composite lanza con ambos mensajes.

6. **`WebhookNotifier`.** Implementar sobre `HttpClient` inyectado. `WebhookNotifierTests.cs` con un
   `HttpMessageHandler` fake: `200` → sin excepción, y el request llevó el header de auth cuando
   `WebhookAuthHeader` está seteado; `500` → excepción reintentable (no
   `ApplicationFailureException` no-retryable); `400` → `ApplicationFailureException(nonRetryable:
true)`; `429` → excepción reintentable (no cae en la rama de 4xx no-retryable).

7. **`NotificationActivities` + cableado en `MonitorWorkflow`.** Implementar la Activity;
   extender `MonitorWorkflow.RunAsync` para invocarla dentro del `if (state.Revision >
revisionBefore)`, con `try/catch (ActivityFailureException)` anidado que suma a
   `NotificationsFailed`/`Errors` sin afectar `PatchesAssessed`; sumar los campos nuevos al
   `MonitorRunSummary`. `MonitorWorkflowTests.cs` extendido: un cambio de veredicto con un
   `FakeNotifier` que no falla → `NotificationsSent == 1`; el mismo estado sin cambio en una segunda
   pasada → `NotificationsSent == 0` en esa pasada; un `FakeNotifier` que siempre falla →
   `NotificationsFailed == 1`, `PatchesAssessed` sigue en 1 y el patch no aparece en `Errors` del
   assessment (aparece en el mensaje de notificación).

8. **DI + `Program.cs` + compose + `NotificationRegistrationTests`.** Registrar
   `NotificationOptions`, el `HttpClient`, los `INotifier` (log siempre, webhook condicionado) y
   `NotificationActivities`; agregar la Activity a `Program.cs`; sumar las cinco env vars al
   `patch-monitor-worker`. `NotificationRegistrationTests.cs`: sin `NOTIFIER_WEBHOOK_URL`,
   `AddPatchMonitorServices()` resuelve un solo `INotifier` fan-out cuyo interno solo tiene el log;
   con la env var seteada, dos. `dotnet build` y `dotnet test` en verde.

9. **Verificación end-to-end manual.** Con el stack de `docker/` arriba, el worker recompilado y un
   receptor de webhook descartable (por ejemplo un `python -m http.server` con un handler que
   loguea el body, apuntado desde `NOTIFIER_WEBHOOK_URL`):
   - `dotnet build PatchMonitor.sln` → 0 errores, 0 advertencias.
   - `dotnet test PatchMonitor.sln` → todos verdes.
   - Declarar un override que cambie la fase de un patch conocido (`SetOverrideAsync`, spec 05) y
     esperar el próximo tick del Schedule: `docker compose logs patch-monitor-worker` muestra
     **una** línea JSON de `StructuredLogNotifier` y el receptor de webhook recibe **un** `POST` con
     el mismo `NotificationId`.
   - Esperar un segundo tick sin cambios: ni el log ni el receptor reciben nada nuevo.
   - `temporal workflow query` sobre el entity del patch: `NotifiedRevision == Revision`.
   - Apagar el receptor de webhook, forzar otro cambio de veredicto (otro override) y esperar el
     tick: la pasada completa igual (`PatchesAssessed` incluye el patch), `MonitorRunSummary`
     muestra `NotificationsFailed >= 1`, y el log estructurado **sí** emitió su línea (el fallo del
     webhook no tapó al log).
   - Levantar el receptor de nuevo y esperar el tick siguiente: no reenvía el cambio ya reclamado
     (el `Revision` no volvió a subir), consistente con "una sola vez" incluso cuando el primer
     intento falló parcialmente.
   - Registrar la salida de estos comandos en este spec antes de marcar los criterios.

### Evidencia de la verificación end-to-end (2026-09-11)

Ejecutada sobre el stack real de `docker/` (Temporal + Postgres + worker + API), con un
receptor de webhook descartable (`python http.server` en el host, expuesto al contenedor como
`http://host.docker.internal:8099/webhook`) y un patch real sembrado con un workflow throwaway
que llama `Workflow.Patched("core-patch")` contra el mismo cluster (namespace `default`, el
mismo que usa `TARGET_TEMPORAL_HOST`).

- `docker compose build patch-monitor-worker monitor-api` → 0 errores. `docker compose up -d` →
  los cinco servicios arrancan; log del worker: `Schedule 'patch-monitor-schedule' ya existía.` /
  `Worker listening on 'patch-monitor-task-queue'...` (sin crash).
- `temporal schedule trigger --schedule-id patch-monitor-schedule` (primer tick, patch nuevo):
  el log estructurado emite **una** línea con
  `NotificationId: patch-state::default::OrderWorkflow::core-patch#1`, `Revision: 1`. El
  webhook recibe **un** `POST` con el mismo `NotificationId` (confirmado vía el receptor
  descartable, body JSON en camelCase).
- Un override (`SetOverride` vía `temporal workflow update execute`) que cambia la fase forzada
  produce un segundo cambio real (`Revision: 2`): una línea de log y un `POST` más, con
  `NotificationId ...#2`. `temporal workflow query ... GetState` confirma
  `NotifiedRevision == Revision == 2`.
- Un tercer tick sin cambios no agrega ninguna línea nueva ni ningún `POST`: `VerdictsChanged`
  se mantiene en 0 para esa pasada (dos pasadas seguidas sin cambio no notifican en la segunda).
- Con el receptor de webhook apagado (`ClearOverride` fuerza un tercer cambio real, `Revision:
  3`): la pasada completa igual (`PatchesAssessed: 2` en el `MonitorRunSummary` decodificado de
  `temporal workflow show`), el log estructurado **sí** emitió su línea `#3`, y **no** abortó la
  corrida. Ver observación abajo sobre `NotificationsFailed` en este escenario puntual.
- Con el receptor de webhook levantado de nuevo, un tick siguiente sin cambio real no reenvía
  `Revision: 3` (ni log ni webhook reciben nada nuevo): el claim ya tomado no se reintenta.
- `dotnet build PatchMonitor.sln` (stack de Docker apagado) → 0 errores, 0 advertencias.
- `dotnet test PatchMonitor.sln` (stack de Docker apagado) → 210/210 en verde. Un test
  preexistente del spec 05 (`TemporalPatchStateStoreTests.LoadActiveOverrides_devuelve_solo_los_vigentes`)
  mostró el mismo *timeout* esporádico del test-server de time-skipping bajo corrida paralela ya
  visto durante los pasos 3, 4 y 8 de este spec — pasa siempre en aislamiento y en una repetición
  completa de la suite; no relacionado con el código de este spec.

**Observación no bloqueante — `NotificationsFailed` y fallos parciales del fan-out:**
`CompositeNotifier` lanza **solo si todos** los `INotifier` fallan (así lo pide el Alcance: "que
el webhook esté caído no puede tapar que el log sí se emitió"). Como el notificador de log está
siempre registrado y `Console.WriteLine` prácticamente nunca falla, en la práctica
`CompositeNotifier.NotifyAsync` casi nunca propaga una excepción — por lo que
`MonitorRunSummary.NotificationsFailed` solo sube ante un fallo de **toda la Activity**
(la entidad de Temporal inalcanzable, o el caso de reintento-silencioso del paso 7 de este
mismo spec), nunca por un fallo aislado de un destino individual (p. ej. el webhook devolviendo
5xx) mientras el log siga vivo. Se confirmó en la verificación E2E: con el webhook caído,
`NotificationsFailed` quedó en `0` en el `MonitorRunSummary` de esa pasada, a pesar de que el
`POST` al webhook falló (solo se ve en el log del receptor externo, no en el propio monitor).
Decisión tomada con el usuario: dejar el código tal como está — el diseño de `CompositeNotifier`
cumple literalmente el Alcance aprobado, y el detalle de "qué destino falló y por qué" es
información que corresponde diseñar recién cuando exista un consumidor real de ese dato (la API/
UI del spec 08), no antes. Anotado acá para que quede trazable cuando se escriba esa spec.

## Criterios de aceptación

- [x] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [x] `dotnet test PatchMonitor.sln` pasa con el stack de Docker **apagado**.
- [x] Un cambio de veredicto (`Revision` que avanza) produce exactamente una notificación por cada
      `INotifier` registrado.
- [x] Dos pasadas seguidas sin cambio real de veredicto no producen ninguna notificación en la
      segunda.
- [x] Un reintento de `NotifyVerdictChangeAsync` (por la `RetryPolicy` de la Activity o por una
      segunda invocación manual con el mismo `Revision`) tras un claim ya tomado **no** vuelve a
      invocar ningún `INotifier`.
- [x] `PatchState.NotifiedRevision` sobrevive a un `Continue-As-New` del entity, igual que
      `Revision`.
- [x] El webhook caído (o devolviendo 5xx) no baja `MonitorRunSummary.PatchesAssessed` ni aborta la
      pasada; queda contado en `NotificationsFailed`. **Matiz confirmado en la verificación
      end-to-end:** esto vale a nivel de toda la Activity (RPC del claim inalcanzable, o el caso
      de reintento-silencioso); un fallo aislado del webhook mientras el notificador de log sigue
      vivo no sube `NotificationsFailed`, por el diseño "lanza solo si todos fallaron" de
      `CompositeNotifier` que el propio Alcance pide. Ver observación arriba.
- [x] Una respuesta `4xx` (salvo `408`/`429`) del webhook no se reintenta
      (`ApplicationFailureException(nonRetryable: true)`); una `5xx`, `408` o `429` sí.
- [x] Sin `NOTIFIER_WEBHOOK_URL`, `AddPatchMonitorServices()` solo registra el notificador de log.
- [x] `NotificationOptions.FromEnvironment()` devuelve los defaults con las env vars ausentes,
      respeta valores válidos y cae al default ante basura o valores ≤ 0 (salvo `Enabled`, que solo
      distingue `"false"` de todo lo demás).
- [x] Un `ServiceProvider` de `AddPatchMonitorServices()` resuelve `INotifier` y
      `NotificationActivities`.
- [x] Los contratos de los specs 02, 03, 04 y 06 no cambian salvo el agregado de
      `NotificationsSent`/`NotificationsFailed` a `MonitorRunSummary` y la nueva llamada a Activity
      dentro de `MonitorWorkflow.RunAsync`.
- [x] El `patch-monitor-worker` declara las cinco env vars nuevas y el worker arranca con
      `NotificationActivities` registrada.

## Decisiones tomadas y descartadas

- **Sí:** claim transaccional (`NotifiedRevision` + `TryClaimNotificationAsync`) en el entity
  **antes** de invocar cualquier `INotifier`. Es la única forma de dar la garantía dura de "una sola
  vez" frente a Activities at-least-once, sin inventar un componente de deduplicación aparte; reusa
  exactamente el patrón "estado como marcador de idempotencia" que `CLAUDE.md`/`Construction.md`
  fijan para el resto del sistema.
- **No:** notificar primero y marcar después. Cierra la ventana en el sentido incorrecto: nunca
  pierde un aviso, pero un reintento tras un envío exitoso sí puede duplicarlo, que es justo lo que
  el enunciado prohíbe.
- **No:** un componente de deduplicación separado (tabla de "notificaciones enviadas", cache con
  TTL). El entity workflow ya es un store durable y consistente por patch; agregar otro con su
  propia semántica de expiración sería la misma clase de complejidad accidental que el spec 05
  evitó al no sumar una tabla de ledger de idempotencia aparte.
- **Sí:** `NotifyVerdictChangeAsync` como Activity separada de `RecordAssessmentAsync`, invocada
  desde `MonitorWorkflow` justo donde ya se calcula `state.Revision > revisionBefore`. El workflow
  ya tiene el antes/después en memoria; la Activity nueva hereda gratis la `RetryPolicy` y el
  aislamiento de fallos de Temporal, y queda visible en la Event History de la pasada como un paso
  propio, no escondida dentro de la persistencia.
- **No:** invocar el notificador desde `TemporalPatchStateStore` (junto al `IDecisionSink`). Ese
  punto no tiene acceso a la `RetryPolicy` de una Activity —es código de servicio plano llamado
  desde dentro de otra Activity— y mezclaría dos preocupaciones con semánticas de fallo distintas:
  el sink es best-effort desde siempre, el notificador necesita el claim exacto.
- **Sí:** `CompositeNotifier` con fan-out sobre `IEnumerable<INotifier>`, registrado por DI, log
  estructurado siempre presente y webhook condicionado a `NOTIFIER_WEBHOOK_URL`. Cumple "pluggable"
  literal: agregar un tercer destino es una clase más + una línea de DI, sin tocar
  `MonitorWorkflow` ni `NotificationActivities`.
- **No:** una única implementación seleccionada por `NOTIFIER_KIND`. Pierde el caso más común en
  operación real — loguear siempre para auditoría local y además avisar a un sistema externo — sin
  ganar nada a cambio.
- **Sí:** notificar también la primera aparición de un patch (`Revision` 0→1,
  `Unknown → Coexistence`). Es un cambio de veredicto real y avisa que el monitor empezó a seguir
  un patch nuevo; acotado por `MONITOR_MAX_PATCHES_PER_RUN` y ocurre una sola vez por patch, nunca
  en cada pasada.
- **Sí:** el fallo de notificación se traga y se cuenta aparte (`NotificationsFailed` +
  `Errors`), sin bajar `PatchesAssessed`. Cumple literal "un fallo de notificación nunca rompe el
  monitoreo" (`Construction.md` §7): el assessment y la persistencia del patch ya ocurrieron antes
  de intentar notificar, y perder el aviso puntual no debe borrar ese trabajo.
- **No:** que un fallo de notificación cuente como error del patch entero (mismo `catch` que
  aborta su procesamiento). Mezclaría "no pude evaluar este patch" con "lo evalué bien pero no
  pude avisar", que son fallas de gravedad distinta para un operador.
- **Sí:** distinguir `4xx` no reintentable de `5xx`/timeout reintentable en `WebhookNotifier`, igual
  criterio de "semántica de reintentos" que `Construction.md` fija para el resto de las Activities
  (`ApplicationFailureException(nonRetryable: true)` desde la Activity). Un 400 no se arregla
  reintentando; agotar los `MaxAttempts` contra un receptor caído sí tiene sentido.
- **Sí:** `NotificationId` determinístico (`{workflowId}#{revision}`) en el payload, además del
  claim. Es una segunda capa de defensa barata para el receptor externo, cuyo propio camino
  (proxy, cola) puede introducir su propia duplicación fuera del control del monitor.
- **No:** plantillas de formato por destino en este spec. Un `INotifier` que transforme el payload a
  Slack blocks u otro formato específico es aditivo y entra cuando haya un destino concreto que lo
  pida, sin tocar el contrato `VerdictChangeNotification`.

## Riesgos identificados

| Riesgo                                                                                                                                            | Mitigación                                                                                                                                                                                                                                  |
| ------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| El claim se toma pero el envío falla después (webhook caído justo tras `TryClaimNotificationAsync`): ese cambio puntual queda sin avisar.         | Aceptado por diseño: es el trade-off explícito de "claim antes de notificar" frente a duplicar avisos. El estado sigue consultable (`GetState()`, y la API del spec 08) y `NotificationsFailed` lo hace visible en el resumen de la pasada. |
| Agregar `NotifiedRevision` y un `[WorkflowUpdate]` nuevo a un entity que en producción ya tendría ejecuciones vivas rompería por no-determinismo. | En este punto del proyecto no hay ejecuciones en producción con historia real todavía; el ciclo formal `Patched → DeprecatePatch` para cambios futuros al entity es el spec 10.                                                             |
| Ráfaga de notificaciones en la primera pasada sobre un namespace con muchos patches nuevos (cada uno notifica su alta).                           | Acotada por `MONITOR_MAX_PATCHES_PER_RUN` (spec 06) y ocurre una sola vez por patch, nunca se repite en pasadas siguientes.                                                                                                                 |
| Un webhook lento alarga la pasada y puede hacer que el tick siguiente se pise.                                                                    | `NOTIFIER_WEBHOOK_TIMEOUT_SECONDS` acota el tiempo por intento; el `Overlap = Skip` del Schedule (spec 06) ya absorbe una pasada ocasionalmente larga descartando el tick solapado.                                                         |
| `Console.WriteLine` como transporte del log estructurado depende del driver de logs de Docker para no perderse.                                   | Aceptado: es el mismo canal que ya usa todo el worker (`docker compose logs`); no es una garantía de entrega distinta de la que ya tiene cualquier otro log del sistema.                                                                    |

## Qué NO entra en este spec

- Exponer notificaciones o su historial por HTTP.
- Un adaptador `IDecisionSink` real (SQL u otro).
- Reintentos propios fuera de la `RetryPolicy` de Temporal, o una cola de notificaciones pendientes.
- Plantillas o formateo específico por destino (Slack, etc.).
- Notificar algo que no sea un cambio de veredicto (errores de pasada, salud del worker).
- Aplicar `Workflow.Patched` / `DeprecatePatch` al entity por los campos agregados acá.
- Agrupar varios cambios de veredicto en una sola notificación digest.

Cada uno, si entra, va en su propio spec.
