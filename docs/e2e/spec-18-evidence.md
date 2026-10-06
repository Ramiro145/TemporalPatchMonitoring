# Evidencia e2e — spec 18 (huecos de la revisión del cierre de la auditoría)

**Fecha:** 2026-10-06
**Target:** Temporal de `ssy-yardflow` (`localhost:7233`), namespace observado `default`, namespace del
monitor `monitor`. Worker y API reconstruidos con el código del spec 18 (pasos 1 a 8).

**Montaje.** Overlay local `docker-compose.patchmonitor.yml` de `ssy-yardflow` (sin versionar, de otro
repo) con `NOTIFIER_WEBHOOK_URL=http://host.docker.internal:5299/hook`,
`NOTIFIER_WEBHOOK_TIMEOUT_SECONDS=5` y `NOTIFIER_MAX_ATTEMPTS=3`. Un webhook local con modo a demanda
(`ok` / `400` / `slow`, 30 s) y un worker desechable (`AuditE2EWorkflow`, patch `audit-e2e-v1`, cola
`audit-e2e-queue`) corrían fuera del repo y se eliminaron al terminar. Para el no-determinismo se usó
una copia del repo con un `Workflow.DelayAsync(1 s)` al inicio de `MonitorWorkflow`, en dos imágenes:
**control** (sin `FailureExceptionTypes`) y **fix** (con él). Ninguna toca el árbol del repo.

## Resultados

| Escenario | Comprobación | Resultado |
| --------- | ------------ | --------- |
| Schedule | Primer arranque del worker nuevo sobre el Schedule existente (creado antes del spec 18, sin timeout) | Log: `actualizado: difería de la configuración`. `temporal schedule describe`: `workflowExecutionTimeout: 900s`, `overlapPolicy: SKIP`, `catchupWindow: 600s`, nota y estado conservados |
| Schedule | Segundo arranque, sin cambios | Log: `ya existía y coincide con la configuración` (el `ExecutionTimeout` hace ida y vuelta por `DescribeAsync`) |
| 4xx | Webhook en `400`, tick que encuentra la revisión 4 pendiente | **1 solo intento** (antes eran 3), `notificationsFailed: 1`, error `webhook: El webhook rechazó la notificación con 400.` en `errors`; la corrida cierra en ~1,5 s |
| 4xx | Mismo caso, estado del entity | `notifiedRevision` quedó atrás de `revision` (no se reclamó) |
| 4xx | Webhook en `ok`, siguiente tick | Reenvía la revisión 4: `notificationsSent: 1`, `notifiedRevision: 4`; el tick posterior no repite |
| Timeout | Webhook en `slow` (30 s) con `NOTIFIER_WEBHOOK_TIMEOUT_SECONDS=5`, revisión 5 nueva | 3 intentos (`:17`, `:23`, `:30`), `notificationsFailed: 1`, error `webhook: El webhook no respondió dentro de 5 s.`; el notificador de log local recibió la revisión en cada intento |
| Corrida colgada (**control**, sin `FailureExceptionTypes`) | Corrida a mitad de actividad, worker matado, redeploy con un timer nuevo al inicio | `[TMPRL1100] Nondeterminism error` en el worker y la corrida queda `Running`. Un `POST /schedule/trigger` se descarta (`overlapSkipped` 19 → 20) |
| Corrida colgada (**control**) | Se espera sin intervenir | A los 15 min exactos la corrida queda `TimedOut` (`ExecutionTimeout`): el Schedule vuelve a correr |
| Corrida colgada (**fix**, con `FailureExceptionTypes`) | Mismo procedimiento | La corrida queda `Failed` ~25 s después del redeploy, con el mismo `[TMPRL1100]` |
| Corrida colgada (**fix**) | Tick siguiente | `Completed` a los 6 s, `overlapSkipped` sin cambio |
| Compatibilidad | Una corrida real de `MonitorWorkflow` abierta desde antes (worker caído) | La retomó el worker nuevo y cerró `Completed`: el replay de historias previas no se rompe |

## Observaciones

- El fan-out ante un timeout **no se distingue en vivo**: en el `CompositeNotifier` real el notificador
  de log va primero y el webhook último, así que el defecto viejo (el `OperationCanceledException`
  saltándose el `catch`) no omitía ningún destino en esta configuración. Esa parte la cubren los tests
  de `CompositeNotifierTests`. En vivo se confirma la clasificación: el timeout es un fallo reintentable
  del destino y el error queda con su motivo.
- Un apagado ordenado (`docker stop`) deja que el worker termine la corrida con el código viejo, así
  que no sirve para provocar el no-determinismo; hay que matar el proceso (`docker kill`).
- Con el control, el `ExecutionTimeout` es lo único que libera al Schedule sin intervención humana;
  con el fix, el no-determinismo falla de inmediato y el timeout queda como red de seguridad.
- Sin errores de no determinismo en las entities (no se tocaron); `/patches` siguió respondiendo.

## Conclusión

Los cuatro comportamientos del spec 18 se verifican en vivo: el Schedule se actualiza y converge, el
4xx no se reintenta dentro del tick y se reenvía en el siguiente, el timeout del webhook es un fallo
reintentable con motivo, y una corrida con replay roto falla (con el atributo) o se corta a los 15 min
(sin él) en vez de bloquear al Schedule. El caso M-6 con ejecuciones `Unknown` posteriores queda
cubierto solo por tests: no hubo forma de provocar una historia ilegible en este cluster.
