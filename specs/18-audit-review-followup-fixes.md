# 18 - Huecos de la revisión del cierre de la auditoría

**Estado:** Aprobado
**Depende de:** [06-monitor-workflow-temporal-schedule.md](06-monitor-workflow-temporal-schedule.md), [07-pluggable-notifier.md](07-pluggable-notifier.md), [14-audit-high-severity-fixes.md](14-audit-high-severity-fixes.md), [15-audit-medium-severity-fixes.md](15-audit-medium-severity-fixes.md), [17-legacy-entity-options-migration.md](17-legacy-entity-options-migration.md)
**Fecha:** 2026-10-06

**Objetivo:** que una corrida del monitor nunca quede colgada en silencio, que un rechazo 4xx o un
timeout del webhook se clasifiquen bien a través del notificador compuesto, y que `Clean` no se
conserve cuando hay ejecuciones nuevas sin leer.

## Por qué existe este spec

La revisión del cierre de la auditoría (2026-10-06, sobre el zip con los specs 14 a 17) confirmó 20
de 21 hallazgos cerrados, pero encontró dos huecos de severidad alta, uno medio y un comentario
falso. Los cuatro se verificaron contra el código.

- **Corrida colgada.** El Schedule usa `Overlap = Skip` (`ScheduleBootstrapper.cs:32`) y la acción no
  fija `ExecutionTimeout`. El worker no declara el no-determinismo como fallo de workflow. Una corrida
  en vuelo durante un deploy que choque con el código nuevo no falla: su workflow task se reintenta
  sin fin, la corrida queda abierta y el Schedule salta todos los ticks siguientes. El monitor deja
  de evaluar sin avisar. La frase "falla ese tick y el siguiente lo reemplaza" de los specs 14, 15 y
  17 es falsa: solo valdría si la corrida fallara.
- **A-1 a medias.** `CompositeNotifier` envuelve cualquier fallo en `InvalidOperationException`, que
  Temporal reintenta: el `WebhookRejected` no reintentable del 4xx pierde la marca. Además, su
  `catch` excluye `OperationCanceledException`, y el timeout del webhook (`CancelAfter`) sale como
  `TaskCanceledException`: corta el fan-out a los demás notificadores. El 4xx solo está probado en
  `WebhookNotifierTests`, no a través del compuesto.
- **M-6 sin mirar lo no leído.** `KeepCleanWithoutNewEvidence` solo sale de `Clean` ante una
  ejecución nueva con marker legible. Si las ejecuciones posteriores no se pudieron leer
  (`Marker = Unknown`), conserva `Clean` sin evidencia, contra la regla del monitor de decir "no sé"
  antes que dar un estado falso.
- **Comentario falso.** `src/MonitorApi/Program.cs:19-20` dice que `ConnectAsync` reintenta; es la
  misma afirmación que M-4 corrigió en `docker-compose.yml`.

## Alcance

**Incluye:**

- **Corrida colgada — no-determinismo.**
  - `src/PatchMonitor/Workflows/MonitorWorkflow.cs`:
    `[Workflow(FailureExceptionTypes = new[] { typeof(WorkflowNondeterminismException) })]`.
    Una corrida que no reproduce su historia falla al instante y el tick siguiente corre normal.
  - Solo en `MonitorWorkflow`. `PatchStateWorkflow`, `PatchRegistryWorkflow` y `HealthWorkflow` no
    cambian: para una entity, quedar trabada hasta un deploy corregido es mejor que perder el estado.
  - Es metadata de la definición, no código de workflow: no exige `Workflow.Patched`.
- **Corrida colgada — tope de ejecución.**
  - `src/Contracts/Monitor/MonitorOptions.cs`: nuevo campo `TimeSpan RunTimeout`, leído de
    `MONITOR_RUN_TIMEOUT_MINUTES`, default `DefaultRunTimeoutMinutes = 15`. Ausente, no numérica o no
    positiva ⇒ default; nunca lanza.
  - `src/Common/Temporal/ScheduleBootstrapper.cs`: `BuildAction` fija
    `WorkflowOptions.ExecutionTimeout = options.RunTimeout`. `Differs` compara también
    `action.Options.ExecutionTimeout`, así que el create-or-update del spec 15 lo aplica al Schedule
    existente con solo reiniciar el worker.
  - Cubre cualquier otro cuelgue (por ejemplo, activities reintentando sin tope ante un cluster
    caído). Las corridas ya en vuelo al desplegar no lo heredan; solo las nuevas.
- **Notificador compuesto.**
  - `src/PatchMonitor/Services/WebhookNotifier.cs`: si el envío lanza `OperationCanceledException` y
    el `ct` del llamador **no** está cancelado (venció `WebhookTimeout`), relanza `TimeoutException`
    con el timeout en el mensaje. Es reintentable.
  - `src/PatchMonitor/Services/CompositeNotifier.cs`:
    - El `catch` atrapa todo salvo una `OperationCanceledException` con el `ct` propio cancelado (esa
      sí propaga). Un timeout de un destino ya no corta a los demás.
    - Si **todos** los fallos son `ApplicationFailureException` con `NonRetryable = true`, lanza
      `ApplicationFailureException` con `errorType: "NotificationRejected"` y `nonRetryable: true`,
      con los mensajes de cada destino.
    - Si al menos uno es reintentable, lanza `InvalidOperationException` como hoy (reintentable).
  - Sin cambios en `NotificationActivities`: un fallo nunca reclama la revisión (spec 14). Un 4xx
    deja de reintentarse dentro del tick; el tick siguiente vuelve a intentarlo una vez.
- **M-6 con ejecuciones sin leer.**
  - `src/PatchMonitor/Services/PhaseResolver.cs`, en `KeepCleanWithoutNewEvidence`: después del
    chequeo de marker nuevo, si alguna ejecución con `Marker = Unknown` arrancó después de
    `previous.LastChangedAt` ⇒ devuelve `Unknown` inferido con motivo
    `no se puede confirmar Clean: <n> ejecuciones posteriores a <since:o> sin leer`.
  - Solo cuentan las `Unknown` posteriores a la entrada a `Clean`. Un `LimitReached` del listado sin
    ejecuciones `Unknown` posteriores sigue conservando `Clean`.
- **Comentario.** `src/MonitorApi/Program.cs:19-20`: la conexión **no** reintenta; si el cluster no
  está, el proceso muere y `restart: unless-stopped` lo levanta (spec 15, M-4).
- **Documentación.**
  - Notas de corrección, con referencia a este spec, en la fila "Corrida de `MonitorWorkflow` en vuelo
    durante el deploy" de los riesgos de `specs/14-audit-high-severity-fixes.md` (también la línea
    162), `specs/15-audit-medium-severity-fixes.md` y `specs/17-legacy-entity-options-migration.md`.
  - `README.md`: `MONITOR_RUN_TIMEOUT_MINUTES` en la tabla de variables; un 4xx del webhook no se
    reintenta en el tick pero sí en el siguiente.
  - `docker/docker-compose.yml`: `MONITOR_RUN_TIMEOUT_MINUTES=15` junto a las demás `MONITOR_*`.
  - `CLAUDE.md` ("Estado actual" y la convención de notificación), `Construction.md` §7 (fila 18) y la
    guía visual (artefacto citado en `CLAUDE.md`) si lista las variables `MONITOR_*`.
  - `docs/e2e/spec-18-evidence.md`: evidencia de la verificación en vivo.

**Fuera de alcance (para otro spec):**

- M-1: token / autenticación de la API.
- Auto-versionado del `MonitorWorkflow` (spec 10, diferido). Este spec no lo reemplaza: solo evita que
  un replay roto deje al monitor mudo.
- Reclamar como "rechazada" una revisión con 4xx para no reintentarla nunca más.
- Que el no-determinismo falle los entity workflows.
- Que la inferencia de `Clean` del caso 3 (spec 04/13) considere ejecuciones `Unknown`; este spec
  solo toca la regla de conservación de M-6.
- Recuperar la memoria de `Clean` después de pasar a `Unknown` por ejecuciones sin leer.

## Modelo de datos

Ningún record que viaje en historias de entities cambia.

```csharp
// src/Contracts/Monitor/MonitorOptions.cs — campo nuevo al final
public sealed record MonitorOptions(
    string ScheduleId,
    TimeSpan Interval,
    TimeSpan CatchupWindow,
    int MaxPatchesPerRun,
    string TaskQueue,
    TimeSpan RunTimeout);

public const int DefaultRunTimeoutMinutes = 15; // MONITOR_RUN_TIMEOUT_MINUTES
```

- Error de notificación permanente: `ApplicationFailureException`, `errorType = "NotificationRejected"`,
  `nonRetryable = true`.
- Timeout de webhook: `TimeoutException` (reintentable).
- La acción del Schedule gana `ExecutionTimeout`; no hay `RunTimeout` aparte (el `MonitorWorkflow` no
  hace Continue-As-New).

## Plan de implementación

Cada paso deja `dotnet build` sin errores y `dotnet test` en verde.

1. **Comentario de `MonitorApi/Program.cs`.** Sin tests.
2. **M-6.** Regla nueva en `KeepCleanWithoutNewEvidence`. Tests en `PhaseResolverTests`:
   - previo `Clean` + ejecución `Unknown` posterior a `LastChangedAt` ⇒ `Unknown` con el motivo;
   - previo `Clean` + `Unknown` solo anteriores a `LastChangedAt` ⇒ se conserva `Clean`;
   - previo `Clean` + `IsTruncated` por listado sin `Unknown` posteriores ⇒ se conserva `Clean`;
   - los tests vigentes de M-6 siguen en verde.
3. **`WebhookNotifier`: timeout ⇒ `TimeoutException`.** Test en `WebhookNotifierTests` con un handler
   lento y `WebhookTimeout` corto; otro con el `ct` del llamador cancelado ⇒ propaga
   `OperationCanceledException`.
4. **`CompositeNotifier`: clasificación y fan-out.** Tests en `CompositeNotifierTests`:
   - todos fallan no reintentables ⇒ `ApplicationFailureException` no reintentable,
     `ErrorType == "NotificationRejected"`;
   - uno no reintentable y otro reintentable ⇒ `InvalidOperationException`;
   - un destino lanza `OperationCanceledException` sin cancelar el `ct` ⇒ el otro igual recibe la
     llamada y el compuesto lanza;
   - `ct` cancelado ⇒ propaga `OperationCanceledException`.
5. **4xx a través del workflow.** Test en `MonitorWorkflowTests` (time-skipping): `NotifierMaxAttempts
   = 3`, un notificador que rechaza no reintentable dentro del `CompositeNotifier` real ⇒ se llama una
   sola vez, `NotificationsFailed == 1` y la revisión queda sin reclamar.
6. **`MonitorOptions.RunTimeout`.** Campo, constante y lectura de `MONITOR_RUN_TIMEOUT_MINUTES`.
   Tests en `MonitorOptionsTests` (default, valor válido, no numérico, cero/negativo). Ajustar el
   `new MonitorOptions(...)` de `MonitorWorkflowTests`.
7. **Schedule.** `BuildAction` con `ExecutionTimeout`; `Differs` lo compara. Tests en
   `ScheduleBootstrapperTests`: la acción lleva el timeout; `Differs` es `true` si cambia y `false` si
   coincide.
8. **`FailureExceptionTypes` en `MonitorWorkflow`.** Test que construye
   `WorkflowDefinition.Create(typeof(MonitorWorkflow))` y comprueba que `FailureExceptionTypes`
   contiene `WorkflowNondeterminismException`, y que las definiciones de `PatchStateWorkflow` y
   `PatchRegistryWorkflow` no la contienen.
9. **Verificación en vivo** contra el Temporal de `ssy-yardflow`, con el worker desechable y el webhook
   a demanda de `docs/e2e/audit-closure-evidence.md`:
   - el Schedule existente pasa a tener `ExecutionTimeout` 15 min al reiniciar el worker
     (`Updated` en el log);
   - webhook respondiendo `400` ⇒ un solo intento en el tick, `notificationsFailed: 1`, la revisión no
     se reclama; restaurado ⇒ el tick siguiente la envía;
   - webhook que tarda más que `NOTIFIER_WEBHOOK_TIMEOUT_SECONDS` ⇒ el log local igual recibe la
     llamada y la activity se reintenta;
   - no-determinismo: arrancar una corrida, detener el worker a mitad, desplegar un `MonitorWorkflow`
     con una activity extra antes de las demás y comprobar que la corrida queda `Failed` (no abierta)
     y que el tick siguiente corre. Revertir el cambio.
   - Evidencia en `docs/e2e/spec-18-evidence.md`.
10. **Documentación** según el alcance.

## Criterios de aceptación

- [ ] `dotnet build PatchMonitor.sln` compila con 0 errores y 0 advertencias.
- [ ] `dotnet test PatchMonitor.sln` pasa en verde sin Docker.
- [ ] La definición de `MonitorWorkflow` declara `WorkflowNondeterminismException` como fallo; las de
      `PatchStateWorkflow` y `PatchRegistryWorkflow` no (test).
- [ ] La acción del Schedule lleva `ExecutionTimeout` igual a `MonitorOptions.RunTimeout` y `Differs`
      detecta un cambio de ese valor (test).
- [ ] `MONITOR_RUN_TIMEOUT_MINUTES` ausente, no numérica o no positiva ⇒ 15 min, sin lanzar (test).
- [ ] Un rechazo 4xx a través de `CompositeNotifier` se intenta una sola vez por tick y no reclama la
      revisión (test de workflow).
- [ ] Fallos mixtos (no reintentable + reintentable) ⇒ reintentable (test).
- [ ] Un timeout de un destino no impide que los demás reciban la llamada (test).
- [ ] Previo `Clean` con ejecuciones `Unknown` posteriores a `LastChangedAt` ⇒ `Unknown` con motivo;
      sin ellas se conserva `Clean` (test).
- [ ] `src/MonitorApi/Program.cs` ya no afirma que `ConnectAsync` reintenta (lectura).
- [ ] Las frases "falla ese tick y el siguiente lo reemplaza" de los specs 14, 15 y 17 tienen su nota
      de corrección (grep).
- [ ] En vivo: Schedule actualizado con el timeout, 4xx sin reintento en el tick, timeout sin cortar
      el fan-out y corrida no determinística `Failed` con el tick siguiente corriendo (verificación
      manual, evidencia en `docs/e2e/spec-18-evidence.md`).
- [ ] `README.md`, `docker-compose.yml`, `CLAUDE.md` y `Construction.md` reflejan el cambio.

## Decisiones

- **Sí:** `FailureExceptionTypes` en `MonitorWorkflow` **y** `ExecutionTimeout` en el Schedule. El
  primero cierra el caso del deploy al instante; el segundo es la red para cualquier otro cuelgue.
- **No:** `WorkflowFailureExceptionTypes` a nivel worker. Fallaría también las entities, que nunca
  cierran y perderían su estado.
- **Sí:** `MONITOR_RUN_TIMEOUT_MINUTES` con default 15 (tres intervalos). **No:** derivarlo del
  intervalo ni dejarlo constante: configurable y explícito como las demás `MONITOR_*`.
- **Sí:** `ExecutionTimeout` en la acción. **No:** un `RunTimeout` aparte (sin Continue-As-New son
  equivalentes).
- **Sí:** un 4xx no se reintenta dentro del tick, pero la revisión no se reclama y el tick siguiente
  lo intenta una vez. Mantiene la regla del spec 14: un fallo nunca cuenta como enviado.
- **No:** reclamar la revisión como "rechazada". Perdería el aviso y cambiaría la regla del spec 14.
- **Sí:** no reintentable solo si **todos** los fallos lo son. Con uno reintentable mezclado, el
  reintento sigue siendo necesario para ese destino.
- **Sí:** el timeout del webhook se traduce a `TimeoutException` en `WebhookNotifier`, donde se conoce
  el `WebhookTimeout`. El compuesto solo deja pasar la cancelación de su propio `ct`.
- **Sí:** M-6 ⇒ `Unknown` solo con ejecuciones `Unknown` posteriores a la entrada a `Clean`: es
  exactamente el caso "ejecuciones nuevas que no se pudieron leer".
- **No:** `Unknown` con cualquier `IsTruncated`. En namespaces que siempre topean el listado, cada
  `Clean` que deriva pasaría a `Unknown` sin motivo nuevo.
- **No:** el arreglo literal de la revisión (no aplicar la regla con truncado y devolver la fase
  inferida). Puede devolver `Deprecated` sin ningún hecho nuevo, que es la regresión que M-6 evitaba.
- **Sí:** las correcciones de las auditorías a M-6, B-6 y B-8 (specs 15 y 16) se mantienen; la revisión
  las confirmó.

## Riesgos identificados

| Riesgo | Mitigación |
| --- | --- |
| Una corrida legítima más larga que 15 min queda cortada | Configurable; el tick siguiente retoma, el estado vive en las entities |
| Una corrida colgada en vuelo al desplegar este spec no hereda el timeout | Terminarla a mano una vez (`temporal workflow terminate`); documentado en el README |
| Un 4xx persistente golpea el webhook una vez por tick | Queda en `errors` y en `notificationsFailed` del run; corregir el destino lo resuelve |
| Al pasar a `Unknown` se pierde la memoria de `Clean` y una deriva posterior se lee `Deprecated` | Requiere ejecuciones sin leer y luego deriva de ventana; documentado, fuera de alcance |
| Cambiar los atributos de `MonitorWorkflow` con corridas en vuelo | Es metadata de la definición, no altera los comandos: no rompe el replay |

## Qué **no** está en este spec

- Autenticación de la API (M-1).
- Auto-versionado del `MonitorWorkflow` (spec 10).
- Reclamar revisiones rechazadas.
- No-determinismo como fallo en los entity workflows.
- Cambios a la inferencia de `Clean` del caso 3.

Cada uno de estos, si hace falta, va en su propio spec.
